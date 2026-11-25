// 错题本页面：筛选、卡片渲染、展开、编辑与删除
import { api } from "./api.js";
import {
  escapeHtml,
  icon,
  renderMarkdown,
  formatDateTime,
  toast,
  showConfirm,
  debounce
} from "./ui.js";

const state = {
  category: "",
  tag: "",
  q: "",
  memos: [],
  editingId: null
};

let initialized = false;

export function initMemos() {
  if (initialized) return;
  initialized = true;

  const tabs = document.getElementById("memo-category-tabs");
  tabs.addEventListener("click", (event) => {
    const button = event.target.closest("[data-cat]");
    if (!button) return;
    state.category = button.dataset.cat || "";
    tabs.querySelectorAll("button").forEach((item) => {
      item.classList.toggle("active", (item.dataset.cat || "") === state.category);
    });
    loadMemos();
  });

  const search = document.getElementById("memo-search");
  search.addEventListener("input", debounce(() => {
    state.q = search.value.trim();
    loadMemos();
  }, 350));

  const tag = document.getElementById("memo-tag-filter");
  tag.addEventListener("input", debounce(() => {
    state.tag = tag.value.trim();
    loadMemos();
  }, 400));

  document.getElementById("memo-list").addEventListener("click", onMemoListClick);
  document.getElementById("memo-edit-cancel").addEventListener("click", closeEditModal);
  document.getElementById("memo-edit-save").addEventListener("click", saveEdit);
  document.getElementById("memo-edit-overlay").addEventListener("click", (event) => {
    if (event.target.id === "memo-edit-overlay") closeEditModal();
  });
  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && !document.getElementById("memo-edit-overlay").hidden) {
      closeEditModal();
    }
  });
}

export async function showMemos() {
  await loadMemos();
}

async function loadMemos() {
  try {
    state.memos = await api.listMemos({
      category: state.category,
      tag: state.tag,
      q: state.q
    });
    renderMemos();
  } catch (error) {
    toast(`读取错题本失败：${error.message}`, "error");
  }
}

function renderMemos() {
  const list = document.getElementById("memo-list");
  const empty = document.getElementById("memo-empty");

  if (state.memos.length === 0) {
    list.innerHTML = "";
    empty.hidden = false;
    const hasFilter = state.category || state.tag || state.q;
    empty.querySelector("h2").textContent = hasFilter ? "没有符合条件的错题" : "错题本还是空的";
    empty.querySelector("p").textContent = hasFilter
      ? "换个关键词或清空筛选条件试试。"
      : "把会话归档后，点击「整理为备忘录」，LLM 会自动提炼单词和语法知识点。";
    return;
  }

  empty.hidden = true;
  list.innerHTML = state.memos.map(memoCardHtml).join("");

  requestAnimationFrame(() => {
    list.querySelectorAll(".memo-card").forEach((card) => {
      const body = card.querySelector(".memo-body");
      const expand = card.querySelector(".expand-btn");
      if (body && expand) {
        expand.hidden = body.scrollHeight <= 240;
      }
    });
  });
}

// 单条错题卡片：分类、标题、正文、标签、错题备注与来源
function memoCardHtml(memo) {
  const tags = (memo.tags || "")
    .split(",")
    .map((tag) => tag.trim())
    .filter(Boolean);
  const source = memo.sourceSessionId
    ? `<button type="button" class="linkish" data-source="${memo.sourceSessionId}">${icon("archive", "icon icon-xs")}来自归档会话</button>`
    : "原会话已删";

  return `
    <article class="memo-card" data-id="${memo.id}">
      <div class="memo-card-head">
        <span class="chip category-${memo.category === "单词" ? "word" : "grammar"}">${escapeHtml(memo.category)}</span>
        <div class="memo-card-actions">
          <button type="button" class="icon-text-btn" data-act="edit" title="编辑">${icon("pencil", "icon icon-xs")}编辑</button>
          <button type="button" class="icon-text-btn danger-text" data-act="delete" title="删除">${icon("trash-2", "icon icon-xs")}删除</button>
        </div>
      </div>
      <h3 class="memo-title">${escapeHtml(memo.title)}</h3>
      <div class="memo-body">${renderMarkdown(memo.summaryMd)}</div>
      <button type="button" class="expand-btn" data-act="expand" hidden>${icon("chevron-down", "icon icon-xs")}展开全文</button>
      ${tags.length ? `<div class="tag-row">${tags.map((tag) => `<span class="tag">${escapeHtml(tag)}</span>`).join("")}</div>` : ""}
      ${memo.myNote ? `<div class="my-note"><span>${icon("pencil", "icon icon-xs")}我的错题备注</span>${escapeHtml(memo.myNote).replaceAll("\n", "<br>")}</div>` : ""}
      <div class="memo-foot">
        <span>${source}</span>
        <span>更新于 ${formatDateTime(memo.updatedAt)}</span>
      </div>
    </article>`;
}

// 卡片事件委托：来源跳转、展开/收起、编辑、删除
function onMemoListClick(event) {
  const card = event.target.closest(".memo-card");
  if (!card) return;
  const id = Number(card.dataset.id);
  const memo = state.memos.find((item) => item.id === id);
  if (!memo) return;

  const source = event.target.closest("[data-source]");
  if (source) {
    location.hash = `#/chat/${source.dataset.source}`;
    return;
  }

  const actionButton = event.target.closest("[data-act]");
  const action = actionButton?.dataset.act;
  if (action === "expand") {
    const body = card.querySelector(".memo-body");
    const expanded = body.classList.toggle("expanded");
    actionButton.innerHTML = `${icon(expanded ? "chevron-up" : "chevron-down", "icon icon-xs")}${expanded ? "收起" : "展开全文"}`;
    return;
  }

  if (action === "edit") {
    openEditModal(memo);
    return;
  }

  if (action === "delete") {
    deleteMemo(memo);
  }
}

function openEditModal(memo) {
  state.editingId = memo.id;
  document.getElementById("memo-edit-title").textContent = memo.title;
  document.getElementById("memo-edit-category").value = memo.category;
  document.getElementById("memo-edit-tags").value = memo.tags || "";
  document.getElementById("memo-edit-note").value = memo.myNote || "";

  const overlay = document.getElementById("memo-edit-overlay");
  overlay.hidden = false;
  requestAnimationFrame(() => overlay.classList.add("show"));
}

function closeEditModal() {
  const overlay = document.getElementById("memo-edit-overlay");
  overlay.classList.remove("show");
  setTimeout(() => { overlay.hidden = true; }, 160);
  state.editingId = null;
}

// 保存人工修正：分类、标签、错题备注
async function saveEdit() {
  if (!state.editingId) return;
  const id = state.editingId;
  const button = document.getElementById("memo-edit-save");
  button.disabled = true;
  try {
    await api.updateMemo(id, {
      category: document.getElementById("memo-edit-category").value,
      tags: document.getElementById("memo-edit-tags").value.trim(),
      myNote: document.getElementById("memo-edit-note").value
    });
    closeEditModal();
    toast("已保存");
    await loadMemos();
  } catch (error) {
    toast(`保存失败：${error.message}`, "error");
  } finally {
    button.disabled = false;
  }
}

async function deleteMemo(memo) {
  const ok = await showConfirm(`确定删除「${memo.title}」这条错题吗？`, "删除");
  if (!ok) return;
  try {
    await api.deleteMemo(memo.id);
    toast("已删除");
    await loadMemos();
  } catch (error) {
    toast(`删除失败：${error.message}`, "error");
  }
}
