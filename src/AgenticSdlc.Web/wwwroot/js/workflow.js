import { api } from './api.js';
import { el, mount, clear, badge, ago, fmtTime, toast, escapeHtml, qs } from './ui.js';
import { connectEvents, debounce } from './sse.js';
import { renderGraph, updateNodeState } from './graph.js';
import { renderMarkdown } from './markdown.js';

const id = qs('id');
let detail = null;
let graphSig = '';
let activeTab = 'Activity';
const TABS = ['Activity', 'Artifacts', 'Decisions', 'Risks', 'Metrics'];

async function init() {
  renderTabs();
  await load();
  connectEvents({
    workflowId: id,
    onOpen: load,                       // refetch full state on (re)connect
    onEvent: debounce(load, 200),       // patch on every event
    onLive: setLive,
  });
}

function setLive(on) {
  const h = document.getElementById('health');
  h.classList.toggle('on', !!on);
  h.querySelector('.txt').textContent = on ? 'live' : 'disconnected';
}

async function load() {
  try {
    detail = await api.getWorkflow(id);
  } catch (e) { toast(e.message, 'err'); return; }

  document.getElementById('wf-name').textContent = detail.name || '(unnamed)';
  mount(document.getElementById('wf-status'), badge(detail.status));
  renderStepper();
  renderControls();
  renderGraphIfNeeded();
  renderGates();
  renderActiveTab();
}

function renderStepper() {
  mount(document.getElementById('stepper'), ...detail.phases.map(p =>
    el('div', { class: 'step ' + p.status }, el('span', { class: 'ic' }), p.name)));
}

function renderControls() {
  const s = detail.status;
  const active = s === 'Running' || s === 'AwaitingApproval';
  const terminal = ['Completed', 'Failed', 'Cancelled'].includes(s);
  const btn = (label, cls, on, enabled) => el('button', { class: cls, disabled: !enabled || null, onclick: on }, label);
  mount(document.getElementById('controls'),
    btn('Pause', '', () => ctl(api.pause), active),
    btn('Resume', '', () => ctl(api.resume), s === 'Paused'),
    btn('Safe stop', '', () => ctl(api.stop), active),
    btn('Cancel', 'danger', () => confirm('Cancel this workflow?') && ctl(api.cancel), !terminal),
    detail.status === 'Completed'
      ? el('a', { class: 'btn', href: `/review.html?id=${id}` }, 'Review package')
      : null,
  );
}

async function ctl(fn) {
  try { await fn(id); await load(); }
  catch (e) { toast(e.message, 'err'); }
}

function renderGraphIfNeeded() {
  const sig = detail.nodes.map(n => n.id).sort().join(',');
  const svg = document.getElementById('graph');
  if (sig !== graphSig) {
    graphSig = sig;
    renderGraph(svg, detail.nodes, detail.edges, openNode);
  } else {
    for (const n of detail.nodes) updateNodeState(svg, n.id, n.state);
  }
}

function renderGates() {
  const host = document.getElementById('gates');
  if (!detail.pendingGates.length) { clear(host); return; }
  mount(host, ...detail.pendingGates.map(g =>
    g.kind === 'Clarification' ? clarificationCard(g) : approvalCard(g)));
}

function approvalCard(g) {
  const comment = el('textarea', { rows: 2, placeholder: 'Comment (optional)' });
  const decide = async (decision) => {
    try {
      await api.decideGate(id, g.gateId, { decision, approver: 'reviewer', comment: comment.value || null });
      await load();
    } catch (e) { toast(e.message, 'err'); }
  };
  return el('div', { class: 'gate' },
    el('h4', {}, '⏸ Approval required'),
    el('p', {}, g.description),
    comment,
    el('div', { class: 'row' },
      el('button', { class: 'primary small', onclick: () => decide('approve') }, 'Approve'),
      el('button', { class: 'danger small', onclick: () => decide('reject') }, 'Request changes')));
}

function clarificationCard(g) {
  const inputs = (g.questions || []).map(q => {
    const input = el('textarea', { rows: 2, placeholder: 'Your answer…' });
    return { q, input,
      node: el('div', {}, el('label', {}, q.question), q.rationale ? el('p', { class: 'muted' }, q.rationale) : null, input) };
  });
  const submit = async () => {
    try {
      await api.answerClarification(id, g.gateId, {
        respondent: 'reviewer',
        answers: inputs.map(i => ({ questionId: i.q.questionId, answer: i.input.value })),
      });
      await load();
    } catch (e) { toast(e.message, 'err'); }
  };
  return el('div', { class: 'gate' },
    el('h4', {}, '❓ Clarification needed'),
    el('p', {}, g.description),
    ...inputs.map(i => i.node),
    el('div', { class: 'row' }, el('button', { class: 'primary small', onclick: submit }, 'Submit answers')));
}

function renderTabs() {
  mount(document.getElementById('tabs'), ...TABS.map(t =>
    el('div', { class: 'tab' + (t === activeTab ? ' active' : ''), dataset: { tab: t }, onclick: () => { activeTab = t; renderTabs(); renderActiveTab(); } }, t)));
}

async function renderActiveTab() {
  const body = document.getElementById('tab-body');
  try {
    if (activeTab === 'Activity') return renderActivity(body);
    if (activeTab === 'Artifacts') return renderArtifacts(body);
    if (activeTab === 'Decisions') return renderDecisions(body);
    if (activeTab === 'Risks') return renderRisks(body);
    if (activeTab === 'Metrics') return renderMetrics(body);
  } catch (e) { toast(e.message, 'err'); }
}

async function renderActivity(body) {
  const events = await api.timeline(id);
  mount(body, el('div', { class: 'log' }, ...events.slice().reverse().map(e =>
    el('div', { class: 'line' },
      el('span', { class: 't' }, fmtTime(e.at)),
      el('span', { class: 'ev' }, e.eventType),
      el('span', {}, e.summary)))));
}

async function renderArtifacts(body) {
  const [arts, tree] = await Promise.all([api.artifacts(id), api.get(`/workflows/${id}/workspace/tree`).catch(() => [])]);
  const live = arts.filter(a => a.status !== 'Superseded');
  mount(body,
    el('h3', {}, 'Artifacts'),
    el('ul', { class: 'plain' }, ...live.map(a =>
      el('li', {},
        el('a', { href: '#', onclick: (e) => { e.preventDefault(); openArtifact(a.id); } }, `${a.name}`),
        el('span', { class: 'muted' }, `  ${a.type} · v${a.version}`)))),
    tree.length ? el('h3', {}, 'Generated workspace') : null,
    tree.length ? el('ul', { class: 'plain' }, ...tree.map(path =>
      el('li', {}, el('a', { href: '#', class: 'mono', onclick: (e) => { e.preventDefault(); openFile(path); } }, path)))) : null);
}

async function renderDecisions(body) {
  const ds = await api.decisions(id);
  mount(body, ...ds.map(d =>
    el('div', { class: 'card', style: 'margin-bottom:8px' },
      el('strong', {}, d.title),
      el('p', { class: 'muted', style: 'margin:4px 0' }, d.rationale),
      el('div', { class: 'muted mono', style: 'font-size:11px' }, `${d.agentType} · ${d.requirementIds.join(', ')}`))));
  if (!ds.length) mount(body, el('p', { class: 'muted' }, 'No decisions yet.'));
}

async function renderRisks(body) {
  const rs = await api.risks(id);
  mount(body, el('table', {}, el('thead', {}, el('tr', {}, el('th', {}, 'Severity'), el('th', {}, 'Risk'), el('th', {}, 'Mitigation'))),
    el('tbody', {}, ...rs.map(r =>
      el('tr', {}, el('td', {}, badge(r.severity)), el('td', {}, r.title), el('td', { class: 'muted' }, r.mitigation))))));
  if (!rs.length) mount(body, el('p', { class: 'muted' }, 'No risks recorded.'));
}

async function renderMetrics(body) {
  const m = await api.metrics(id);
  const tile = (k, v) => el('div', { class: 'tile' }, el('div', { class: 'k' }, k), el('div', { class: 'v' }, v));
  const pct = (x) => `${Math.round((x || 0) * 100)}%`;
  mount(body, el('div', { class: 'tiles' },
    tile('Nodes', `${m.nodesSucceeded}/${m.nodesTotal}`),
    tile('Agent success', pct(m.agentSuccessRate)),
    tile('Validation pass', pct(m.validationPassRate)),
    tile('Requirement coverage', pct(m.requirementCoverage)),
    tile('Retries', m.retries),
    tile('Rollbacks', m.rollbacks),
    tile('Latency', m.workflowLatencySeconds ? `${m.workflowLatencySeconds.toFixed(1)}s` : '—'),
    tile('Approval time', m.meanApprovalSeconds ? `${m.meanApprovalSeconds.toFixed(1)}s` : '—'),
    tile('Tokens', `${m.inputTokens}/${m.outputTokens}`),
    tile('Agent calls', m.agentInvocations)));
}

function openNode(n) {
  const artForNode = () => api.artifacts(id).then(a => a.filter(x => x.producedByNodeId === n.id && x.status !== 'Superseded'));
  showModal(n.label, el('div', {},
    el('p', { class: 'muted' }, `${n.agentType} · ${n.phase} · ${n.state}${n.error ? ' · ' + n.error : ''}`),
    el('p', { class: 'muted' }, `Attempt ${n.attempt}${n.startedAt ? ' · started ' + fmtTime(n.startedAt) : ''}`)));
}

async function openArtifact(aid) {
  const a = await api.artifact(aid);
  showModal(a.summary.name, artifactBody(a));
}

async function openFile(path) {
  const text = await api.get(`/workflows/${id}/workspace/file?path=${encodeURIComponent(path)}`);
  showModal(path, el('pre', { class: 'code' }, text));
}

function artifactBody(a) {
  const ct = a.summary.contentType;
  let content;
  if (a.content == null) content = el('p', { class: 'muted' }, 'No inline content.');
  else if (ct === 'markdown') content = el('div', { class: 'md', html: renderMarkdown(a.content) });
  else if (ct === 'json') { try { content = el('pre', { class: 'code' }, JSON.stringify(JSON.parse(a.content), null, 2)); } catch { content = el('pre', { class: 'code' }, a.content); } }
  else content = el('pre', { class: 'code' }, a.content);
  return el('div', {},
    el('div', { class: 'lineage muted', style: 'margin-bottom:8px' },
      `${a.summary.type} · v${a.summary.version} · requirements: ${a.lineage.requirementIds.join(', ') || '—'}`),
    content);
}

function showModal(title, bodyNode) {
  const root = document.getElementById('modal-root');
  const close = () => clear(root);
  mount(root, el('div', { class: 'modal-bg', onclick: (e) => { if (e.target.classList.contains('modal-bg')) close(); } },
    el('div', { class: 'modal' },
      el('div', { class: 'head' }, el('h3', {}, title), el('div', { style: 'flex:1' }), el('button', { class: 'small', onclick: close }, '✕')),
      el('div', { class: 'body' }, bodyNode))));
}

init();
