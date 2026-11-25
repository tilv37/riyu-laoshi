using System.Diagnostics;
using RiyuLaoshi.Services;

namespace RiyuLaoshi.Endpoints;

// 设置接口：读取/保存配置、测试连接、拉取可用模型
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        // 测试连接使用“聊天场景”的有效模型与思考强度，贴近真实调用
        app.MapGet("/api/settings", (SettingsService settings) => settings.Get());

        app.MapPut("/api/settings", (SettingsDto dto, SettingsService settings) => settings.Save(dto));

        app.MapPost("/api/settings/test", async (SettingsDto dto, LlmClient llm, CancellationToken ct) =>
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var messages = new List<LlmMessage>
                {
                    new() { Role = "system", Content = "连接测试，请只回复 OK。" },
                    new() { Role = "user", Content = "OK" }
                };
                var (content, _) = await llm.CompleteAsync(
                    dto,
                    messages,
                    timeout.Token,
                    0,
                    SettingsService.ResolveModel(dto.ChatModel, dto.Model),
                    dto.ReasoningEffort);
                stopwatch.Stop();
                var preview = string.IsNullOrWhiteSpace(content) ? "（空响应）" : content.Trim();
                if (preview.Length > 80)
                {
                    preview = preview[..80];
                }

                return new TestResultDto(true, $"连接成功，模型返回：{preview}", stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return new TestResultDto(false, ex.Message, stopwatch.ElapsedMilliseconds);
            }
        });

        // /models 拉取失败时返回 ok=false 与错误说明，不中断已保存配置
        app.MapPost("/api/settings/models", async (SettingsDto dto, LlmClient llm, CancellationToken ct) =>
            await llm.ListModelsAsync(dto, ct));
    }
}
