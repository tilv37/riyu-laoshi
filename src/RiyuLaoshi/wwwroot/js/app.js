// 应用主控：路由、侧栏、会话管理、聊天流式渲染与标题轮询
import { api, streamChat } from "./api.js";
import {
  escapeHtml,
  icon,
  renderMarkdown,
  formatTime,
  toast,
  showConfirm,
  showRename,
  autoGrow
} from "./ui.js";
import { initSettings, openSettings } from "./settings.js";
import { initMemos, showMemos } from "./memos.js";
import { initJobs, showJobs, hideJobs } from "./jobs.js";

// 与 CSS 媒体查询保持一致的移动端断点
const MOBILE_BREAKPOINT = 960;

const MEMO_STATUS_META = {
  queued: { label: "排队中…", icon: "clock" },
  running: { label: "整理中…", icon: "loader-circle", spin: true },
  succeeded: { label: "已整理", icon: "circle-check" },
  empty: { label: "无可提炼内容", icon: "inbox" },
  failed: { label: "整理失败", icon: "circle-alert" }
};

// 全局视图状态：设置、会话列表、当前会话、消息与各类轮询定时器
const state = {
  settings: null,
  sessions: { active: [], archived: [] },
  current: null,
  messages: [],
  streaming: false,
  memoPollTimer: null,
  titleRefreshTimer: null,
  lastFailedText: "",
  currentView: "chat",
  menuSessionId: null
};

const els = {};

function $(id) {
  return document.getElementById(id);
}

function cacheElements() {
  [
    "sidebar", "sidebar-collapse", "sidebar-open", "new-chat-btn", "nav-memos", "nav-jobs",
    "active-group", "archived-group", "active-sessions", "archived-sessions", "sidebar-empty",
    "session-scroll", "settings-btn", "view-chat", "view-memos", "view-jobs",
    "chat-title", "archived-badge", "memo-status-chip", "memo-trigger-btn", "archive-btn",
    "chat-scroll", "messages", "composer-area", "ctx-n", "ctx-enabled", "chat-input",
    "send-btn", "session-menu"
  ].forEach((id) => { els[id] = $(id); });
}

// 启动顺序：加载设置 → 会话列表 → 初始化侧栏 → 按 hash 路由
async function init() {
  cacheElements();
  initSettings(onSettingsSaved);
  initMemos();
  initJobs();
  bindEvents();

  try {
    state.settings = await api.getSettings();
  } catch (error) {
    toast(`读取设置失败：${error.message}`, "error");
  }
  applySettingsDefaults();
  await loadSessions();

  const stored = localStorage.getItem("sidebarCollapsed");
  const collapsed = stored === null ? window.innerWidth < MOBILE_BREAKPOINT : stored === "1";
  setSidebarCollapsed(collapsed, false);

  await route();
  window.addEventListener("hashchange", () => { route(); });
}

function closeSidebarOnMobile() {
  if (window.innerWidth < MOBILE_BREAKPOINT) toggleSidebar(true);
}

function bindEvents() {
  els["new-chat-btn"].addEventListener("click", () => {
    navigate("#/chat");
    closeSidebarOnMobile();
  });
  els["nav-memos"].addEventListener("click", () => {
    navigate("#/memos");
    closeSidebarOnMobile();
  });
  els["nav-jobs"].addEventListener("click", () => {
    navigate("#/jobs");
    closeSidebarOnMobile();
  });
  els["settings-btn"].addEventListener("click", () => {
    closeSidebarOnMobile();
    openSettings();
  });
  els["sidebar-collapse"].addEventListener("click", () => toggleSidebar(true));
  els["sidebar-open"].addEventListener("click", () => toggleSidebar(false));

  els["chat-input"].addEventListener("input", () => {
    autoGrow(els["chat-input"]);
    updateComposer();
  });
  els["chat-input"].addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      sendQuestion(els["chat-input"].value);
    }
  });
  els["send-btn"].addEventListener("click", () => sendQuestion(els["chat-input"].value));

  els["ctx-n"].addEventListener("change", () => {
    const value = clampNumber(els["ctx-n"].value, 0, 50, 5);
    els["ctx-n"].value = String(value);
  });

  els["archive-btn"].addEventListener("click", () => {
    if (state.current) archiveSession(state.current.id);
  });
  els["memo-trigger-btn"].addEventListener("click", triggerMemoJob);

  els["active-sessions"].addEventListener("click", onSessionListClick);
  els["archived-sessions"].addEventListener("click", onSessionListClick);
  els["session-menu"].addEventListener("click", onSessionMenuClick);
  $("sidebar-backdrop").addEventListener("click", () => toggleSidebar(true));

  els["messages"].addEventListener("click", onMessagesClick);

  document.addEventListener("click", (event) => {
    if (!event.target.closest("#session-menu") && !event.target.closest(".session-menu-btn")) {
      closeSessionMenu();
    }
  });
  window.addEventListener("resize", closeSessionMenu);
  els["session-scroll"].addEventListener("scroll", closeSessionMenu);
}

function navigate(hash) {
  if (location.hash === hash) {
    route();
  } else {
    location.hash = hash;
  }
}

function showView(name) {
  state.currentView = name;
  els["view-chat"].hidden = name !== "chat";
  els["view-memos"].hidden = name !== "memos";
  els["view-jobs"].hidden = name !== "jobs";
  els["nav-memos"].classList.toggle("active", name === "memos");
  els["nav-jobs"].classList.toggle("active", name === "jobs");
  if (name !== "jobs") hideJobs();
}

async function route() {
  clearTimeout(state.memoPollTimer);
  const hash = location.hash || "#/chat";

  if (hash.startsWith("#/memos")) {
    showView("memos");
    await showMemos();
    return;
  }

  if (hash.startsWith("#/jobs")) {
    showView("jobs");
    await showJobs();
    return;
  }

  showView("chat");
  const match = hash.match(/^#\/chat\/(\d+)/);
  if (match) {
    await loadSession(Number(match[1]));
  } else {
    startNewChat();
  }
}

function startNewChat() {
  clearTimeout(state.memoPollTimer);
  clearTimeout(state.titleRefreshTimer);
  state.current = null;
  state.messages = [];
  renderChatHeader();
  renderChatBody();
  updateComposer();
  renderSessions();
  setTimeout(() => els["chat-input"].focus(), 60);
}

async function loadSession(id) {
  clearTimeout(state.memoPollTimer);
  clearTimeout(state.titleRefreshTimer);
  try {
    const detail = await api.getSession(id);
    state.current = detail.session;
    state.messages = detail.messages;
    renderChatHeader();
    renderChatBody();
    updateComposer();
    renderSessions();
    scrollToBottom(true);
    if (state.current.archived && ["queued", "running"].includes(state.current.memoStatus)) {
      scheduleMemoPoll();
    }
  } catch (error) {
    toast("会话不存在或已被删除", "error");
    navigate("#/chat");
  }
}

async function loadSessions() {
  try {
    const all = await api.listSessions();
    state.sessions.active = all.filter((session) => !session.archived);
    state.sessions.archived = all.filter((session) => session.archived);
    renderSessions();
    syncCurrentSession();
  } catch (error) {
    toast(`读取会话列表失败：${error.message}`, "error");
  }
}

function syncCurrentSession() {
  if (!state.current) return;
  const found = [...state.sessions.active, ...state.sessions.archived]
    .find((session) => session.id === state.current.id);
  if (!found) return;
  const changed = found.title !== state.current.title ||
    found.archived !== state.current.archived ||
    found.memoStatus !== state.current.memoStatus;
  if (changed) {
    state.current = { ...state.current, title: found.title, archived: found.archived, memoStatus: found.memoStatus };
    renderChatHeader();
    updateComposer();
  }
}

// 渲染侧栏的活跃/归档两组会话，空组自动隐藏
function renderSessions() {
  const active = state.sessions.active;
  const archived = state.sessions.archived;

  els["active-sessions"].innerHTML = active.map(sessionItemHtml).join("");
  els["archived-sessions"].innerHTML = archived.map(sessionItemHtml).join("");
  els["active-group"].hidden = active.length === 0;
  els["archived-group"].hidden = archived.length === 0;
  els["sidebar-empty"].hidden = active.length > 0 || archived.length > 0;
}

function sessionItemHtml(session) {
  const selected = state.current?.id === session.id ? "selected" : "";
  return `
    <div class="session-item ${selected}" data-id="${session.id}">
      <button class="session-open" type="button">
        <span class="session-name">${escapeHtml(session.title || "新会话")}</span>
        <span class="session-meta">${session.archived ? "已归档" : formatTime(session.createdAt)}</span>
      </button>
      <button class="session-menu-btn" type="button" data-menu="${session.id}" title="更多操作">${icon("ellipsis", "icon icon-xs")}</button>
    </div>`;
}

function onSessionListClick(event) {
  const menuButton = event.target.closest(".session-menu-btn");
  if (menuButton) {
    event.stopPropagation();
    const id = Number(menuButton.dataset.menu);
    openSessionMenu(id, menuButton);
    return;
  }

  const openButton = event.target.closest(".session-open");
  if (openButton) {
    const item = openButton.closest(".session-item");
    const id = Number(item.dataset.id);
    navigate(`#/chat/${id}`);
    if (window.innerWidth < MOBILE_BREAKPOINT) toggleSidebar(true);
  }
}

function findSession(id) {
  return [...state.sessions.active, ...state.sessions.archived].find((session) => session.id === id);
}

function openSessionMenu(id, anchor) {
  const session = findSession(id);
  if (!session) return;
  state.menuSessionId = id;
  const menu = els["session-menu"];
  menu.innerHTML = `
    <button type="button" data-action="rename">${icon("pencil", "icon icon-xs")}重命名</button>
    ${session.archived ? "" : `<button type="button" data-action="archive">${icon("archive", "icon icon-xs")}归档</button>`}
    <button type="button" data-action="delete" class="danger-text">${icon("trash-2", "icon icon-xs")}删除</button>`;
  menu.hidden = false;

  const rect = anchor.getBoundingClientRect();
  const menuRect = menu.getBoundingClientRect();
  let left = rect.right - menuRect.width;
  let top = rect.bottom + 6;
  if (top + menuRect.height > window.innerHeight - 8) {
    top = rect.top - menuRect.height - 6;
  }
  left = Math.max(8, Math.min(left, window.innerWidth - menuRect.width - 8));
  menu.style.left = `${left}px`;
  menu.style.top = `${top}px`;
}

function closeSessionMenu() {
  if (!els["session-menu"].hidden) {
    els["session-menu"].hidden = true;
    state.menuSessionId = null;
  }
}

async function onSessionMenuClick(event) {
  const actionButton = event.target.closest("[data-action]");
  if (!actionButton) return;
  const id = state.menuSessionId;
  const action = actionButton.dataset.action;
  closeSessionMenu();
  if (!id) return;

  if (action === "rename") {
    await renameSession(id);
  } else if (action === "archive") {
    await archiveSession(id);
  } else if (action === "delete") {
    await deleteSession(id);
  }
}

async function renameSession(id) {
  const session = findSession(id);
  if (!session) return;
  const title = await showRename(session.title);
  if (!title) return;
  try {
    await api.renameSession(id, title);
    toast("已重命名");
    await loadSessions();
  } catch (error) {
    toast(`重命名失败：${error.message}`, "error");
  }
}

async function archiveSession(id) {
  const session = findSession(id);
  if (!session || session.archived) return;
  const ok = await showConfirm("归档后会话将变为只读，不能继续对话。确认归档？", "归档");
  if (!ok) return;
  try {
    await api.archiveSession(id);
    toast("会话已归档");
    await loadSessions();
    if (state.current?.id === id) {
      await loadSession(id);
    }
  } catch (error) {
    toast(`归档失败：${error.message}`, "error");
  }
}

async function deleteSession(id) {
  const ok = await showConfirm("删除会话后不可恢复；已经生成的错题本内容会保留。确认删除？", "删除");
  if (!ok) return;
  try {
    await api.deleteSession(id);
    toast("会话已删除");
    if (state.current?.id === id) {
      navigate("#/chat");
    }
    await loadSessions();
  } catch (error) {
    toast(`删除失败：${error.message}`, "error");
  }
}

function renderChatHeader() {
  const session = state.current;
  const title = session?.title || "新对话";
  els["chat-title"].textContent = title;
  els["archived-badge"].hidden = !session?.archived;
  els["archive-btn"].hidden = !session || session.archived;

  if (session?.archived) {
    els["memo-trigger-btn"].hidden = false;
    const status = session.memoStatus || "none";
    const busy = status === "queued" || status === "running";
    els["memo-trigger-btn"].disabled = busy;
    const triggerLabel = busy
      ? "整理中…"
      : (status === "succeeded" || status === "empty" || status === "failed" ? "重新整理" : "整理为备忘录");
    els["memo-trigger-btn"].innerHTML = `${icon(busy ? "loader-circle" : "book-plus", busy ? "icon icon-sm spin" : "icon icon-sm")}${triggerLabel}`;

    const chip = els["memo-status-chip"];
    const meta = MEMO_STATUS_META[status];
    if (meta) {
      chip.hidden = false;
      chip.className = `chip memo-status status-${status}`;
      chip.innerHTML = `${icon(meta.icon, meta.spin ? "icon spin" : "icon")}${meta.label}`;
    } else {
      chip.hidden = true;
    }
  } else {
    els["memo-trigger-btn"].hidden = true;
    els["memo-status-chip"].hidden = true;
  }
}

function needsSetup() {
  const settings = state.settings;
  return !settings || !(settings.chatModel || settings.model);
}

function renderChatBody() {
  const container = els["messages"];
  if (!state.current) {
    container.innerHTML = emptyGreetingHtml();
    return;
  }

  if (state.messages.length === 0) {
    container.innerHTML = `
      <div class="greeting small">
        <div class="greeting-sparkle">${icon("sparkles")}</div>
        <h1>开始提问吧</h1>
        <p>会话已创建，输入你的第一个日语问题。</p>
      </div>`;
    return;
  }

  container.innerHTML = state.messages.map((message, index) => messageHtml(message, index)).join("");
}

function emptyGreetingHtml() {
  return `
    <div class="greeting">
      <div class="greeting-sparkle">${icon("sparkles")}</div>
      <h1 class="greeting-title">你好，今天想学什么日语？</h1>
      <p class="greeting-sub">语法讲解、词汇辨析、敬语用法，都可以问我。</p>
      ${needsSetup() ? `
        <div class="setup-banner">
          ${icon("key-round", "icon icon-sm")}
          <span>首次使用：先配置接口与模型，才能开始对话。</span>
          <button type="button" class="text-btn" data-open-settings>打开设置</button>
        </div>` : ""}
      <div class="suggestions">
        <button type="button" class="suggestion" data-suggestion="帮我讲解一下「は」和「が」的区别">「は」和「が」的区别</button>
        <button type="button" class="suggestion" data-suggestion="请辨析「勉強する」和「学ぶ」的用法差异">词汇辨析：勉強する / 学ぶ</button>
        <button type="button" class="suggestion" data-suggestion="请总结一下日语敬语中尊敬语和谦让语的区别">尊敬语与谦让语</button>
        <button type="button" class="suggestion" data-suggestion="N2 常考的「〜わけだ」「〜はずだ」「〜べきだ」怎么区分？">N2 语法辨析</button>
      </div>
    </div>`;
}

function messageHtml(message, index) {
  if (message.role === "user") {
    return `
      <div class="msg user">
        <div class="bubble">${escapeHtml(message.content).replaceAll("\n", "<br>")}</div>
      </div>`;
  }

  const isLast = index === state.messages.length - 1;
  const canRegenerate = isLast && !state.current?.archived && !state.streaming;
  return `
    <div class="msg assistant" data-mid="${message.id}">
      <div class="assistant-avatar" aria-hidden="true">${icon("sparkles")}</div>
      <div class="assistant-body">
        <div class="md">${renderMarkdown(message.content)}</div>
        <div class="msg-actions">
          <button type="button" class="msg-action" data-copy="${message.id}">${icon("copy", "icon icon-xs")}复制</button>
          ${canRegenerate ? `<button type="button" class="msg-action" data-regen="1">${icon("refresh-cw", "icon icon-xs")}重新生成</button>` : ""}
        </div>
      </div>
    </div>`;
}

function updateComposer() {
  const archived = Boolean(state.current?.archived);
  els["chat-input"].disabled = archived;
  els["ctx-n"].disabled = archived;
  els["ctx-enabled"].disabled = archived;
  els["send-btn"].disabled = archived || state.streaming || els["chat-input"].value.trim().length === 0;
  els["chat-input"].placeholder = archived ? "该会话已归档，无法继续对话" : "输入你的日语问题…";
  els["composer-area"].classList.toggle("archived", archived);
}

function readContextRounds() {
  return clampNumber(els["ctx-n"].value, 0, 50, state.settings?.defaultContextRounds ?? 5);
}

function applySettingsDefaults() {
  const settings = state.settings;
  if (!settings) return;
  els["ctx-n"].value = String(settings.defaultContextRounds ?? 5);
  els["ctx-enabled"].checked = Boolean(settings.defaultIncludeContext);
  updateComposer();
  if (!state.current) {
    renderChatBody();
  }
}

function onSettingsSaved(settings) {
  state.settings = settings;
  applySettingsDefaults();
  toast("设置已保存");
}

// 发送消息：创建会话（如需要）→ 流式接收 → 落库 → 按需刷新标题
async function sendQuestion(rawText) {
  if (state.streaming) return;
  const text = String(rawText ?? "").trim();
  if (!text) return;

  if (state.current?.archived) {
    toast("该会话已归档，无法继续对话", "error");
    return;
  }

  if (needsSetup()) {
    openSettings();
    toast("请先在设置中配置模型", "error");
    return;
  }

  if (!state.current) {
    try {
      const created = await api.createSession();
      state.current = {
        id: created.id,
        title: created.title,
        archived: false,
        memoStatus: "none",
        createdAt: new Date().toISOString(),
        archivedAt: null
      };
      state.messages = [];
      history.replaceState(null, "", `#/chat/${created.id}`);
      renderChatHeader();
      await loadSessions();
    } catch (error) {
      toast(`创建会话失败：${error.message}`, "error");
      return;
    }
  }

  const payload = {
    sessionId: state.current.id,
    message: text,
    contextRounds: readContextRounds(),
    includeContext: els["ctx-enabled"].checked
  };

  state.streaming = true;
  state.lastFailedText = text;
  els["chat-input"].value = "";
  autoGrow(els["chat-input"]);
  updateComposer();
  els["messages"].querySelector(".greeting")?.remove();

  const userMessage = { id: 0, role: "user", content: text, createdAt: new Date().toISOString() };
  state.messages.push(userMessage);
  appendUserMessage(text);

  const streamNode = appendStreamingAssistant();
  const renderStreamingMarkdown = createStreamRenderer(streamNode);
  let fullAnswer = "";
  let finished = false;
  let errorMessage = "";

  try {
    await streamChat("/api/chat", payload, (event) => {
      if (event.type === "delta") {
        fullAnswer += event.delta ?? "";
        renderStreamingMarkdown(fullAnswer);
      } else if (event.type === "error") {
        errorMessage = event.error || "请求失败";
        throw new Error(errorMessage);
      } else if (event.type === "done") {
        const assistantMessage = {
          id: event.assistantMessageId ?? 0,
          role: "assistant",
          content: fullAnswer,
          createdAt: new Date().toISOString()
        };
        state.messages.push(assistantMessage);
        if (event.archived) {
          state.current.archived = true;
          toast("会话超过长度阈值，已自动归档", "warn");
        }
        finished = true;
      }
    });
  } catch (error) {
    errorMessage = errorMessage || error.message || "请求失败";
  }

  if (!finished) {
    state.messages.pop();
    renderChatBody();
    showErrorRow(state.lastFailedText, errorMessage);
    els["chat-input"].value = text;
    autoGrow(els["chat-input"]);
    toast(errorMessage, "error");
  } else {
    renderChatBody();
    renderChatHeader();
    scrollToBottom(true);
  }

  state.streaming = false;
  updateComposer();
  await loadSessions();
  if (finished && state.current?.title === "新会话") {
    startTitlePolling(state.current.id);
  }
  els["chat-input"].focus();
}

function appendUserMessage(text) {
  const wrapper = document.createElement("div");
  wrapper.className = "msg user";
  const bubble = document.createElement("div");
  bubble.className = "bubble";
  bubble.innerHTML = escapeHtml(text).replaceAll("\n", "<br>");
  wrapper.appendChild(bubble);
  els["messages"].appendChild(wrapper);
  scrollToBottom(true);
}

function appendStreamingAssistant() {
  const wrapper = document.createElement("div");
  wrapper.className = "msg assistant streaming";

  const avatar = document.createElement("div");
  avatar.className = "assistant-avatar";
  avatar.innerHTML = icon("sparkles");

  const body = document.createElement("div");
  body.className = "assistant-body";
  const text = document.createElement("div");
  text.className = "md streaming-text";
  body.appendChild(text);

  wrapper.appendChild(avatar);
  wrapper.appendChild(body);
  els["messages"].appendChild(wrapper);
  scrollToBottom(true);
  return text;
}

// 流式 Markdown 渲染器：节流到 40ms 重绘，避免每个字节都解析一次
function createStreamRenderer(node) {
  let timer = null;
  let latest = "";

  const paint = () => {
    timer = null;
    node.innerHTML = renderMarkdown(latest);
    const cursor = document.createElement("span");
    cursor.className = "stream-cursor";
    let target = node.lastElementChild;
    if (target && (target.tagName === "UL" || target.tagName === "OL")) {
      target = target.lastElementChild || target;
    }

    if (target && /^(P|LI|H[1-6]|BLOCKQUOTE|TD|TH|DT|DD)$/.test(target.tagName)) {
      target.appendChild(cursor);
    } else {
      node.appendChild(cursor);
    }

    scrollToBottom();
  };

  return (text) => {
    latest = text;
    if (timer === null) {
      timer = window.setTimeout(paint, 40);
    }
  };
}

function showErrorRow(text, message) {
  const row = document.createElement("div");
  row.className = "error-row";
  row.innerHTML = `
    <div class="error-copy">
      <strong>发送失败</strong>
      <span>${escapeHtml(message)}</span>
    </div>
    <button type="button" class="btn ghost retry-send">${icon("rotate-ccw", "icon icon-xs")}重试</button>`;
  els["messages"].appendChild(row);
  scrollToBottom(true);
}

// 重新生成最后一条回答：原地替换内容，不产生新的历史消息
async function regenerateLast() {
  if (state.streaming || !state.current || state.current.archived) return;
  const last = state.messages[state.messages.length - 1];
  if (!last || last.role !== "assistant") return;

  state.streaming = true;
  updateComposer();

  const node = els["messages"].querySelector(
    `.msg.assistant[data-mid="${last.id}"] .md`
  );
  const renderStreamingMarkdown = node ? createStreamRenderer(node) : null;
  if (node) {
    node.textContent = "";
    node.classList.add("streaming-text");
    node.closest(".msg")?.classList.add("streaming");
  }

  let full = "";
  let errorMessage = "";
  let finished = false;

  try {
    await streamChat("/api/chat/regenerate", {
      sessionId: state.current.id,
      contextRounds: readContextRounds(),
      includeContext: els["ctx-enabled"].checked
    }, (event) => {
      if (event.type === "delta") {
        full += event.delta ?? "";
        if (renderStreamingMarkdown) renderStreamingMarkdown(full);
      } else if (event.type === "error") {
        errorMessage = event.error || "重新生成失败";
        throw new Error(errorMessage);
      } else if (event.type === "done") {
        last.content = full;
        finished = true;
      }
    });
  } catch (error) {
    errorMessage = errorMessage || error.message || "重新生成失败";
  }

  state.streaming = false;
  renderChatBody();
  updateComposer();
  await loadSessions();
  if (!finished) {
    toast(errorMessage, "error");
  }
}

// 手动触发归档会话的错题整理，立即返回，进度靠轮询刷新
async function triggerMemoJob() {
  if (!state.current?.archived) return;
  const button = els["memo-trigger-btn"];
  button.disabled = true;
  try {
    await api.triggerMemoJob(state.current.id);
    state.current.memoStatus = "queued";
    renderChatHeader();
    toast("已加入整理队列，可到任务中心查看进度");
    await loadSessions();
    scheduleMemoPoll();
  } catch (error) {
    button.disabled = false;
    toast(`触发整理失败：${error.message}`, "error");
  }
}

// 轮询整理任务状态，完成后停止并提示结果
function scheduleMemoPoll() {
  clearTimeout(state.memoPollTimer);
  const sessionId = state.current?.id;
  if (!sessionId) return;

  state.memoPollTimer = setTimeout(async () => {
    if (state.current?.id !== sessionId) return;
    try {
      const detail = await api.getSession(sessionId);
      state.current = detail.session;
      renderChatHeader();
      if (["queued", "running"].includes(state.current.memoStatus)) {
        scheduleMemoPoll();
      } else {
        await loadSessions();
        if (state.current.memoStatus === "succeeded") {
          toast("错题本整理完成，去错题本看看");
        } else if (state.current.memoStatus === "empty") {
          toast("整理完成：没有发现可收录的知识点", "warn");
        } else if (state.current.memoStatus === "failed") {
          toast("整理失败，可到任务中心查看日志", "error");
        }
      }
    } catch {
      // Session may have been deleted; stop polling.
    }
  }, 2500);
}

// 标题由后台异步生成：轮询到标题变化或超时（约 30 秒）后停止
function startTitlePolling(sessionId) {
  clearTimeout(state.titleRefreshTimer);
  let attempts = 0;

  const tick = async () => {
    if (state.current?.id !== sessionId) return;
    if (state.current.title && state.current.title !== "新会话") return;

    try {
      await loadSessions();
    } catch {
      // Keep polling; a transient list failure should not strand the title.
    }

    if (state.current?.id !== sessionId) return;
    if (state.current.title && state.current.title !== "新会话") return;

    attempts++;
    if (attempts < 20) {
      state.titleRefreshTimer = setTimeout(tick, 1500);
    }
  };

  state.titleRefreshTimer = setTimeout(tick, 1200);
}

function onMessagesClick(event) {
  const suggestion = event.target.closest("[data-suggestion]");
  if (suggestion) {
    els["chat-input"].value = suggestion.dataset.suggestion;
    autoGrow(els["chat-input"]);
    updateComposer();
    els["chat-input"].focus();
    return;
  }

  if (event.target.closest("[data-open-settings]")) {
    openSettings();
    return;
  }

  const copyButton = event.target.closest("[data-copy]");
  if (copyButton) {
    copyMessage(Number(copyButton.dataset.copy));
    return;
  }

  if (event.target.closest("[data-regen]")) {
    regenerateLast();
    return;
  }

  if (event.target.closest(".retry-send")) {
    if (state.lastFailedText) {
      sendQuestion(state.lastFailedText);
    }
  }
}

// 复制回答正文，优先用 Clipboard API，失败时回退到 execCommand
async function copyMessage(id) {
  const message = state.messages.find((item) => item.id === id);
  if (!message) return;
  try {
    await navigator.clipboard.writeText(message.content);
    toast("已复制");
  } catch {
    const textarea = document.createElement("textarea");
    textarea.value = message.content;
    document.body.appendChild(textarea);
    textarea.select();
    document.execCommand("copy");
    textarea.remove();
    toast("已复制");
  }
}

function scrollToBottom(force = false) {
  const container = els["chat-scroll"];
  const nearBottom = container.scrollHeight - container.scrollTop - container.clientHeight < 140;
  if (force || nearBottom) {
    requestAnimationFrame(() => {
      container.scrollTop = container.scrollHeight;
    });
  }
}

function setSidebarCollapsed(collapsed, persist = true) {
  document.body.classList.toggle("sidebar-collapsed", collapsed);
  els["sidebar-open"].hidden = !collapsed;
  if (persist) {
    localStorage.setItem("sidebarCollapsed", collapsed ? "1" : "0");
  }
}

function toggleSidebar(collapsed) {
  setSidebarCollapsed(collapsed, true);
}

function clampNumber(value, min, max, fallback) {
  const parsed = Number.parseInt(value, 10);
  if (Number.isNaN(parsed)) return fallback;
  return Math.max(min, Math.min(max, parsed));
}

init();
