// 任务中心：任务列表、状态轮询、日志展开与重试
import { api } from "./api.js";
import { escapeHtml, icon, formatDateTime, toast } from "./ui.js";

const STATUS = {
  queued: { label: "排队中", className: "status-queued", icon: "clock" },
  running: { label: "整理中", className: "status-running", icon: "loader-circle", spin: true },
  succeeded: { label: "已完成", className: "status-succeeded", icon: "circle-check" },
  empty: { label: "无可提炼内容", className: "status-empty", icon: "inbox" },
  failed: { label: "已挂起", className: "status-failed", icon: "circle-alert" }
};

const state = {
  jobs: [],
  logs: new Map(),
  expanded: new Set(),
  timer: null,
  loading: false
};

let initialized = false;

export function initJobs() {
  if (initialized) return;
  initialized = true;
  document.getElementById("job-list").addEventListener("click", onJobListClick);
}

export async function showJobs() {
  await loadJobs();
  schedulePoll();
}

export function hideJobs() {
  clearTimeout(state.timer);
  state.timer = null;
}

async function loadJobs() {
  if (state.loading) return;
  state.loading = true;
  try {
    state.jobs = await api.listJobs();
    renderJobs();
  } catch (error) {
    toast(`读取任务失败：${error.message}`, "error");
  } finally {
    state.loading = false;
  }
}

function schedulePoll() {
  clearTimeout(state.timer);
  const hasActive = state.jobs.some((job) => job.status === "queued" || job.status === "running");
  if (!hasActive) return;
  if (document.getElementById("view-jobs").hidden) return;
  state.timer = setTimeout(async () => {
    await loadJobs();
    schedulePoll();
  }, 3000);
}

function renderJobs() {
  const list = document.getElementById("job-list");
  const empty = document.getElementById("job-empty");

  if (state.jobs.length === 0) {
    list.innerHTML = "";
    empty.hidden = false;
    return;
  }

  empty.hidden = true;
  list.innerHTML = state.jobs.map(jobCardHtml).join("");

  for (const job of state.jobs) {
    if (state.expanded.has(job.id)) {
      const container = list.querySelector(`.job-card[data-id="${job.id}"] .job-logs`);
      if (container) {
        renderLogsInto(container, state.logs.get(job.id));
      }
    }
  }
}

// 单条任务卡片：状态图标、进度条、错误摘要与操作按钮
function jobCardHtml(job) {
  const status = STATUS[job.status] || { label: job.status, className: "", icon: "clock" };
  const total = job.totalChunks || 0;
  const done = Math.min(job.doneChunks || 0, total);
  const percent = total > 0 ? Math.round((done / total) * 100) : (job.status === "running" ? 5 : 0);
  const showProgress = job.status === "queued" || job.status === "running" || total > 0;
  const canRetry = job.status === "failed" || job.status === "succeeded" || job.status === "empty";
  const retryLabel = job.status === "failed" ? "重试" : "重新整理";

  return `
    <article class="job-card" data-id="${job.id}">
      <div class="job-head">
        <div class="job-title-wrap">
          <button type="button" class="linkish job-session" data-session="${job.sessionId}">${icon("notebook-tabs", "icon")}${escapeHtml(job.sessionTitle || `会话 #${job.sessionId}`)}</button>
          <span class="chip ${status.className}">${icon(status.icon, status.spin ? "icon spin" : "icon")}${status.label}</span>
          ${job.retryCount > 0 ? `<span class="job-retry">重试 ${job.retryCount} 次</span>` : ""}
        </div>
        <span class="job-time">${formatDateTime(job.createdAt)}</span>
      </div>

      ${showProgress ? `
        <div class="job-progress">
          <div class="progress-track"><div class="progress-fill" style="width:${percent}%"></div></div>
          <span class="progress-text">${total > 0 ? `${done} / ${total} 片` : "等待开始"}</span>
        </div>` : ""}

      ${job.error ? `<div class="job-error">${escapeHtml(job.error)}</div>` : ""}

      <div class="job-actions">
        <button type="button" class="btn ghost small" data-act="logs">${icon("list-checks", "icon icon-xs")}${state.expanded.has(job.id) ? "收起日志" : "查看日志"}</button>
        ${canRetry ? `<button type="button" class="btn ghost small" data-act="retry">${icon(job.status === "failed" ? "rotate-ccw" : "refresh-cw", "icon icon-xs")}${retryLabel}</button>` : ""}
        <span class="job-finished">${job.finishedAt ? `结束于 ${formatDateTime(job.finishedAt)}` : ""}</span>
      </div>

      <div class="job-logs" ${state.expanded.has(job.id) ? "" : "hidden"}></div>
    </article>`;
}

async function onJobListClick(event) {
  const sessionButton = event.target.closest("[data-session]");
  if (sessionButton) {
    location.hash = `#/chat/${sessionButton.dataset.session}`;
    return;
  }

  const card = event.target.closest(".job-card");
  if (!card) return;
  const id = Number(card.dataset.id);
  const action = event.target.closest("[data-act]")?.dataset.act;
  if (!action) return;

  if (action === "logs") {
    await toggleLogs(id, card);
  } else if (action === "retry") {
    await retryJob(id);
  }
}

// 展开日志时按需加载并缓存，重复展开不再请求
async function toggleLogs(id, card) {
  const container = card.querySelector(".job-logs");
  if (state.expanded.has(id)) {
    state.expanded.delete(id);
    container.hidden = true;
    const button = card.querySelector('[data-act="logs"]');
    if (button) button.innerHTML = `${icon("list-checks", "icon icon-xs")}查看日志`;
    return;
  }

  state.expanded.add(id);
  container.hidden = false;
  const button = card.querySelector('[data-act="logs"]');
  if (button) button.innerHTML = `${icon("list-checks", "icon icon-xs")}收起日志`;

  if (!state.logs.has(id)) {
    container.innerHTML = `<div class="log-loading">${icon("loader-circle", "icon icon-xs spin")}加载日志中…</div>`;
    try {
      const logs = await api.getJobLogs(id);
      state.logs.set(id, logs);
    } catch (error) {
      container.innerHTML = `<div class="log-loading">日志加载失败：${escapeHtml(error.message)}</div>`;
      return;
    }
  }

  renderLogsInto(container, state.logs.get(id));
}

function renderLogsInto(container, logs) {
  if (!logs || logs.length === 0) {
    container.innerHTML = '<div class="log-loading">暂无日志</div>';
    return;
  }

  container.innerHTML = logs.map((log) => `
    <div class="log-line level-${escapeHtml(log.level)}">
      <span class="log-time">${formatDateTime(log.createdAt)}</span>
      <span class="log-step">${escapeHtml(log.step)}</span>
      <span class="log-message">${escapeHtml(log.message)}</span>
      ${log.tokensUsed != null ? `<span class="log-tokens">${log.tokensUsed} tokens</span>` : ""}
    </div>`).join("");
}

// 重试失败任务或重新整理已完成会话
async function retryJob(id) {
  try {
    await api.retryJob(id);
    toast("已重新加入整理队列");
    state.logs.delete(id);
    state.expanded.delete(id);
    await loadJobs();
    schedulePoll();
  } catch (error) {
    toast(`操作失败：${error.message}`, "error");
  }
}
