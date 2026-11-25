using System.Text;
using System.Text.Json;
using RiyuLaoshi.Data;

namespace RiyuLaoshi.Services;

// 错题本整理流水线：切片 → 逐片提炼(Map) → 合并去重(Reduce) → 落库
public sealed class MemoPipeline(
    Db db,
    SettingsService settingsService,
    LlmClient llm,
    ILogger<MemoPipeline> logger)
{
    // 合并阶段的固定提示词：同一知识点只留一条，分类仍限定单词/语法
    private const string ReduceSystemPrompt =
        "你是日语考试错题本的知识点合并助手。下面是同一会话多个片段提炼出的知识点 JSON 数组。\n" +
        "请完成：1) 合并重复的单词或语法点，同一知识点只保留一条；2) 合并标签；3) 删除空泛或不属于单词/语法的条目；4) category 只能是「单词」或「语法」。\n" +
        "保留所有不重复的有效知识点，不要丢内容。只输出 JSON 数组，结构不变，不要输出任何其他文字或代码围栏。";

    /// <summary>
    /// 执行完整整理流程，返回 succeeded/empty；失败直接抛异常，由调用方标记任务失败。
    /// </summary>
    public async Task<string> RunAsync(long jobId, CancellationToken ct)
    {
        var job = db.GetJob(jobId) ?? throw new InvalidOperationException("任务不存在");
        var session = db.GetSession(job.SessionId) ?? throw new InvalidOperationException("来源会话不存在");
        var messages = db.GetMessages(job.SessionId);
        var settings = settingsService.Get();
        var memoModel = SettingsService.ResolveModel(settings.MemoModel, settings.Model);
        if (string.IsNullOrWhiteSpace(memoModel))
        {
            throw new InvalidOperationException("未配置模型：请设置默认模型或错题本整理模型");
        }

        db.AddJobLog(
            jobId,
            "info",
            "start",
            $"开始整理会话「{session.Title}」，共 {messages.Count} 条消息，使用模型 {memoModel}");
        if (messages.Count == 0)
        {
            db.UpdateMemoStatus(session.Id, "empty");
            db.AddJobLog(jobId, "info", "result", "会话没有消息，无需整理");
            return "empty";
        }

        // 把消息拼成带轮次标记的文本，再按字符数切片，避免单次请求超出上下文
        var transcript = BuildTranscript(messages);
        var chunks = ChunkText(transcript, settings.ChunkChars, settings.ChunkOverlap);
        db.SetJobTotal(jobId, chunks.Count);
        db.AddJobLog(jobId, "info", "chunk", $"会话全文 {transcript.Length} 字，切分为 {chunks.Count} 片");

        var drafts = new List<MemoDraft>();
        for (var i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            db.AddJobLog(jobId, "info", $"chunk_{i + 1}", $"处理第 {i + 1}/{chunks.Count} 片（{chunks[i].Length} 字）");

            var mapMessages = new List<LlmMessage>
            {
                new() { Role = "system", Content = settings.MemoSystemPrompt },
                new()
                {
                    Role = "user",
                    Content = $"以下是一个归档会话的第 {i + 1}/{chunks.Count} 个片段。请按规则提炼单词与语法知识点。\n\n{chunks[i]}"
                }
            };

            var (content, tokens) = await llm.CompleteAsync(settings, mapMessages, ct, 0.2, memoModel);
            var parsed = ParseDrafts(content);
            drafts.AddRange(parsed);
            db.SetJobProgress(jobId, i + 1);
            db.AddJobLog(
                jobId,
                "info",
                $"chunk_{i + 1}",
                $"第 {i + 1} 片完成，提取 {parsed.Count} 条知识点",
                tokens);
        }

        // 多片才需要 Reduce；输入过长或调用失败时降级为本地去重
        if (chunks.Count > 1 && drafts.Count > 1)
        {
            ct.ThrowIfCancellationRequested();
            drafts = await ReduceAsync(jobId, settings, memoModel, drafts, ct);
        }

        var final = Deduplicate(drafts);
        ct.ThrowIfCancellationRequested();

        // 事务替换该会话的旧错题，保证重试不会产生重复数据
        var saved = db.ReplaceMemosForSession(session.Id, final);
        var status = saved == 0 ? "empty" : "succeeded";
        db.UpdateMemoStatus(session.Id, status);
        db.AddJobLog(
            jobId,
            "info",
            "persist",
            saved == 0
                ? "本次整理未发现可收录的单词或语法知识点"
                : $"已写入 {saved} 条错题知识点");
        return status;
    }

    private async Task<List<MemoDraft>> ReduceAsync(
        long jobId,
        SettingsDto settings,
        string memoModel,
        List<MemoDraft> drafts,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(drafts, AppJsonContext.Default.ListMemoDraft);
        if (payload.Length > 150_000)
        {
            db.AddJobLog(jobId, "warn", "reduce", $"合并输入过长（{payload.Length} 字），跳过 LLM 合并，改用本地去重");
            return NaiveMerge(drafts);
        }

        db.AddJobLog(jobId, "info", "reduce", $"开始合并 {drafts.Count} 条候选知识点");
        try
        {
            var messages = new List<LlmMessage>
            {
                new() { Role = "system", Content = ReduceSystemPrompt },
                new() { Role = "user", Content = payload }
            };

            var (content, tokens) = await llm.CompleteAsync(settings, messages, ct, 0.2, memoModel);
            var merged = ParseDrafts(content);
            db.AddJobLog(jobId, "info", "reduce", $"合并完成，得到 {merged.Count} 条知识点", tokens);
            return merged;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "任务 {JobId} 的 LLM 合并失败，改用本地去重", jobId);
            db.AddJobLog(jobId, "warn", "reduce", $"LLM 合并失败，改用本地去重：{ex.Message}");
            return NaiveMerge(drafts);
        }
    }

    // 从模型输出中截取 JSON 数组并校验字段，过滤非法分类与空内容
    private List<MemoDraft> ParseDrafts(string raw)
    {
        var json = ExtractJsonArray(raw)
                   ?? throw new InvalidOperationException($"模型输出不是 JSON 数组：{LlmClient.Truncate(raw, 400)}");

        List<MemoDraft>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListMemoDraft);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"模型输出的 JSON 无法解析：{LlmClient.Truncate(raw, 400)}", ex);
        }

        if (parsed is null)
        {
            return new List<MemoDraft>();
        }

        var result = new List<MemoDraft>();
        foreach (var draft in parsed)
        {
            var title = (draft.Title ?? "").Trim();
            var category = MemoCategories.Normalize(draft.Category);
            var summary = (draft.SummaryMd ?? "").Trim();
            if (title.Length == 0 || category is null || summary.Length == 0)
            {
                continue;
            }

            var tags = NormalizeTags(draft.Tags);
            result.Add(new MemoDraft
            {
                Title = LlmClient.Truncate(title, 100),
                Category = category,
                Tags = tags,
                TagsText = string.Join(", ", tags),
                SummaryMd = LlmClient.Truncate(summary, 20_000)
            });
        }

        return result;
    }

    private static List<string> NormalizeTags(List<string>? tags)
    {
        if (tags is null)
        {
            return new List<string>();
        }

        return tags
            .Select(t => (t ?? "").Trim().TrimStart('#'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static string? ExtractJsonArray(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return null;
        }

        return raw[start..(end + 1)];
    }

    // 本地兜底去重：按“分类 + 规范化标题”合并标签，保留更长的正文
    private static List<MemoDraft> NaiveMerge(IEnumerable<MemoDraft> drafts)
    {
        var result = new List<MemoDraft>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var draft in drafts)
        {
            var key = $"{draft.Category}|{NormalizeKey(draft.Title)}";
            if (index.TryGetValue(key, out var existingIndex))
            {
                var existing = result[existingIndex];
                var tags = existing.TagsText.Length == 0
                    ? draft.TagsText
                    : string.Join(", ", (existing.TagsText + ", " + draft.TagsText)
                        .Split(", ", StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                existing.TagsText = tags;
                existing.Tags = tags.Split(", ", StringSplitOptions.RemoveEmptyEntries).ToList();
                if (draft.SummaryMd.Length > existing.SummaryMd.Length)
                {
                    existing.SummaryMd = draft.SummaryMd;
                }
            }
            else
            {
                index[key] = result.Count;
                result.Add(new MemoDraft
                {
                    Title = draft.Title,
                    Category = draft.Category,
                    Tags = draft.Tags?.ToList() ?? new List<string>(),
                    TagsText = draft.TagsText,
                    SummaryMd = draft.SummaryMd
                });
            }
        }

        return result.Take(200).ToList();
    }

    private static List<MemoDraft> Deduplicate(List<MemoDraft> drafts)
    {
        var merged = NaiveMerge(drafts);
        return merged
            .Where(d => MemoCategories.IsValid(d.Category) && d.Title.Length > 0 && d.SummaryMd.Length > 0)
            .ToList();
    }

    private static string NormalizeKey(string title)
    {
        var chars = title
            .Where(c => !char.IsWhiteSpace(c) && c is not ('。' or '、' or '，' or ',' or '.' or '：' or ':' or '「' or '」' or '『' or '』'))
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static string BuildTranscript(List<MessageDto> messages)
    {
        var builder = new StringBuilder();
        var round = 0;
        foreach (var message in messages)
        {
            if (message.Role == "user")
            {
                round++;
                builder.Append("【第 ").Append(round).Append(" 轮 · 用户】\n");
            }
            else
            {
                builder.Append("【助手】\n");
            }

            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString();
    }

    // 按字符切片并保留重叠，切点避开 UTF-16 代理对，防止半个 emoji 被截断
    private static List<string> ChunkText(string text, int chunkChars, int overlap)
    {
        var size = Math.Max(500, chunkChars);
        var step = Math.Max(1, size - Math.Clamp(overlap, 0, size / 2));
        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = Math.Min(size, text.Length - start);
            if (start + length < text.Length && length > 0 && char.IsHighSurrogate(text[start + length - 1]))
            {
                length--;
            }

            if (length <= 0)
            {
                length = Math.Min(size, text.Length - start);
            }

            chunks.Add(text.Substring(start, length));
            if (start + length >= text.Length)
            {
                break;
            }

            start += step;
        }

        if (chunks.Count == 0)
        {
            chunks.Add(text);
        }

        return chunks;
    }
}
