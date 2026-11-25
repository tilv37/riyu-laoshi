# 日语问答助手（riyu-laoshi）设计定稿

> 单用户本地 B/S 应用：限定场景的日语问答 LLM Chatbot + 面向日语考试复习的错题本式备忘录。
> 本文档为需求与技术方案的最终定稿，实现以本文为准。

---

## 1. 项目定位

- **是什么**：一个本地运行的日语学习问答助手。配置日语教学系统提示词后，在聊天框输入日语问题获得流式回答；过往问答经 LLM 整理后归档为面向**日语考试复习**的错题本式备忘录（"日语书"），只保留**单词**与**语法**两类知识点。
- **单用户**：无登录、无多租户，默认只监听 localhost。
- **非目标（明确不做）**：
  - 假名注音（振り仮名）标注
  - 从归档会话"开新会话续聊"
  - 跨会话知识点自动去重（仅会话内去重，跨会话重复靠备忘录页搜索手工清理）
  - 多模型/多厂商管理（仅一个 OpenAI 兼容连接，所有任务同主模型）
  - 移动端适配（桌面优先）

---

## 2. 技术选型

| 项 | 选定 | 说明 |
|---|---|---|
| 后端 | .NET + ASP.NET Core Minimal API，**Native AOT 发布** | 单进程，同端口 serve API + 静态页 |
| 数据库 | SQLite（`Microsoft.Data.Sqlite`），开启 WAL | 不用 EF（避免 AOT 反射坑），Dapper 或手写 ADO |
| 前端 | 原生 HTML / CSS / JS，无框架 | 中文界面 |
| Markdown 渲染 | marked + DOMPurify，**本地打包**（vendor 进 wwwroot） | 无 CDN 依赖，防 LLM 输出 HTML 注入 |
| LLM 接入 | OpenAI 兼容接口（`/v1/chat/completions`），单一连接配置 | BaseUrl / ApiKey / Model / Temperature 可配 |
| 数据文件 | `./data/app.db`，环境变量可改路径 | |
| 部署形态 | AOT 单文件/单目录自包含，启动即用 | |

---

## 3. 核心概念与状态机

### 3.1 会话状态机

```
活跃(active) ──手动归档 / 超限自动归档──▶ 已归档(archived，只读终态)
                                                │
                                                └─(手动触发)──▶ 备忘录整理 Job
```

- **活跃**：可反复打开、继续聊。消息永久保存于 SQLite。
- **已归档**：只读冻结。不可发消息、不可解档、不可恢复。触发过整理或正在整理的会话同样不可删除来源关联。
- **自动归档**：每次回答完成后检测会话长度，超过阈值（默认 `100 轮 或 100,000 字`，设置可改）即自动归档并 toast 提示。
- **归档本身不触发任何 LLM 调用**，只是状态翻转。

### 3.2 上下文（Context）

- **界面**：当前会话消息永远全量显示，从不因上下文设置而清空。
- **请求**：发送给 LLM 的内容 = `chat_system_prompt` + 最近 N 轮问答（若开关开）+ 当前问题。开关关 = 仅 `chat_system_prompt` + 当前问题（单轮模式）。
- **配置层级**：全局默认（N、开关）保存在 Settings；聊天框上方两个控件可临时修改，**只影响当次发送，不写回全局**。
- "轮" = 一对完整的问答（user + assistant）。

### 3.3 备忘录（Memo）

从**归档**会话中由 LLM 提炼的知识点条目，独立于会话存在。**定位为日语考试复习错题本**：只收录「单词」和「语法」两类，不收录发音、文化、常识等非考试向内容。字段：标题、分类（单词/语法）、标签、整理后正文、我的错题备注、来源会话。

---

## 4. 功能模块定稿

### 4.1 聊天

- **流式输出**：后端 `HttpClient` 流式转发 `chat/completions(stream:true)` 为 SSE，前端 `fetch + ReadableStream` 打字机逐字显示；流结束后一次性写入 `Messages` 表（断线不丢历史）。
- **重新生成**：仅对最后一条 AI 回答提供 [重新生成]；删除旧答案、以相同上下文重新请求，不产生重复历史。
- **失败处理**：请求失败不落库、问题留在输入框，气泡显示错误 + [重试]；不打断会话，与后台任务互不影响。
- **归档检测**：每次回答完成后计算总轮数与总字符数，超阈值 → 自动归档 + toast；归档会话输入框禁用，后端校验拒绝发送。

### 4.2 会话管理

- 会话列表分"活跃 / 已归档"两组；新建、重命名、删除。
- **标题**：第一轮问答流结束后，异步调一次 LLM（`title_system_prompt`，截取首轮问答约 200 字）生成 ≤15 字标题；失败降级取第一问前 20 字，不阻塞聊天。
- **删除归档会话**：归档会话可以删除；删除时其已生成的备忘录**独立保留**，来源标记为"原会话已删"（`source_session_id` 置空）。唯一原则：备忘录独立于会话存在。

### 4.3 备忘录整理（核心 pipeline，全自动）

- **触发**：仅且只能在归档会话详情页手动点击 [整理为备忘录]。触发后**人类不参与任何整理过程**，前端立即返回，后台执行。
- **幂等**：以 `Sessions.memo_status` 判重；已成功的会话再触发 = "重新整理"（事务内替换该会话旧 memos）；进行中的会话不可重复触发。
- **切片（防上下文超限）**：按字符数切片，默认**每 20,000 字一片、相邻片重叠 1,000 字**（设置可改）。LLM 上下文 272k；日文按 `字符数 × 1.6` 保守估 token，单片 ≈ 2~3 万 tokens，输出 JSON 亦留足空间，安全。
- **Map**：每片单独调用一次 LLM（`memo_system_prompt`），要求输出 JSON 数组：`[{title, category, tags, summary_md}]`。
- **Reduce**：再调一次合并 Prompt，对全部 Map 结果做会话内去重 + 按分类合并，产出最终知识点列表，批量写入 `Memos`。
- **分类约束**：**固定两类——`单词` / `语法`**（错题本定位，不做可配置列表）。LLM 只能二选一；不属于这两类的知识点一律丢弃。
- **Prompt 硬规则**（写入 `memo_system_prompt` 默认值）：这是面向日语考试复习的错题本；只提炼考试相关的单词与语法点，优先提取用户提问中暴露出的易错点、易混淆点；发音、文化、常识、闲聊、寒暄等一律丢弃；无可提炼时输出空数组 `[]`。
- **条目结构约定**（由 Prompt 约束，不改变表结构）：单词条目正文包含词形、读音、释义、例句/易错提示；语法条目正文包含接续、意义、例句、易错提示。
- **空结果**：0 条也算成功，任务标记"无可提炼内容"（empty 状态）。
- **执行载体**：`BackgroundService + Channel` 单 worker 队列，FIFO；失败任务挂起，队列中后续任务继续执行；**不阻塞新聊天**。

### 4.4 任务中心（专门页面）

- Job 状态机：`queued → running → succeeded / failed(挂起)`；另有 `empty`（成功但 0 条）。
- 列表：所属会话、状态、进度条（已完成片数/总片数）、创建/完成时间；失败标红挂起。
- 详情：该 job 全部日志——每片开始/成功/失败、LLM 耗时、**每次调用 token 用量**、合并结果数、错误信息与堆栈。
- 操作：[重试]（失败任务重跑）、[重新整理]（成功后想再跑一次，事务替换旧 memos）。

### 4.5 备忘录库（页面，错题本）

- 顶部筛选：分类（全部 / 单词 / 语法）+ 标签过滤 + 关键字搜索。
- 卡片列表：标题、分类、标签、整理正文（Markdown 渲染）、我的错题备注。
- 手工编辑：`my_note`（我的错题备注）、`category`（仅单词/语法）、`tags`；可删除单条。
- **导出"日语书"**：`GET /api/memos/export.md`，按 `单词 → 语法` 分章、章内按时间排序：

```markdown
# 我的日语错题本
## 单词
### 单词标题
（整理后正文）
> 我的错题备注：……
## 语法
### 语法标题
（整理后正文）
```

  导出**仅含知识点正文**（标题 + 正文 + 我的错题备注 + 标签），不含原始问答。

### 4.6 设置（抽屉）

| 分组 | 配置项 |
|---|---|
| 连接 | BaseUrl、ApiKey、Model（自动请求 `/models` 获取可选模型下拉，也支持手动输入）、Temperature、[测试连接] 按钮（发一次最小请求验证） |
| 场景模型 | 聊天问答 / 错题本整理 / 标题总结可分别指定模型；留空则回退默认 Model。三个场景共用同一 OpenAI 兼容连接 |
| 思考 | 聊天思考强度可选：默认（不发送）/ low / medium / high。启用时向聊天请求发送 `reasoning_effort`；错题本与标题不发送该参数。应用不展示思考过程，只消费最终回答 |
| 提示词 | `chat_system_prompt`（日语教学问答）、`memo_system_prompt`（错题本整理，含 JSON 输出格式与硬规则）、`title_system_prompt`（标题总结） |
| 上下文 | 默认上下文轮数 N（如 5）、默认是否携带上下文 |
| 归档 | 自动归档阈值：最大轮数（默认 100）、最大字符数（默认 100,000） |
| 切片 | 每片字符数（默认 20,000）、片间重叠（默认 1,000） |

首次运行（空配置）时聊天区引导进入设置抽屉完成连接配置。

---

## 5. 数据库设计

```sql
-- 会话
Sessions(
  id            INTEGER PK,
  title         TEXT,
  archived      INTEGER DEFAULT 0,          -- 0 活跃 / 1 归档(只读终态)
  memo_status   TEXT DEFAULT 'none',        -- none/queued/running/succeeded/failed/empty
  created_at    TEXT,
  archived_at   TEXT
)

-- 消息（会话内全量保存，界面永远全量显示）
Messages(
  id            INTEGER PK,
  session_id    INTEGER REFERENCES Sessions(id),
  role          TEXT,                      -- user / assistant
  content       TEXT,
  created_at    TEXT
)

-- 备忘录（独立于会话；不存原问答）
Memos(
  id                INTEGER PK,
  source_session_id INTEGER NULLABLE,       -- 会话删除后置空，标"原会话已删"
  title             TEXT,                   -- 知识点标题
  category          TEXT,                   -- 固定两类：单词 / 语法
  tags              TEXT,                   -- 逗号分隔多标签
  summary_md        TEXT,                   -- LLM 整理后的正文（Markdown）
  my_note           TEXT,                   -- 我的错题备注（人工）
  created_at        TEXT,
  updated_at        TEXT
)

-- 整理任务
MemoJobs(
  id            INTEGER PK,
  session_id    INTEGER REFERENCES Sessions(id),
  status        TEXT,                       -- queued/running/succeeded/failed/empty
  total_chunks  INTEGER,
  done_chunks   INTEGER DEFAULT 0,
  error         TEXT,
  retry_count   INTEGER DEFAULT 0,
  created_at    TEXT,
  finished_at    TEXT
)

-- 任务日志
MemoJobLogs(
  id            INTEGER PK,
  job_id        INTEGER REFERENCES MemoJobs(id),
  level         TEXT,                       -- info/warn/error
  step          TEXT,                       -- chunk_3_map / reduce / persist 等
  message       TEXT,
  tokens_used   INTEGER,
  created_at    TEXT
)

-- 键值配置
Settings(key TEXT PK, value TEXT)
```

Settings 预置键：`base_url`、`api_key`、`model`、`chat_model`、`memo_model`、`title_model`、`reasoning_effort`、`temperature`、`chat_system_prompt`、`memo_system_prompt`、`title_system_prompt`、`default_context_rounds`、`default_include_context`、`auto_archive_rounds`、`auto_archive_chars`、`chunk_chars`、`chunk_overlap`。

---

## 6. API 概览

| 方法 & 路径 | 说明 |
|---|---|
| `POST /api/chat` | 发送消息，SSE 流式返回；body 含 session_id、message、临时覆盖的 n/include_context |
| `POST /api/chat/regenerate` | 重新生成最后一条 AI 回答（替换） |
| `GET /api/sessions` | 会话列表（?archived=0/1） |
| `POST /api/sessions` | 新建会话 |
| `GET /api/sessions/{id}` | 会话详情 + 全量消息 |
| `PATCH /api/sessions/{id}` | 重命名 |
| `POST /api/sessions/{id}/archive` | 手动归档 |
| `DELETE /api/sessions/{id}` | 删除（归档会话删除后其备忘录独立保留） |
| `POST /api/sessions/{id}/memo-job` | 在归档会话上手动触发整理，立即入队返回 |
| `GET /api/memo-jobs` | 任务列表 |
| `GET /api/memo-jobs/{id}/logs` | 任务日志详情 |
| `POST /api/memo-jobs/{id}/retry` | 重试（失败）/ 重新整理（成功，事务替换） |
| `GET /api/memos` | 备忘录列表（?category=&tag=&q=） |
| `PATCH /api/memos/{id}` | 手工编辑（my_note/category/tags） |
| `DELETE /api/memos/{id}` | 删除单条 |
| `GET /api/memos/export.md` | 导出"日语书" Markdown，浏览器直接下载 |
| `GET /api/settings` / `PUT /api/settings` | 读/写配置 |
| `POST /api/settings/test` | 测试连接（最小 LLM 请求） |
| `POST /api/settings/models` | 请求 OpenAI 兼容 `/models`，返回可选模型列表；支持 `data[]/models[]/数组` 响应 |

---

## 7. 边界与已知取舍

1. **聊天请求失败**：不落库、问题留输入框、错误气泡可重试；与后台任务完全隔离。
2. **整理任务失败**：挂起（failed）标红，不阻塞任何新聊天；日志可查、可一键重试。
3. **跨会话知识点重复**：Map-Reduce 仅会话内去重；跨会话重复靠备忘录页搜索手工清理。已知取舍，MVP 不做相似检测。
4. **删除已生成备忘录的归档会话**：备忘录独立保留，来源标"原会话已删"。
5. **导出书仅含知识点正文**，不含原始问答——因此 `Memos` 表也不存原问答，原文始终留在归档会话里。
6. **token 估算**：`字符数 × 1.6` 保守粗估（日文假名 ≈ 1 token/字），不引入 tokenizer；272k 上下文下所有阈值均安全。
7. **并发**：单 worker 顺序执行整理任务；SQLite WAL 模式支持读写并发。

---

## 8. 实现注意事项（AOT）

- Minimal API 使用 RDG（Request Delegate Generator，类型化 lambda 的 `MapGet/MapPost`）保证 AOT 兼容。
- 不用 EF Core；`Microsoft.Data.Sqlite` + Dapper（如 Dapper AOT 有坑则手写 ADO.NET，表只有 6 张，成本可控）。
- SSE 转发：`HttpClient` + `Response.Body` 逐块写出，注意关闭/取消令牌传递（用户断开时中止上游请求）。
- 后台队列：`Channel<T>` + `BackgroundService`；进程重启后 queued/running 的 job 启动时重置为 failed（挂起待人工重试），避免僵尸状态。
- 静态资源：marked.js、DOMPurify 直接 vendor 进 `wwwroot/vendor/`，无 CDN。
- 监听：默认 `http://localhost:5000`（可用 `--urls` 覆盖）；数据库路径默认 `./data/app.db`，环境变量可改。

---

## 9. 里程碑建议

| 阶段 | 内容 |
|---|---|
| M1 | 工程骨架 + 设置读写/测试连接 + 会话与消息 CRUD + 聊天流式（含上下文 N/开关） |
| M2 | 手动归档 + 回答后超限自动归档 + 标题自动总结 + 重新生成 |
| M3 | 切片 Map-Reduce 整理 pipeline + 后台队列 + 任务中心（状态/日志/重试） |
| M4 | 备忘录页（筛选/编辑）+ 导出"日语书" + 空状态/错误态打磨 |

---

## 附：关键决策记录

| # | 决策点 | 结论 |
|---|---|---|
| 1 | 技术栈 | .NET Native AOT + SQLite + 无框架原生前端 |
| 2 | LLM 接入 | OpenAI 兼容接口，单一连接，所有任务同主模型 |
| 3 | 上下文控制 | 全局默认 N 轮 + 开关；聊天框临时改不写回；界面永不清空 |
| 4 | 归档 | 只读终态，不可解档；手动或超限（100轮/10万字）自动 |
| 5 | 备忘录触发 | 仅归档会话手动触发；触发后全自动，人不参与 |
| 6 | 长会话整理 | Map-Reduce 切片（2万字/片+1000重叠），适配 272k 上下文 |
| 7 | 失败策略 | 任务挂起不阻塞聊天；专门任务中心看状态/日志/重试 |
| 8 | 导出书 | 仅知识点正文（标题+正文+错题备注+标签），按单词/语法分章节 |
| 9 | 删除归档会话 | 备忘录独立保留，来源置空 |
| 10 | 假名注音 | 不做 |
| 11 | 重新生成 | 仅最后一条 AI 回答，替换式 |
| 12 | 归档后续聊 | 不做"开新会话续聊"功能 |
| 13 | 备忘录定位（变更） | 面向日语考试复习的错题本，仅保留「单词 / 语法」两类，固定分类，不收录发音/文化/常识 |
| 14 | 模型选择 | 设置页自动请求 OpenAI 兼容 `/models`，形成可下拉候选，同时保留手动输入 |
| 15 | 场景模型（变更） | 聊天问答 / 错题本整理 / 标题总结分别配置模型，留空回退默认模型；测试连接使用聊天模型 |
| 16 | 思考模式 | 聊天支持 `reasoning_effort`（low/medium/high），默认不发送；仅聊天场景启用，错题本与标题不发送以控制成本；不展示思考过程 |
