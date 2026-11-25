# riyu-laoshi · 日语问答助手

面向日语考试复习的本地 LLM 问答工具：聊天问答 → 会话归档 → 手动触发 LLM 整理 → 生成「单词 / 语法」错题本 → 导出 Markdown 日语书。

完整需求与技术定稿见 [`docs/PLAN.md`](docs/PLAN.md)。

## 功能

- 流式聊天（SSE 打字机输出），可临时调整上下文轮数与是否携带上下文
- 全局系统提示词、错题本整理提示词、标题总结提示词均可配置
- 设置页自动请求 OpenAI 兼容 `/models` 接口，模型可下拉选择，也支持手动输入
- 聊天问答 / 错题本整理 / 标题总结可分别指定模型（留空回退默认模型），降低不必要的 token 与费用
- 聊天可选思考强度（`reasoning_effort`: low / medium / high），默认不发送；错题本与标题不启用思考以控制成本
- 会话自动标题、超长自动归档、手动归档（归档后只读）
- 归档会话手动触发后台整理：切片 Map-Reduce，任务状态与日志可在「任务中心」查看、重试
- 错题本仅收录「单词 / 语法」，支持分类、标签、搜索、人工备注与编辑
- 导出「日语书」Markdown（按单词 / 语法分章）
- 单用户、本地运行、无登录

## 技术栈

- .NET 10 Minimal API（Native AOT 发布配置）
- SQLite（`Microsoft.Data.Sqlite`，WAL）+ 手写 ADO.NET
- 原生 HTML / CSS / JS，无前端框架
- marked + DOMPurify（本地 vendor，无 CDN）
- OpenAI 兼容接口（`/v1/chat/completions`）

## 开发运行

需要 .NET 10 SDK。

```bash
dotnet run --project src/RiyuLaoshi
```

默认监听 `http://localhost:5000`（可用 `ASPNETCORE_URLS` 覆盖），首次打开后进入「设置」配置：

- Base URL，例如 `https://api.openai.com/v1`（也支持完整 `/chat/completions` 地址）
- API Key（本地模型可留空）
- 模型名
- 温度与三个提示词

数据库默认位于运行目录 `data/app.db`，可通过环境变量 `RIYU_DB_PATH` 指定其他路径。SQLite 使用 WAL 模式，单文件即可备份。

## Native AOT 发布

```bash
dotnet publish src/RiyuLaoshi -c Release -r linux-x64 -o out
```

AOT 编译需要本机具备 clang 与 zlib 开发库；若环境缺少工具链，可先使用普通发布（去掉 `PublishAot` 或直接 `dotnet publish -c Release`）验证功能。

## 使用流程

1. 在聊天框提问，回答流式返回；首轮结束后自动生成会话标题。
2. 会话过长（默认 100 轮或 100,000 字）会自动归档；也可手动归档。
3. 打开已归档会话，点击「整理为备忘录」手动触发后台整理（归档本身不会触发 LLM）。
4. 整理过程全自动：按配置切片，逐片提炼，再合并去重，写入错题本。
5. 在「任务中心」查看进度、日志与 token 用量，失败任务可重试。
6. 在「错题本」筛选、编辑、补充错题备注，导出 Markdown。

## 目录

```
docs/PLAN.md                需求与技术定稿
src/RiyuLaoshi/             ASP.NET Core 工程
  Data/Db.cs                SQLite 建表与数据访问
  Services/                 LLM 客户端、聊天、整理流水线、后台 worker
  Endpoints/                Minimal API 端点
  wwwroot/                  原生前端（Gemini 风格）
    vendor/                 marked、DOMPurify
```

## 说明

- 本项目默认只面向本机单用户使用，请勿直接暴露到公网；如需局域网访问，请自行增加鉴权与 HTTPS。
- 界面图标通过 Iconify 选取并内联为 SVG 雪碧图：Lucide（ISC）用于通用 UI，IconOven（MIT）用于品牌鸟居图标与 favicon。
- marked（MIT）与 DOMPurify（Apache-2.0）版权归各自作者所有。
