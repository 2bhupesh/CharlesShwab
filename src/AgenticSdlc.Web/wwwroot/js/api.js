// Thin fetch wrapper + named API calls. All endpoints are same-origin under /api.

async function req(method, path, body) {
  const opts = { method, headers: {} };
  if (body !== undefined) { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body); }
  const res = await fetch('/api' + path, opts);
  if (!res.ok) {
    let detail = res.statusText;
    try { const p = await res.json(); detail = p.detail || p.title || detail; } catch { /* ignore */ }
    throw new Error(`${res.status} ${detail}`);
  }
  const ct = res.headers.get('content-type') || '';
  if (res.status === 204) return null;
  return ct.includes('application/json') ? res.json() : res.text();
}

export const api = {
  get: (p) => req('GET', p),
  post: (p, b) => req('POST', p, b),

  scenarios: () => req('GET', '/scenarios'),
  startWorkflow: (b) => req('POST', '/workflows', b),
  listWorkflows: (q = '') => req('GET', '/workflows' + q),
  getWorkflow: (id) => req('GET', `/workflows/${id}`),
  pause: (id) => req('POST', `/workflows/${id}/pause`),
  stop: (id) => req('POST', `/workflows/${id}/stop`),
  resume: (id) => req('POST', `/workflows/${id}/resume`),
  cancel: (id) => req('POST', `/workflows/${id}/cancel`),

  approvals: (id) => req('GET', id ? `/workflows/${id}/approvals` : '/approvals'),
  decideGate: (id, gateId, b) => req('POST', `/workflows/${id}/gates/${gateId}/decision`, b),
  answerClarification: (id, gateId, b) => req('POST', `/workflows/${id}/gates/${gateId}/clarifications`, b),

  artifacts: (id) => req('GET', `/workflows/${id}/artifacts`),
  artifact: (aid) => req('GET', `/artifacts/${aid}`),
  decisions: (id) => req('GET', `/workflows/${id}/decisions`),
  risks: (id) => req('GET', `/workflows/${id}/risks`),
  timeline: (id, afterSeq = 0) => req('GET', `/workflows/${id}/timeline?afterSeq=${afterSeq}`),
  metrics: (id) => req('GET', `/workflows/${id}/metrics`),
  globalMetrics: () => req('GET', '/metrics'),
  reviewPackage: (id) => req('GET', `/workflows/${id}/review-package`),
  health: () => req('GET', '/health'),
};
