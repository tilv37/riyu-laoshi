using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RiyuLaoshi.Services;

// LLM 调用失败时抛出的业务异常，错误信息会直接展示给用户
public sealed class LlmException(string message) : Exception(message);

// OpenAI 兼容接口封装：模型列表、非流式补全、SSE 流式补全
public sealed class LlmClient(HttpClient http)
{
    // 把用户填的 Base URL 统一拼成 /chat/completions
    public static string BuildUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            throw new LlmException("未配置接口地址 BaseUrl");
        }

        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    // 把 Base URL 统一拼成 /models；若填的是完整聊天地址则自动去掉尾部
    public static string BuildModelsUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            throw new LlmException("未配置接口地址 BaseUrl");
        }

        const string chatSuffix = "/chat/completions";
        if (url.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^chatSuffix.Length].TrimEnd('/');
        }

        return url.EndsWith("/models", StringComparison.OrdinalIgnoreCase) ? url : url + "/models";
    }

    // 拉取可用模型列表，兼容 data[]/models[]/根数组三种返回格式
    public async Task<ModelListDto> ListModelsAsync(SettingsDto settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return new ModelListDto(false, "未配置接口地址 BaseUrl", new List<string>());
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUrl(settings.BaseUrl));
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelListDto(
                    false,
                    $"接口返回 HTTP {(int)response.StatusCode}: {Truncate(body, 300)}",
                    new List<string>());
            }

            var models = ParseModelIds(body);
            return models.Count == 0
                ? new ModelListDto(false, "接口未返回模型列表，可手动输入模型名", new List<string>())
                : new ModelListDto(true, $"已获取 {models.Count} 个模型", models);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ModelListDto(false, "获取模型超时（20 秒）", new List<string>());
        }
        catch (Exception ex)
        {
            return new ModelListDto(false, ex.Message, new List<string>());
        }
    }

    private static List<string> ParseModelIds(string body)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            JsonElement items;

            if (root.ValueKind == JsonValueKind.Array)
            {
                items = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var data) &&
                     data.ValueKind == JsonValueKind.Array)
            {
                items = data;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("models", out var models) &&
                     models.ValueKind == JsonValueKind.Array)
            {
                items = models;
            }
            else
            {
                return new List<string>();
            }

            foreach (var item in items.EnumerateArray())
            {
                string? id = null;
                if (item.ValueKind == JsonValueKind.String)
                {
                    id = item.GetString();
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        id = idProp.GetString();
                    }
                    else if (item.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    {
                        id = nameProp.GetString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add(id.Trim());
                }
            }
        }
        catch (JsonException)
        {
            // 响应结构不符合预期时按空列表处理，交给上层提示用户手动输入
        }

        return result
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
    }

    // 非流式请求：返回正文与 token 用量，用于标题总结、错题整理和连接测试
    public async Task<(string Content, int Tokens)> CompleteAsync(
        SettingsDto settings,
        IReadOnlyList<LlmMessage> messages,
        CancellationToken ct,
        double? temperature = null,
        string? model = null,
        string? reasoningEffort = null)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? (settings.Model ?? "").Trim() : model.Trim();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new LlmException("未配置模型 Model");
        }

        var payload = new LlmRequest
        {
            Model = modelName,
            Messages = messages.ToList(),
            Stream = false,
            Temperature = temperature ?? settings.Temperature,
            ReasoningEffort = NormalizeReasoningEffort(reasoningEffort)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(settings.BaseUrl));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        request.Content = JsonContent.Create(payload, AppJsonContext.Default.LlmRequest);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmException($"接口返回 HTTP {(int)response.StatusCode}: {Truncate(body, 600)}");
        }

        var parsed = JsonSerializer.Deserialize(body, AppJsonContext.Default.LlmResponse)
                     ?? throw new LlmException("接口响应无法解析");
        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        var tokens = parsed.Usage?.TotalTokens ?? 0;
        return (content, tokens);
    }

    // 流式请求：逐块 yield 文本增量，供聊天界面打字机渲染
    public async IAsyncEnumerable<string> StreamAsync(
        SettingsDto settings,
        IReadOnlyList<LlmMessage> messages,
        [EnumeratorCancellation] CancellationToken ct,
        string? model = null,
        string? reasoningEffort = null)
    {
        var modelName = string.IsNullOrWhiteSpace(model) ? (settings.Model ?? "").Trim() : model.Trim();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new LlmException("未配置模型 Model");
        }

        var payload = new LlmRequest
        {
            Model = modelName,
            Messages = messages.ToList(),
            Stream = true,
            Temperature = settings.Temperature,
            ReasoningEffort = NormalizeReasoningEffort(reasoningEffort)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(settings.BaseUrl));
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Content = JsonContent.Create(payload, AppJsonContext.Default.LlmRequest);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new LlmException($"接口返回 HTTP {(int)response.StatusCode}: {Truncate(errorBody, 600)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                break;
            }

            var delta = ParseStreamDelta(data);
            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    // 解析一条 SSE data；忽略心跳/非 JSON 行，遇到上游错误则抛异常
    private static string? ParseStreamDelta(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
                throw new LlmException($"接口返回错误：{Truncate(message ?? "未知错误", 600)}");
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            // 部分供应商会发心跳或非 JSON 行，直接忽略
        }

        return null;
    }

    internal static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // 思考强度只发送白名单值，未配置时不写该字段（避免兼容问题）
    private static string? NormalizeReasoningEffort(string? value)
    {
        var normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" ? normalized : null;
    }
}
