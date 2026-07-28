// Hand-rolled layered DAG layout rendered as SVG. No dependency: longest-path layering + barycenter
// ordering keeps a ~13-node SDLC graph readable, and a single node's fill patches on each SSE event.

const NS = 'http://www.w3.org/2000/svg';
const NODE_W = 152, NODE_H = 46, H_GAP = 56, V_GAP = 20, PAD = 24;

function svg(tag, attrs = {}) {
  const n = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
  return n;
}

export function renderGraph(svgEl, nodes, edges, onNodeClick) {
  const byId = new Map(nodes.map(n => [n.id, n]));
  const preds = new Map(nodes.map(n => [n.id, []]));
  const succs = new Map(nodes.map(n => [n.id, []]));
  for (const e of edges) {
    if (!byId.has(e.fromNodeId) || !byId.has(e.toNodeId)) continue;
    succs.get(e.fromNodeId).push(e.toNodeId);
    preds.get(e.toNodeId).push(e.fromNodeId);
  }

  // Longest-path layering.
  const layer = new Map(nodes.map(n => [n.id, 0]));
  for (let iter = 0; iter < nodes.length; iter++) {
    let changed = false;
    for (const e of edges) {
      if (!byId.has(e.fromNodeId) || !byId.has(e.toNodeId)) continue;
      const nl = layer.get(e.fromNodeId) + 1;
      if (nl > layer.get(e.toNodeId)) { layer.set(e.toNodeId, nl); changed = true; }
    }
    if (!changed) break;
  }

  const layers = [];
  for (const n of nodes) { (layers[layer.get(n.id)] ??= []).push(n.id); }

  // Barycenter ordering: a couple of sweeps to reduce crossings.
  const orderIndex = new Map();
  layers.forEach(col => col.forEach((id, i) => orderIndex.set(id, i)));
  const bary = (id, neigh) => {
    const ns = neigh.get(id);
    if (!ns.length) return orderIndex.get(id);
    return ns.reduce((a, x) => a + orderIndex.get(x), 0) / ns.length;
  };
  for (let sweep = 0; sweep < 3; sweep++) {
    for (let l = 1; l < layers.length; l++) {
      layers[l].sort((a, b) => bary(a, preds) - bary(b, preds));
      layers[l].forEach((id, i) => orderIndex.set(id, i));
    }
    for (let l = layers.length - 2; l >= 0; l--) {
      layers[l].sort((a, b) => bary(a, succs) - bary(b, succs));
      layers[l].forEach((id, i) => orderIndex.set(id, i));
    }
  }

  // Positions.
  const pos = new Map();
  let maxRow = 0;
  layers.forEach((col, l) => {
    col.forEach((id, i) => {
      pos.set(id, { x: PAD + l * (NODE_W + H_GAP), y: PAD + i * (NODE_H + V_GAP) });
      maxRow = Math.max(maxRow, i);
    });
  });
  const width = PAD * 2 + layers.length * NODE_W + (layers.length - 1) * H_GAP;
  const height = PAD * 2 + (maxRow + 1) * NODE_H + maxRow * V_GAP;

  while (svgEl.firstChild) svgEl.removeChild(svgEl.firstChild);
  const defs = svg('defs');
  const marker = svg('marker', { id: 'arrow', viewBox: '0 0 8 8', refX: 7, refY: 4, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse' });
  marker.append(svg('path', { d: 'M0,0 L8,4 L0,8 z', fill: '#3a4453' }));
  defs.append(marker); svgEl.append(defs);

  // Edges.
  for (const e of edges) {
    const a = pos.get(e.fromNodeId), b = pos.get(e.toNodeId);
    if (!a || !b) continue;
    const x1 = a.x + NODE_W, y1 = a.y + NODE_H / 2, x2 = b.x, y2 = b.y + NODE_H / 2;
    const path = svg('path', {
      class: 'edge', 'marker-end': 'url(#arrow)',
      d: `M${x1},${y1} C${x1 + 40},${y1} ${x2 - 40},${y2} ${x2},${y2}`,
    });
    if (e.kind === 'Soft') path.setAttribute('stroke-dasharray', '3 3');
    svgEl.append(path);
  }

  // Nodes.
  for (const n of nodes) {
    const p = pos.get(n.id);
    const g = svg('g', { class: 'node', 'data-node-id': n.id, 'data-state': n.state, transform: `translate(${p.x},${p.y})` });
    g.append(svg('rect', { width: NODE_W, height: NODE_H, rx: 6 }));
    const label = svg('text', { class: 'label', x: 10, y: 19 });
    label.textContent = truncate(n.label, 20);
    const sub = svg('text', { class: 'sub', x: 10, y: 35 });
    sub.textContent = n.agentType;
    g.append(label, sub);
    g.addEventListener('click', () => onNodeClick?.(n));
    svgEl.append(g);
  }

  svgEl.setAttribute('viewBox', `0 0 ${width} ${height}`);
  attachPanZoom(svgEl, width, height);
}

export function updateNodeState(svgEl, nodeId, state) {
  const g = svgEl.querySelector(`[data-node-id="${CSS.escape(nodeId)}"]`);
  if (g) g.setAttribute('data-state', state);
}

function truncate(s, n) { return s.length > n ? s.slice(0, n - 1) + '…' : s; }

function attachPanZoom(svgEl, w, h) {
  let vb = { x: 0, y: 0, w, h };
  const apply = () => svgEl.setAttribute('viewBox', `${vb.x} ${vb.y} ${vb.w} ${vb.h}`);
  svgEl.onwheel = (e) => {
    e.preventDefault();
    const scale = e.deltaY > 0 ? 1.1 : 0.9;
    const r = svgEl.getBoundingClientRect();
    const mx = vb.x + (e.clientX - r.left) / r.width * vb.w;
    const my = vb.y + (e.clientY - r.top) / r.height * vb.h;
    vb.w *= scale; vb.h *= scale;
    vb.x = mx - (e.clientX - r.left) / r.width * vb.w;
    vb.y = my - (e.clientY - r.top) / r.height * vb.h;
    apply();
  };
  let drag = null;
  svgEl.onmousedown = (e) => { drag = { x: e.clientX, y: e.clientY, vx: vb.x, vy: vb.y }; };
  window.addEventListener('mousemove', (e) => {
    if (!drag) return;
    const r = svgEl.getBoundingClientRect();
    vb.x = drag.vx - (e.clientX - drag.x) / r.width * vb.w;
    vb.y = drag.vy - (e.clientY - drag.y) / r.height * vb.h;
    apply();
  });
  window.addEventListener('mouseup', () => { drag = null; });
}
