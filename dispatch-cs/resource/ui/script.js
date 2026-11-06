// NUI side of the dispatch console. The server sends the call list; this page renders it and
// posts intent back. Every rule (duty, ownership, position) is enforced on the server.
const $ = (id) => document.getElementById(id);
const resource = typeof GetParentResourceName === 'function' ? GetParentResourceName() : 'dispatch-cs';
const panel = $('panel');

const post = (name, data = {}) =>
  fetch(`https://${resource}/${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify(data),
  }).catch(() => {});

let role = '';
let pay = 0;
let calls = [];
let receivedAt = 0;

const money = (n) => '$' + Number(n || 0).toLocaleString('en-US');

function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function button(label, cls, onClick) {
  const b = el('button', 'act' + (cls ? ' ' + cls : ''), label);
  b.onclick = onClick;
  return b;
}

const statusLabel = (s) => ({
  open: 'OPEN',
  claimed: 'CLAIMED',
  enroute: 'EN ROUTE',
  onscene: 'ON SCENE',
}[s] || s.toUpperCase());

function ageText(seconds) {
  const s = Math.max(0, Math.floor(seconds || 0));
  if (s < 60) return s + 's';
  return Math.floor(s / 60) + 'm ' + String(s % 60).padStart(2, '0') + 's';
}

const distText = (m) => (m >= 1000 ? (m / 1000).toFixed(1) + ' km' : Math.round(m || 0) + ' m');

// ---------- rendering ----------
function render() {
  $('title').textContent =
    role === 'ems' ? 'Medical Dispatch' : role === 'admin' ? 'Dispatch Overview' : 'Police Dispatch';

  const list = $('calls');
  list.replaceChildren();

  if (!calls.length) {
    list.append(el('div', 'empty', 'No active calls. Stay on duty.'));
  } else {
    for (const call of calls) list.append(card(call));
  }

  const count = calls.length;
  $('footer').textContent = role === 'admin'
    ? `${count} active call(s). Admin overview - respond while on duty as police or EMS.`
    : `${count} active call(s). ${pay > 0 ? 'Resolved calls pay ' + money(pay) + ' to your bank.' : ''}`;
}

function card(call) {
  const box = el('article', `call ${call.category}${call.mine ? ' mine' : ''}`);

  const top = el('div', 'top');
  top.append(el('span', 'badge', call.category === 'medical' ? 'MEDICAL' : 'POLICE'));
  top.append(el('span', 'id', '#' + call.id));
  top.append(el('span', 'status ' + call.status, statusLabel(call.status)));
  top.append(el('span', 'spacer'));
  const age = el('span', 'age', ageText(call.age));
  age.dataset.age = call.age;
  top.append(age);
  box.append(top);

  box.append(el('p', 'msg', call.message));

  const loc = el('div', 'loc');
  const place = el('span');
  place.append(el('strong', '', call.area));
  loc.append(place, el('span', '', distText(call.dist)));
  box.append(loc);

  const meta = el('div', 'loc');
  meta.append(el('span', '', 'Caller: ' + call.caller));
  box.append(meta);

  if (call.mine) {
    const actions = el('div', 'actions');

    if (call.status === 'claimed') {
      actions.append(button('En route', '', () => post('enroute', { id: call.id })));
    }
    if (call.status === 'claimed' || call.status === 'enroute') {
      actions.append(button('On scene', '', () => post('onscene', { id: call.id })));
    }

    actions.append(button('Release', 'ghost', () => post('release', { id: call.id })));

    if (call.status === 'onscene') {
      const outcome = document.createElement('select');
      outcome.className = 'outcome';
      outcome.append(new Option('Resolved', 'resolved'), new Option('False alarm', 'false_alarm'), new Option('No response', 'no_response'));
      const close = button('Close call', 'danger', () => post('closeCall', { id: call.id, outcome: outcome.value }));
      actions.append(outcome, close);
    }

    box.append(actions);
  } else if (call.status === 'open') {
    const actions = el('div', 'actions');
    if (call.canClaim) actions.append(button('Respond', '', () => post('claim', { id: call.id })));
    else actions.append(el('div', 'unit', 'Waiting for an on-duty unit.'));
    box.append(actions);
  } else if (call.claimedBy && !call.mine) {
    box.append(el('div', 'unit', 'Unit: ' + call.claimedBy));
  }

  return box;
}

// ---------- ui events ----------
$('closeBtn').onclick = () => post('close');
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') { e.preventDefault(); post('close'); }
});

// Age counters tick locally between server updates; no need to re-render the whole list.
setInterval(() => {
  if (panel.classList.contains('hidden')) return;
  const elapsed = Math.floor((performance.now() - receivedAt) / 1000);
  document.querySelectorAll('[data-age]').forEach((node) => {
    node.textContent = ageText(Number(node.dataset.age) + elapsed);
  });
}, 1000);

// ---------- messages from the game ----------
window.addEventListener('message', ({ data }) => {
  if (!data || !data.type) return;

  if (data.type === 'open') {
    calls = [];
    receivedAt = performance.now();
    panel.classList.remove('hidden');
    $('calls').replaceChildren(el('div', 'empty', 'Requesting calls\u2026'));
    $('footer').textContent = '';
  }

  if (data.type === 'close') panel.classList.add('hidden');

  if (data.type === 'state') {
    role = data.role || '';
    pay = data.pay || 0;
    calls = Array.isArray(data.calls) ? data.calls : [];
    receivedAt = performance.now();
    render();
  }
});

// Plain-browser preview (outside the game): #demo shows a few sample calls.
if (typeof GetParentResourceName !== 'function' && location.hash === '#demo') {
  window.postMessage({ type: 'open' }, '*');
  window.postMessage({
    type: 'state', role: 'police', pay: 150,
    calls: [
      { id: 14, category: 'police', message: 'Armed robbery at the convenience store, suspect fled north.', area: 'Legion Square', status: 'open', caller: 'Mia', claimedBy: '', mine: false, canClaim: true, age: 42, dist: 310 },
      { id: 13, category: 'medical', message: 'Cyclist down, conscious but bleeding.', area: 'Vespucci Beach', status: 'onscene', caller: 'Sam', claimedBy: 'You', mine: true, canClaim: false, age: 190, dist: 18 },
    ],
  }, '*');
}
