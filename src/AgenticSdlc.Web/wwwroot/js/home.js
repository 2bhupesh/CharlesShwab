import { api } from './api.js';
import { el, mount, clear, badge, ago, toast } from './ui.js';
import { connectEvents, debounce } from './sse.js';

let selected = null;
let scenarios = [];

async function init() {
  await loadHealth();
  scenarios = await api.scenarios();
  renderScenarios();
  selectScenario(scenarios[0]);
  await refreshList();

  document.getElementById('start').addEventListener('click', start);

  // Live-refresh the workflow list from the global event stream.
  const refresh = debounce(refreshList, 250);
  connectEvents({
    onEvent: refresh,
    onLive: (on) => setLive(on),
  });
}

function setLive(on) {
  const h = document.getElementById('health');
  h.classList.toggle('on', !!on);
  h.querySelector('.txt').textContent = on ? 'live' : 'disconnected';
}

async function loadHealth() {
  try {
    const h = await api.health();
    document.querySelector('#health .txt').textContent = `${h.provider} · ${h.model}`;
  } catch { /* ignore */ }
}

function renderScenarios() {
  const host = document.getElementById('scenarios');
  mount(host, ...scenarios.map(s =>
    el('div', { class: 'scenario-card', dataset: { id: s.id }, onclick: () => selectScenario(s) },
      el('h3', {}, s.title),
      el('p', {}, s.description),
      s.requiresExistingCodebase ? el('span', { class: 'tag' }, 'needs existing codebase') : null,
    )));
}

function selectScenario(s) {
  selected = s;
  document.querySelectorAll('.scenario-card').forEach(c => c.classList.toggle('selected', c.dataset.id === s.id));
  document.getElementById('requirement').value = s.sampleRequirement;
  document.getElementById('brownfield-seed').style.display = s.id === 'brownfield' ? 'block' : 'none';
  if (s.id === 'brownfield') loadSources();
}

async function loadSources() {
  const list = await api.listWorkflows('?scenario=greenfield');
  const sel = document.getElementById('source');
  clear(sel);
  sel.append(el('option', { value: '' }, '— none (use bundled sample) —'));
  list.filter(w => w.status === 'Completed').forEach(w =>
    sel.append(el('option', { value: w.id }, `${w.name} (${w.id.slice(0, 8)})`)));
}

async function start() {
  const btn = document.getElementById('start');
  btn.disabled = true;
  try {
    const body = {
      scenario: selected.id,
      requirement: document.getElementById('requirement').value.trim(),
      name: document.getElementById('name').value.trim() || null,
      sourceWorkflowId: document.getElementById('source')?.value || null,
    };
    const res = await api.startWorkflow(body);
    location.href = `/workflow.html?id=${res.workflowId}`;
  } catch (e) {
    toast(e.message, 'err');
    btn.disabled = false;
  }
}

async function refreshList() {
  const list = await api.listWorkflows();
  const body = document.getElementById('workflow-list');
  mount(body, ...list.map(w =>
    el('tr', {},
      el('td', {}, el('a', { href: `/workflow.html?id=${w.id}` }, w.name || '(unnamed)')),
      el('td', { class: 'muted' }, w.scenario),
      el('td', {}, badge(w.status)),
      el('td', { class: 'muted' }, w.currentPhase),
      el('td', { class: 'mono' }, `${w.nodesSucceeded}/${w.nodesTotal}${w.nodesFailed ? ` ✗${w.nodesFailed}` : ''}`),
      el('td', {}, w.pendingApprovals ? badge('AwaitingApproval') : el('span', { class: 'muted' }, '—')),
      el('td', { class: 'muted' }, ago(w.createdAt)),
    )));
  if (list.length === 0) mount(body, el('tr', {}, el('td', { colspan: 7, class: 'muted' }, 'No workflows yet.')));
}

init();
