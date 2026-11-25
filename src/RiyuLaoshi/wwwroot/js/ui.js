// 前端通用工具：转义、图标、Markdown、提示与弹窗

// HTML 转义，所有用户输入拼进 innerHTML 前都必须先过这一层
export function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

// 生成内联 SVG 图标，name 对应 index.html 雪碧图中的 symbol id
export function icon(name, className = "icon") {
  return `<svg class="${className}" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
}

// Markdown 渲染：marked 转 HTML 后用 DOMPurify 消毒，防止 LLM 输出注入脚本
export function renderMarkdown(value) {
  const source = String(value ?? "");
  try {
    const html = window.marked.parse(source, { breaks: true, gfm: true });
    return window.DOMPurify.sanitize(html, {
      USE_PROFILES: { html: true },
      ADD_ATTR: ["target", "rel"]
    });
  } catch {
    return escapeHtml(source).replaceAll("\n", "<br>");
  }
}

// 会话列表用的短时间：当天显示时分，跨天显示月日
export function formatTime(iso) {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const now = new Date();
  const sameDay = date.toDateString() === now.toDateString();
  if (sameDay) {
    return date.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit" });
  }
  return date.toLocaleDateString("zh-CN", { month: "2-digit", day: "2-digit" });
}

// 任务与错题本用的完整时间：月日 + 时分
export function formatDateTime(iso) {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleString("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit"
  });
}

// 每条提示各自计时，避免连续提示时旧的提示永远不消失
export function toast(message, kind = "") {
  const container = document.getElementById("toast-container");
  if (!container) return;
  const el = document.createElement("div");
  el.className = `toast ${kind}`;
  el.textContent = message;
  container.appendChild(el);
  requestAnimationFrame(() => el.classList.add("show"));
  setTimeout(() => {
    el.classList.remove("show");
    setTimeout(() => el.remove(), 220);
  }, 3200);
  // 最多保留 3 条，防止连续操作刷屏
  while (container.children.length > 3) {
    container.firstElementChild.remove();
  }
}

export function debounce(fn, delay = 300) {
  let timer = null;
  return (...args) => {
    clearTimeout(timer);
    timer = setTimeout(() => fn(...args), delay);
  };
}

// ---------- 确认弹窗 ----------

// Promise 形式的确认弹窗；点击遮罩或 Esc 视为取消
export function showConfirm(message, confirmLabel = "确认") {
  const overlay = document.getElementById("confirm-overlay");
  const text = document.getElementById("confirm-message");
  const ok = document.getElementById("confirm-ok");
  const cancel = document.getElementById("confirm-cancel");
  if (!overlay) return Promise.resolve(false);

  text.textContent = message;
  ok.textContent = confirmLabel;
  overlay.hidden = false;
  requestAnimationFrame(() => overlay.classList.add("show"));

  return new Promise((resolve) => {
    const finish = (value) => {
      overlay.classList.remove("show");
      setTimeout(() => { overlay.hidden = true; }, 160);
      ok.removeEventListener("click", onOk);
      cancel.removeEventListener("click", onCancel);
      overlay.removeEventListener("click", onOverlay);
      document.removeEventListener("keydown", onKey);
      resolve(value);
    };
    const onOk = () => finish(true);
    const onCancel = () => finish(false);
    const onOverlay = (event) => { if (event.target === overlay) finish(false); };
    const onKey = (event) => { if (event.key === "Escape") finish(false); };
    ok.addEventListener("click", onOk);
    cancel.addEventListener("click", onCancel);
    overlay.addEventListener("click", onOverlay);
    document.addEventListener("keydown", onKey);
  });
}

// ---------- 重命名弹窗 ----------

// Promise 形式的重命名弹窗；返回 null 表示取消
export function showRename(currentTitle) {
  const overlay = document.getElementById("rename-overlay");
  const input = document.getElementById("rename-input");
  const ok = document.getElementById("rename-ok");
  const cancel = document.getElementById("rename-cancel");
  if (!overlay) return Promise.resolve(null);

  input.value = currentTitle ?? "";
  overlay.hidden = false;
  requestAnimationFrame(() => {
    overlay.classList.add("show");
    input.focus();
    input.select();
  });

  return new Promise((resolve) => {
    const finish = (value) => {
      overlay.classList.remove("show");
      setTimeout(() => { overlay.hidden = true; }, 160);
      ok.removeEventListener("click", onOk);
      cancel.removeEventListener("click", onCancel);
      overlay.removeEventListener("click", onOverlay);
      document.removeEventListener("keydown", onKey);
      resolve(value);
    };
    const submit = () => {
      const value = input.value.trim();
      if (value) finish(value);
    };
    const onOk = () => submit();
    const onCancel = () => finish(null);
    const onOverlay = (event) => { if (event.target === overlay) finish(null); };
    const onKey = (event) => {
      if (event.key === "Escape") finish(null);
      if (event.key === "Enter" && !event.isComposing) submit();
    };
    ok.addEventListener("click", onOk);
    cancel.addEventListener("click", onCancel);
    overlay.addEventListener("click", onOverlay);
    document.addEventListener("keydown", onKey);
  });
}

// 输入框随内容增高，最高不超过 maxHeight
export function autoGrow(textarea, maxHeight = 200) {
  textarea.style.height = "auto";
  textarea.style.height = Math.min(textarea.scrollHeight, maxHeight) + "px";
}

