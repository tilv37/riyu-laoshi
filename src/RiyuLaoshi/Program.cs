using RiyuLaoshi;
using RiyuLaoshi.Data;
using RiyuLaoshi.Endpoints;
using RiyuLaoshi.Services;

// 日语问答助手入口：单进程同时提供 API 与静态前端
var builder = WebApplication.CreateBuilder(args);

var dbPath = Environment.GetEnvironmentVariable("RIYU_DB_PATH");
if (string.IsNullOrWhiteSpace(dbPath))
{
    dbPath = Path.Combine(builder.Environment.ContentRootPath, "data", "app.db");
}

// 数据库路径可用 RIYU_DB_PATH 覆盖，默认放在运行目录的 data/app.db
var db = new Db(dbPath);
db.Initialize();
db.ResetInterruptedJobs();

// 启动时建表并补齐新增配置项；把上次进程中断的整理任务标成失败，避免僵尸状态
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<SettingsService>();
// 全局 HttpClient 复用连接池；流式请求的超时由各调用链路自行控制
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(10)
})
{
    Timeout = Timeout.InfiniteTimeSpan
});
builder.Services.AddSingleton<LlmClient>();
builder.Services.AddSingleton<ChatService>();
builder.Services.AddSingleton<MemoPipeline>();
builder.Services.AddSingleton<MemoJobQueue>();
builder.Services.AddHostedService<MemoWorker>();

// Native AOT 关闭反射序列化，所有 DTO 必须走源生成上下文
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var app = builder.Build();

// 静态页面与 API 共用同一端口，未匹配的路径回退到 index.html
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapSettingsEndpoints();
app.MapSessionEndpoints();
app.MapChatEndpoints();
app.MapJobEndpoints();
app.MapMemoEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
