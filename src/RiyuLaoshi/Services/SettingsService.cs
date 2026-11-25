using RiyuLaoshi.Data;

namespace RiyuLaoshi.Services;

// 错题本分类固定为「单词 / 语法」两类，兼容模型常见的别名写法
public static class MemoCategories
{
    public const string Word = "单词";
    public const string Grammar = "语法";

    public static bool IsValid(string category) => category is Word or Grammar;

    public static string? Normalize(string? raw)
    {
        var value = (raw ?? "").Trim();
        return value switch
        {
            "单词" or "词汇" or "単語" or "vocabulary" or "Vocabulary" => Word,
            "语法" or "文法" or "grammar" or "Grammar" => Grammar,
            _ => null
        };
    }
}

// 默认提示词与配置值；新增键后会在启动时 INSERT OR IGNORE 补齐到已有数据库
public static class SettingsDefaults
{
    public const string ChatSystemPrompt =
        "你是一位专业、耐心的日语老师，面向准备日语考试的学习者。用简体中文解释，必要时给出日语例句和中文翻译。回答要准确、条理清晰，始终围绕日语学习。不要在汉字后标注假名读音。如果用户的问题与日语学习无关，礼貌地说明你只回答日语相关问题。";

    public const string TitleSystemPrompt =
        "你是一个会话标题生成器。根据用户提供的日语问答内容，生成一个不超过15个字的简洁标题，只输出标题本身，不要引号、句号或任何解释。";

    public const string MemoSystemPrompt =
        "你是日语考试复习错题本的整理助手。用户会给你一段日语学习问答的对话片段。\n" +
        "你的任务：只提炼其中与日语考试相关的「单词」和「语法」知识点。\n\n" +
        "硬性规则：\n" +
        "1. category 只能是「单词」或「语法」二者之一，不允许其他分类。\n" +
        "2. 单词条目：给出词形、读音（假名）、释义、典型例句，以及易错/易混淆提示（如果有）。\n" +
        "3. 语法条目：给出接续、意义、典型例句，以及易错/易混淆提示（如果有）。\n" +
        "4. 优先提炼用户提问中暴露出的薄弱点、易错点和易混淆点。\n" +
        "5. 发音、文化、常识、寒暄、闲聊、学习方法等非考试内容一律丢弃。\n" +
        "6. 不要编造对话中没有依据的知识点；可以补充必要的正确解释。\n" +
        "7. 如果片段中没有可提炼的单词或语法知识点，输出空数组 []。\n\n" +
        "输出格式：只输出 JSON 数组，不要输出任何其他文字、解释或代码围栏。每个元素格式：\n" +
        "{\"title\":\"简短标题\",\"category\":\"单词或语法\",\"tags\":[\"标签1\",\"标签2\"],\"summary_md\":\"Markdown 正文\"}";

    public static readonly (string Key, string Value)[] All =
    {
        ("base_url", "https://api.openai.com/v1"),
        ("api_key", ""),
        ("model", ""),
        ("chat_model", ""),
        ("memo_model", ""),
        ("title_model", ""),
        ("reasoning_effort", ""),
        ("temperature", "0.7"),
        ("chat_system_prompt", ChatSystemPrompt),
        ("memo_system_prompt", MemoSystemPrompt),
        ("title_system_prompt", TitleSystemPrompt),
        ("default_context_rounds", "5"),
        ("default_include_context", "true"),
        ("auto_archive_rounds", "100"),
        ("auto_archive_chars", "100000"),
        ("chunk_chars", "20000"),
        ("chunk_overlap", "1000")
    };
}

// 配置的读取、校验与持久化；所有数值在这里统一收敛到安全范围
public sealed class SettingsService(Db db)
{
    // 场景模型留空时回退到默认模型，方便只给部分场景指定便宜模型
    public static string ResolveModel(string? scenarioModel, string defaultModel) =>
        string.IsNullOrWhiteSpace(scenarioModel) ? (defaultModel ?? "").Trim() : scenarioModel.Trim();

    // 从 SQLite 读取键值配置；空白值视为未配置，回退到内置默认值
    public SettingsDto Get()
    {
        var values = db.GetSettings();
        string Raw(string key, string fallback) =>
            values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
        string Optional(string key) =>
            values.TryGetValue(key, out var v) ? v.Trim() : "";

        return new SettingsDto(
            BaseUrl: Raw("base_url", "https://api.openai.com/v1"),
            ApiKey: values.GetValueOrDefault("api_key", ""),
            Model: values.GetValueOrDefault("model", ""),
            ChatModel: Optional("chat_model"),
            MemoModel: Optional("memo_model"),
            TitleModel: Optional("title_model"),
            ReasoningEffort: Optional("reasoning_effort"),
            Temperature: ParseDouble(Raw("temperature", "0.7"), 0.7),
            ChatSystemPrompt: Raw("chat_system_prompt", SettingsDefaults.ChatSystemPrompt),
            MemoSystemPrompt: Raw("memo_system_prompt", SettingsDefaults.MemoSystemPrompt),
            TitleSystemPrompt: Raw("title_system_prompt", SettingsDefaults.TitleSystemPrompt),
            DefaultContextRounds: ParseInt(Raw("default_context_rounds", "5"), 5),
            DefaultIncludeContext: Raw("default_include_context", "true") == "true",
            AutoArchiveRounds: ParseInt(Raw("auto_archive_rounds", "100"), 100),
            AutoArchiveChars: ParseInt(Raw("auto_archive_chars", "100000"), 100000),
            ChunkChars: ParseInt(Raw("chunk_chars", "20000"), 20000),
            ChunkOverlap: ParseInt(Raw("chunk_overlap", "1000"), 1000));
    }

    // 保存前做归一化：裁剪空白、限制数值范围、校验思考强度枚举
    public SettingsDto Save(SettingsDto dto)
    {
        var chunkChars = Math.Clamp(dto.ChunkChars, 1000, 500_000);
        var chunkOverlap = Math.Clamp(dto.ChunkOverlap, 0, Math.Max(0, chunkChars / 2));
        var normalized = dto with
        {
            BaseUrl = (dto.BaseUrl ?? "").Trim(),
            ApiKey = (dto.ApiKey ?? "").Trim(),
            Model = (dto.Model ?? "").Trim(),
            ChatModel = (dto.ChatModel ?? "").Trim(),
            MemoModel = (dto.MemoModel ?? "").Trim(),
            TitleModel = (dto.TitleModel ?? "").Trim(),
            ReasoningEffort = NormalizeReasoningEffort(dto.ReasoningEffort),
            Temperature = Math.Clamp(dto.Temperature, 0, 2),
            ChatSystemPrompt = dto.ChatSystemPrompt ?? "",
            MemoSystemPrompt = dto.MemoSystemPrompt ?? "",
            TitleSystemPrompt = dto.TitleSystemPrompt ?? "",
            DefaultContextRounds = Math.Clamp(dto.DefaultContextRounds, 0, 100),
            AutoArchiveRounds = Math.Max(1, dto.AutoArchiveRounds),
            AutoArchiveChars = Math.Max(100, dto.AutoArchiveChars),
            ChunkChars = chunkChars,
            ChunkOverlap = chunkOverlap
        };

        db.SetSettings(new Dictionary<string, string>
        {
            ["base_url"] = normalized.BaseUrl,
            ["api_key"] = normalized.ApiKey,
            ["model"] = normalized.Model,
            ["chat_model"] = normalized.ChatModel,
            ["memo_model"] = normalized.MemoModel,
            ["title_model"] = normalized.TitleModel,
            ["reasoning_effort"] = normalized.ReasoningEffort,
            ["temperature"] = normalized.Temperature.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            ["chat_system_prompt"] = normalized.ChatSystemPrompt,
            ["memo_system_prompt"] = normalized.MemoSystemPrompt,
            ["title_system_prompt"] = normalized.TitleSystemPrompt,
            ["default_context_rounds"] = normalized.DefaultContextRounds.ToString(),
            ["default_include_context"] = normalized.DefaultIncludeContext ? "true" : "false",
            ["auto_archive_rounds"] = normalized.AutoArchiveRounds.ToString(),
            ["auto_archive_chars"] = normalized.AutoArchiveChars.ToString(),
            ["chunk_chars"] = normalized.ChunkChars.ToString(),
            ["chunk_overlap"] = normalized.ChunkOverlap.ToString()
        });

        return Get();
    }

    private static int ParseInt(string raw, int fallback) =>
        int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    // 只接受 OpenAI 风格的 low/medium/high，其他值一律视为不发送
    private static string NormalizeReasoningEffort(string? raw)
    {
        var value = (raw ?? "").Trim().ToLowerInvariant();
        return value is "low" or "medium" or "high" ? value : "";
    }

    private static double ParseDouble(string raw, double fallback) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;
}
