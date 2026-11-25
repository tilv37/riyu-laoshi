// 设置抽屉：连接配置、场景模型、思考强度、提示词与自动获取模型
import { api } from "./api.js";
import { toast, escapeHtml, icon } from "./ui.js";

let onSavedCallback = null;
let initialized = false;
let modelsRequestId = 0;

const FIELDS = {
  baseUrl: "set-base-url",
  apiKey: "set-api-key",
  model: "set-model",
  chatModel: "set-chat-model",
  memoModel: "set-memo-model",
  titleModel: "set-title-model",
  reasoningEffort: "set-reasoning-effort",
  temperature: "set-temperature",
  chatSystemPrompt: "set-chat-prompt",
  memoSystemPrompt: "set-memo-prompt",
  titleSystemPrompt: "set-title-prompt",
  defaultContextRounds: "set-default-rounds",
  defaultIncludeContext: "set-default-include",
  autoArchiveRounds: "set-auto-rounds",
  autoArchiveChars: "set-auto-chars",
  chunkChars: "set-chunk-chars",
  chunkOverlap: "set-chunk-overlap"
};

function el(key) {
  return document.getElementById(FIELDS[key]);
}

export function initSettings(onSaved) {
  if (initialized) return;
  initialized = true;
  onSavedCallback = onSaved;

  document.getElementById("settings-close").addEventListener("click", closeSettings);
  document.getElementById("settings-overlay").addEventListener("click", closeSettings);
  document.getElementById("settings-save").addEventListener("click", saveSettings);
  document.getElementById("test-connection-btn").addEventListener("click", testConnection);
  document.getElementById("toggle-key").addEventListener("click", toggleKeyVisibility);
  document.getElementById("fetch-models-btn").addEventListener("click", () => fetchModels(false));

  el("baseUrl").addEventListener("change", () => {
    if (shouldAutoFetchModels()) fetchModels(true);
  });
  el("apiKey").addEventListener("change", () => {
    if (shouldAutoFetchModels()) fetchModels(true);
  });

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !document.getElementById("settings-drawer").hidden) {
      closeSettings();
    }
  });
}

export async function openSettings() {
  const drawer = document.getElementById("settings-drawer");
  const overlay = document.getElementById("settings-overlay");
  const result = document.getElementById("test-result");
  result.hidden = true;

  try {
    const settings = await api.getSettings();
    fillForm(settings);
  } catch (error) {
    toast(`读取设置失败：${error.message}`, "error");
    return;
  }

  overlay.hidden = false;
  drawer.hidden = false;
  requestAnimationFrame(() => {
    overlay.classList.add("show");
    drawer.classList.add("show");
  });
  document.body.classList.add("drawer-open");

  if (shouldAutoFetchModels()) {
    fetchModels(true);
  } else {
    const hint = document.getElementById("model-hint");
    hint.className = "model-hint";
    hint.textContent = "填写 API Key 后会自动获取模型；本地接口可免 Key 直接点击「获取模型」。";
  }
}

function closeSettings() {
  const drawer = document.getElementById("settings-drawer");
  const overlay = document.getElementById("settings-overlay");
  overlay.classList.remove("show");
  drawer.classList.remove("show");
  document.body.classList.remove("drawer-open");
  setTimeout(() => {
    overlay.hidden = true;
    drawer.hidden = true;
  }, 200);
}

function fillForm(settings) {
  el("baseUrl").value = settings.baseUrl ?? "";
  el("apiKey").value = settings.apiKey ?? "";
  el("model").value = settings.model ?? "";
  el("chatModel").value = settings.chatModel ?? "";
  el("memoModel").value = settings.memoModel ?? "";
  el("titleModel").value = settings.titleModel ?? "";
  el("reasoningEffort").value = settings.reasoningEffort ?? "";
  el("temperature").value = String(settings.temperature ?? 0.7);
  el("chatSystemPrompt").value = settings.chatSystemPrompt ?? "";
  el("memoSystemPrompt").value = settings.memoSystemPrompt ?? "";
  el("titleSystemPrompt").value = settings.titleSystemPrompt ?? "";
  el("defaultContextRounds").value = String(settings.defaultContextRounds ?? 5);
  el("defaultIncludeContext").checked = Boolean(settings.defaultIncludeContext);
  el("autoArchiveRounds").value = String(settings.autoArchiveRounds ?? 100);
  el("autoArchiveChars").value = String(settings.autoArchiveChars ?? 100000);
  el("chunkChars").value = String(settings.chunkChars ?? 20000);
  el("chunkOverlap").value = String(settings.chunkOverlap ?? 1000);
}

// 把当前表单值整理成后端 SettingsDto
function formDto() {
  return {
    baseUrl: el("baseUrl").value.trim(),
    apiKey: el("apiKey").value.trim(),
    model: el("model").value.trim(),
    chatModel: el("chatModel").value.trim(),
    memoModel: el("memoModel").value.trim(),
    titleModel: el("titleModel").value.trim(),
    reasoningEffort: el("reasoningEffort").value,
    temperature: numberOr(el("temperature").value, 0.7),
    chatSystemPrompt: el("chatSystemPrompt").value,
    memoSystemPrompt: el("memoSystemPrompt").value,
    titleSystemPrompt: el("titleSystemPrompt").value,
    defaultContextRounds: intOr(el("defaultContextRounds").value, 5),    defaultIncludeContext: el("defaultIncludeContext").checked,
    autoArchiveRounds: intOr(el("autoArchiveRounds").value, 100),
    autoArchiveChars: intOr(el("autoArchiveChars").value, 100000),
    chunkChars: intOr(el("chunkChars").value, 20000),
    chunkOverlap: intOr(el("chunkOverlap").value, 1000)
  };
}

// 保存设置；按钮显示加载态并防止重复提交
async function saveSettings() {
  const button = document.getElementById("settings-save");
  button.disabled = true;
  button.innerHTML = `${icon("loader-circle", "icon icon-sm spin")}保存中…`;
  try {
    const saved = await api.saveSettings(formDto());
    closeSettings();
    if (onSavedCallback) onSavedCallback(saved);
  } catch (error) {
    toast(`保存失败：${error.message}`, "error");
  } finally {
    button.disabled = false;
    button.innerHTML = `${icon("circle-check", "icon icon-sm")}保存设置`;
  }
}

// 用当前表单（可能尚未保存）测试连接，成功后再刷新模型列表
async function testConnection() {
  const button = document.getElementById("test-connection-btn");
  const result = document.getElementById("test-result");
  button.disabled = true;
  button.innerHTML = `${icon("loader-circle", "icon icon-sm spin")}测试中…`;
  result.hidden = false;
  result.className = "test-result";
  result.textContent = "正在请求模型…";

  try {
    const test = await api.testSettings(formDto());
    if (test.ok) {
      result.classList.add("ok");
      result.textContent = `${test.message}${test.latencyMs != null ? `（${test.latencyMs} ms）` : ""}`;
      fetchModels(true);
    } else {
      result.classList.add("fail");
      result.textContent = test.message || "连接失败";
    }
  } catch (error) {
    result.classList.add("fail");
    result.textContent = error.message;
  } finally {
    button.disabled = false;
    button.innerHTML = `${icon("key-round", "icon icon-sm")}测试连接`;
  }
}

function toggleKeyVisibility() {
  const input = el("apiKey");
  const button = document.getElementById("toggle-key");
  const visible = input.type === "text";
  input.type = visible ? "password" : "text";
  button.textContent = visible ? "显示" : "隐藏";
}

// 有 Key，或接口地址是本机时，打开设置自动拉取模型列表
function shouldAutoFetchModels() {
  const base = el("baseUrl").value.trim();
  const key = el("apiKey").value.trim();
  if (!base) return false;
  if (key) return true;
  try {
    const url = new URL(base);
    return ["localhost", "127.0.0.1", "0.0.0.0", "::1", "[::1]"].includes(url.hostname);
  } catch {
    return false;
  }
}

// 拉取 /models 并填充共享 datalist；requestId 避免旧请求覆盖新结果
async function fetchModels(auto) {
  const button = document.getElementById("fetch-models-btn");
  const hint = document.getElementById("model-hint");
  const options = document.getElementById("model-options");

  if (!el("baseUrl").value.trim()) {
    hint.className = "model-hint";
    hint.textContent = "填写接口地址后会自动获取可用模型。";
    return;
  }

  const requestId = ++modelsRequestId;
  button.disabled = true;
  button.innerHTML = `${icon("loader-circle", "icon icon-sm spin")}获取中…`;
  hint.className = "model-hint";
  hint.textContent = auto ? "正在自动获取可用模型…" : "正在获取可用模型…";
  options.innerHTML = "";

  try {
    const result = await api.fetchModels(formDto());
    if (requestId !== modelsRequestId) return;

    if (!result.ok) {
      hint.classList.add("fail");
      hint.textContent = result.message;
      return;
    }

    options.innerHTML = result.models
      .map((id) => `<option value="${escapeHtml(id)}"></option>`)
      .join("");

    hint.classList.add("ok");
    hint.textContent = `${result.message}，点击模型输入框可从下拉框选择。`;

    if (!el("model").value.trim() && result.models.length > 0) {
      el("model").value = result.models[0];
    }
  } catch (error) {
    if (requestId !== modelsRequestId) return;
    hint.classList.add("fail");
    hint.textContent = error.message;
  } finally {
    if (requestId === modelsRequestId) {
      button.disabled = false;
      button.innerHTML = `${icon("refresh-cw", "icon icon-sm")}获取模型`;
    }
  }
}

function intOr(value, fallback) {
  const parsed = Number.parseInt(value, 10);
  return Number.isNaN(parsed) ? fallback : parsed;
}

function numberOr(value, fallback) {
  const parsed = Number.parseFloat(value);
  return Number.isNaN(parsed) ? fallback : parsed;
}
