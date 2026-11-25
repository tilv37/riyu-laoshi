using System.Text.Json.Serialization;

namespace RiyuLaoshi;

// ---------- HTTP 接口 DTO ----------

public sealed record ChatRequest(
    long SessionId,
    string Message,
    int? ContextRounds,
    bool? IncludeContext);

public sealed record RegenerateRequest(
    long SessionId,
    int? ContextRounds,
    bool? IncludeContext);

public sealed record RenameRequest(string? Title);

public sealed record SessionDto(
    long Id,
    string Title,
    bool Archived,
    string MemoStatus,
    string CreatedAt,
    string? ArchivedAt);

public sealed record MessageDto(
    long Id,
    string Role,
    string Content,
    string CreatedAt);

public sealed record SessionDetailDto(SessionDto Session, List<MessageDto> Messages);

public sealed record CreateSessionResponse(long Id, string Title);

// 全部可配置项一次传给前端；空字符串表示“使用默认值/回退值”
public sealed record SettingsDto(
    string BaseUrl,
    string ApiKey,
    string Model,
    string ChatModel,
    string MemoModel,
    string TitleModel,
    string ReasoningEffort,
    double Temperature,
    string ChatSystemPrompt,
    string MemoSystemPrompt,
    string TitleSystemPrompt,
    int DefaultContextRounds,
    bool DefaultIncludeContext,
    int AutoArchiveRounds,
    int AutoArchiveChars,
    int ChunkChars,
    int ChunkOverlap);

public sealed record TestResultDto(bool Ok, string Message, long? LatencyMs);

public sealed record ModelListDto(bool Ok, string Message, List<string> Models);

public sealed record SsePayload(
    string Type,
    string? Delta = null,
    long? AssistantMessageId = null,
    bool? Archived = null,
    long? SessionId = null,
    string? Error = null);

public sealed record MemoDto(
    long Id,
    long? SourceSessionId,
    string Title,
    string Category,
    string Tags,
    string SummaryMd,
    string MyNote,
    string CreatedAt,
    string UpdatedAt);

public sealed record MemoUpdateRequest(string? Category, string? Tags, string? MyNote);

public sealed record MemoJobDto(
    long Id,
    long SessionId,
    string? SessionTitle,
    string Status,
    int TotalChunks,
    int DoneChunks,
    string? Error,
    int RetryCount,
    string CreatedAt,
    string? FinishedAt);

public sealed record MemoJobLogDto(
    long Id,
    string Level,
    string Step,
    string Message,
    int? TokensUsed,
    string CreatedAt);

public sealed record MemoJobTriggerResponse(long JobId, string Status);

// ---------- LLM 请求/响应 DTO，JSON 字段名对齐 OpenAI 兼容接口 ----------

public sealed class LlmMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public sealed class LlmRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<LlmMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; set; }
}

public sealed class LlmResponse
{
    [JsonPropertyName("choices")]
    public List<LlmChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public LlmUsage? Usage { get; set; }
}

public sealed class LlmChoice
{
    [JsonPropertyName("message")]
    public LlmResponseMessage? Message { get; set; }
}

public sealed class LlmResponseMessage
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

public sealed class LlmUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

// ---------- LLM 提炼出的错题知识点草稿 ----------

public sealed class MemoDraft
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("summary_md")]
    public string SummaryMd { get; set; } = "";

    [JsonIgnore]
    public string TagsText { get; set; } = "";
}

// ---------- AOT 所需的 JSON 源生成上下文，新增 DTO 必须在此登记 ----------

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ChatRequest))]
[JsonSerializable(typeof(RegenerateRequest))]
[JsonSerializable(typeof(RenameRequest))]
[JsonSerializable(typeof(SessionDto))]
[JsonSerializable(typeof(List<SessionDto>))]
[JsonSerializable(typeof(MessageDto))]
[JsonSerializable(typeof(List<MessageDto>))]
[JsonSerializable(typeof(SessionDetailDto))]
[JsonSerializable(typeof(CreateSessionResponse))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(TestResultDto))]
[JsonSerializable(typeof(ModelListDto))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(SsePayload))]
[JsonSerializable(typeof(MemoDto))]
[JsonSerializable(typeof(List<MemoDto>))]
[JsonSerializable(typeof(MemoUpdateRequest))]
[JsonSerializable(typeof(MemoJobDto))]
[JsonSerializable(typeof(List<MemoJobDto>))]
[JsonSerializable(typeof(MemoJobLogDto))]
[JsonSerializable(typeof(List<MemoJobLogDto>))]
[JsonSerializable(typeof(MemoJobTriggerResponse))]
[JsonSerializable(typeof(LlmRequest))]
[JsonSerializable(typeof(LlmResponse))]
[JsonSerializable(typeof(MemoDraft))]
[JsonSerializable(typeof(List<MemoDraft>))]
public partial class AppJsonContext : JsonSerializerContext
{
}
