using System.Text;
using System.Text.Json;
using RiyuLaoshi.Data;

namespace RiyuLaoshi.Services;

// 聊天主流程：校验会话 → 组装上下文 → 流式转发 → 落库 → 自动归档/标题
public sealed class ChatService(
    Db db,
    SettingsService settingsService,
    LlmClient llm,
    ILogger<ChatService> logger)
{
    // 统一设置 SSE 响应头；异常转成 error 事件，客户端断开则中止上游请求
    public async Task HandleAsync(HttpContext ctx, ChatRequest request, bool regenerate)
    {
        var ct = ctx.RequestAborted;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            await HandleCoreAsync(ctx, request, regenerate, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("客户端断开，已中止会话 {SessionId} 的流式请求", request.SessionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "聊天请求失败，会话 {SessionId}", request.SessionId);
            await TrySendAsync(ctx, new SsePayload("error", Error: ex.Message), CancellationToken.None);
        }
    }

    // 正常发送与“重新生成”共用该流程，区别只在上下文与落库方式
    private async Task HandleCoreAsync(HttpContext ctx, ChatRequest request, bool regenerate, CancellationToken ct)
    {
        var session = db.GetSession(request.SessionId);
        if (session is null)
        {
            await SendAsync(ctx, new SsePayload("error", Error: "会话不存在"), ct);
            return;
        }

        if (session.Archived)
        {
            await SendAsync(ctx, new SsePayload("error", Error: "该会话已归档，无法继续对话"), ct);
            return;
        }

        var settings = settingsService.Get();
        var chatModel = SettingsService.ResolveModel(settings.ChatModel, settings.Model);
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(chatModel))
        {
            await SendAsync(ctx, new SsePayload("error", Error: "请先在设置中配置接口地址与聊天模型"), ct);
            return;
        }

        var allMessages = db.GetMessages(session.Id);
        string question;
        long? assistantIdToUpdate = null;
        List<MessageDto> historySource;

        if (regenerate)
        {
            var lastAssistant = allMessages.LastOrDefault(m => m.Role == "assistant");
            if (lastAssistant is null || allMessages.Count == 0 || allMessages[^1].Role != "assistant")
            {
                await SendAsync(ctx, new SsePayload("error", Error: "没有可重新生成的回答"), ct);
                return;
            }

            var lastUserIndex = -1;
            for (var i = allMessages.Count - 1; i >= 0; i--)
            {
                if (allMessages[i].Role == "user")
                {
                    lastUserIndex = i;
                    break;
                }
            }

            if (lastUserIndex < 0)
            {
                await SendAsync(ctx, new SsePayload("error", Error: "没有可重新生成的回答"), ct);
                return;
            }

            question = allMessages[lastUserIndex].Content;
            assistantIdToUpdate = lastAssistant.Id;
            historySource = allMessages.Take(lastUserIndex).ToList();
        }
        else
        {
            question = (request.Message ?? "").Trim();
            if (question.Length == 0)
            {
                await SendAsync(ctx, new SsePayload("error", Error: "消息不能为空"), ct);
                return;
            }

            if (question.Length > 100_000)
            {
                await SendAsync(ctx, new SsePayload("error", Error: "消息过长，请拆分后再发送"), ct);
                return;
            }

            historySource = allMessages;
        }

        var includeContext = request.IncludeContext ?? settings.DefaultIncludeContext;
        var rounds = Math.Clamp(request.ContextRounds ?? settings.DefaultContextRounds, 0, 100);

        var history = new List<MessageDto>();
        if (includeContext && rounds > 0 && historySource.Count > 0)
        {
            var take = Math.Min(rounds * 2, historySource.Count);
            history = historySource.Skip(historySource.Count - take).ToList();
            if (history.Count > 0 && history[0].Role != "user")
            {
                history.RemoveAt(0);
            }
        }

        var llmMessages = new List<LlmMessage>();
        if (!string.IsNullOrWhiteSpace(settings.ChatSystemPrompt))
        {
            llmMessages.Add(new LlmMessage { Role = "system", Content = settings.ChatSystemPrompt });
        }

        foreach (var message in history)
        {
            llmMessages.Add(new LlmMessage
            {
                Role = message.Role == "assistant" ? "assistant" : "user",
                Content = message.Content
            });
        }

        llmMessages.Add(new LlmMessage { Role = "user", Content = question });

        var answer = new StringBuilder();
        await foreach (var delta in llm.StreamAsync(settings, llmMessages, ct, chatModel, settings.ReasoningEffort))
        {
            answer.Append(delta);
            await SendAsync(ctx, new SsePayload("delta", Delta: delta), ct);
        }

        if (answer.Length == 0)
        {
            throw new LlmException("模型返回了空内容");
        }

        var fullAnswer = answer.ToString();
        long assistantId;
        // 新消息事务插入；重新生成则原地更新最后一条回答，避免重复历史
        if (regenerate && assistantIdToUpdate is not null)
        {
            db.UpdateMessageContent(assistantIdToUpdate.Value, fullAnswer);
            assistantId = assistantIdToUpdate.Value;
        }
        else
        {
            var ids = db.InsertExchange(session.Id, question, fullAnswer);
            assistantId = ids.AssistantId;
        }

        var (roundCount, totalChars) = db.GetSessionStats(session.Id);
        var archivedNow = false;
        if (!session.Archived &&
            (roundCount >= settings.AutoArchiveRounds || totalChars >= settings.AutoArchiveChars))
        {
            archivedNow = db.ArchiveSession(session.Id);
            if (archivedNow)
            {
                logger.LogInformation(
                    "会话 {SessionId} 已自动归档（{Rounds} 轮 / {Chars} 字）",
                    session.Id, roundCount, totalChars);
            }
        }

        if (roundCount == 1 && (session.Title == "新会话" || string.IsNullOrWhiteSpace(session.Title)))
        {
            ScheduleTitleGeneration(session.Id, question, fullAnswer);
        }

        await SendAsync(
            ctx,
            new SsePayload(
                "done",
                AssistantMessageId: assistantId,
                SessionId: session.Id,
                Archived: archivedNow),
            ct);
    }

    // 首轮结束后异步总结标题：失败时用问题前 20 字兜底，不阻塞回答
    private void ScheduleTitleGeneration(long sessionId, string question, string answer)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var settings = settingsService.Get();
                var titleModel = SettingsService.ResolveModel(settings.TitleModel, settings.Model);
                if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(titleModel))
                {
                    return;
                }

                var messages = new List<LlmMessage>
                {
                    new() { Role = "system", Content = settings.TitleSystemPrompt },
                    new()
                    {
                        Role = "user",
                        Content = $"用户问题：{Truncate(question, 600)}\n\n助手回答：{Truncate(answer, 1200)}"
                    }
                };

                var (raw, _) = await llm.CompleteAsync(settings, messages, CancellationToken.None, 0.2, titleModel);
                var title = CleanTitle(raw);
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = FallbackTitle(question);
                }

                db.TrySetAutoTitle(sessionId, title);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "为会话 {SessionId} 自动生成标题失败，使用兜底标题", sessionId);
                db.TrySetAutoTitle(sessionId, FallbackTitle(question));
            }
        });
    }

    internal static string FallbackTitle(string question)
    {
        var firstLine = (question ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (firstLine.Length == 0)
        {
            return "新会话";
        }

        return firstLine.Length <= 20 ? firstLine : firstLine[..20];
    }

    internal static string CleanTitle(string raw)
    {
        var title = (raw ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (title.Length == 0)
        {
            return "";
        }

        var lines = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        title = string.Join(" ", lines).Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', '。', '.', '：', ':');
        if (title.StartsWith("标题", StringComparison.Ordinal))
        {
            var colon = title.IndexOfAny(new[] { '：', ':' });
            if (colon >= 0)
            {
                title = title[(colon + 1)..].Trim();
            }
        }

        return title.Length <= 20 ? title : title[..20];
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // 写出一条 SSE data 事件并立即刷新缓冲区
    private static async Task SendAsync(HttpContext ctx, SsePayload payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, AppJsonContext.Default.SsePayload);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static async Task TrySendAsync(HttpContext ctx, SsePayload payload, CancellationToken ct)
    {
        try
        {
            await SendAsync(ctx, payload, ct);
        }
        catch
        {
            // 响应可能已断开，忽略写入失败
        }
    }
}
