import { el, mount, qs, toast } from './ui.js';
import { renderMarkdown } from './markdown.js';

const id = qs('id');

async function init() {
  document.getElementById('download').href = `/api/workflows/${id}/review-package.md`;
  const content = document.getElementById('content');
  try {
    const res = await fetch(`/api/workflows/${id}/review-package.md`);
    if (!res.ok) throw new Error(`Review package not available (status ${res.status}). The workflow may not be complete.`);
    const md = await res.text();
    content.innerHTML = renderMarkdown(md);
  } catch (e) {
    mount(content, el('p', { class: 'muted' }, e.message));
    toast(e.message, 'err');
  }
}

init();
