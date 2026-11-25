// 与后端 API 交互的轻量封装

// 统一 JSON 请求：非 2xx 抛出带状态码和响应片段的错误
async function request(method, url, body) {
  const options = { method };
  if (body !== undefined) {
    options.headers = { "Content-Type": "application/json" };
    options.body = JSON.stringify(body);
  }
  const response = await fetch(url, options);
  if (!response.ok) {
    let detail = "";
    try {
      detail = await response.text();
    } catch {
      // ignore
    }
    const message = detail ? `${response.status}: ${detail.slice(0, 300)}` : `请求失败（HTTP ${response.status}）`;
    throw new Error(message);
  }
  if (response.status === 204) return null;
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

// 所有 REST 端点集中在这里，便于统一改路径
export const api = {
  getSettings: () => request("GET", "/api/settings"),
  saveSettings: (dto) => request("PUT", "/api/settings", dto),
  testSettings: (dto) => request("POST", "/api/settings/test", dto),
  fetchModels: (dto) => request("POST", "/api/settings/models", dto),

  listSessions: (archived) => request("GET", archived === undefined ? "/api/sessions" : `/api/sessions?archived=${archived}`),
  createSession: () => request("POST", "/api/sessions"),
  getSession: (id) => request("GET", `/api/sessions/${id}`),
  renameSession: (id, title) => request("PATCH", `/api/sessions/${id}`, { title }),
  archiveSession: (id) => request("POST", `/api/sessions/${id}/archive`),
  deleteSession: (id) => request("DELETE", `/api/sessions/${id}`),
  triggerMemoJob: (id) => request("POST", `/api/sessions/${id}/memo-job`),

  listJobs: () => request("GET", "/api/memo-jobs"),
  getJobLogs: (id) => request("GET", `/api/memo-jobs/${id}/logs`),
  retryJob: (id) => request("POST", `/api/memo-jobs/${id}/retry`),

  listMemos: ({ category, tag, q } = {}) => {
    const params = new URLSearchParams();
    if (category) params.set("category", category);
    if (tag) params.set("tag", tag);
    if (q) params.set("q", q);
    const query = params.toString();
    return request("GET", `/api/memos${query ? `?${query}` : ""}`);
  },
  updateMemo: (id, dto) => request("PATCH", `/api/memos/${id}`, dto),
  deleteMemo: (id) => request("DELETE", `/api/memos/${id}`)
};

/**
 * 读取聊天 SSE 流，把 delta/done/error 事件逐条交给回调。
 * @param {'/api/chat'|'/api/chat/regenerate'} endpoint
 * @param {object} payload
 * @param {(event: object) => void} onEvent
 */
export async function streamChat(endpoint, payload, onEvent) {
  const response = await fetch(endpoint, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    let detail = "";
    try {
      detail = await response.text();
    } catch {
      // ignore
    }
    throw new Error(detail ? detail.slice(0, 300) : `请求失败（HTTP ${response.status}）`);
  }

  if (!response.body) {
    throw new Error("当前浏览器不支持流式读取");
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let done = false;

  const dispatch = (data) => {
    if (!data) return;
    onEvent(data);
    if (data.type === "done" || data.type === "error") {
      done = true;
    }
  };

  while (true) {
    const { value, done: readerDone } = await reader.read();
    if (readerDone) break;
    buffer += decoder.decode(value, { stream: true });

    let index;
    while ((index = buffer.indexOf("\n\n")) >= 0) {
      const raw = buffer.slice(0, index);
      buffer = buffer.slice(index + 2);
      for (const line of raw.split("\n")) {
        if (!line.startsWith("data:")) continue;
        const text = line.slice(5).trim();
        if (!text) continue;
        try {
          dispatch(JSON.parse(text));
        } catch {
          // 忽略格式异常的 SSE 数据块
        }
      }
    }
  }

  if (!done) {
    throw new Error("连接中断，未收到完整回答");
  }
}
