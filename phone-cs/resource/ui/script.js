// NUI side of the phone. It holds no authority: it shows what the server sent and posts requests back.
const $ = (id) => document.getElementById(id);
const resource = typeof GetParentResourceName === 'function' ? GetParentResourceName() : 'phone-cs';

const post = (name, data = {}) =>
  fetch(`https://${resource}/${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify(data),
  }).catch(() => {});

const money = (n) => '$' + Number(n).toLocaleString('en-US');

let me = '';
let contacts = [];
let messages = [];
let openThread = null; // the other party's number while a thread is on screen

const nameOf = (number) => (contacts.find((c) => c.number === number) || {}).name || number;

// ---------- tabs ----------
function showTab(name) {
  for (const t of document.querySelectorAll('.tab')) t.classList.add('hidden');
  openThread = null;
  $('tab-' + name).classList.remove('hidden');
  for (const b of document.querySelectorAll('nav button')) b.classList.toggle('active', b.dataset.tab === name);
}
for (const b of document.querySelectorAll('nav button')) b.onclick = () => { showTab(b.dataset.tab); render(); };

// ---------- rendering (textContent only: message text is other players' input) ----------
function el(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

function threads() {
  const map = new Map();
  for (const m of messages) {
    const other = m.from === me ? m.to : m.from;
    const t = map.get(other) || { number: other, last: m, unread: 0 };
    t.last = m;
    if (m.to === me && !m.seen) t.unread++;
    map.set(other, t);
  }
  return [...map.values()].sort((a, b) => b.last.id - a.last.id);
}

function render() {
  $('myNumber').textContent = me;

  const list = $('threads');
  list.replaceChildren();
  const all = threads();
  if (!all.length) list.append(el('div', 'empty', 'No messages yet'));
  for (const t of all) {
    const row = el('div', 'row');
    const left = el('div');
    left.append(el('div', 'name', nameOf(t.number)), el('div', 'sub', (t.last.from === me ? 'You: ' : '') + t.last.body));
    row.append(left);
    if (t.unread) row.append(el('span', 'dot', t.unread));
    row.onclick = () => openConversation(t.number);
    list.append(row);
  }
  const unread = all.reduce((n, t) => n + t.unread, 0);
  $('badge').textContent = unread;
  $('badge').classList.toggle('hidden', unread === 0);

  const cl = $('contacts');
  cl.replaceChildren();
  if (!contacts.length) cl.append(el('div', 'empty', 'No contacts'));
  for (const c of contacts) {
    const row = el('div', 'row');
    const left = el('div');
    left.append(el('div', 'name', c.name), el('div', 'sub', c.number));
    const del = el('button', 'del', '×');
    del.onclick = (e) => { e.stopPropagation(); post('deleteContact', { id: c.id }); };
    row.append(left, del);
    row.onclick = () => openConversation(c.number);
    cl.append(row);
  }

  if (openThread) renderThread();
}

function renderThread() {
  $('threadTitle').textContent = nameOf(openThread);
  const box = $('bubbles');
  box.replaceChildren();
  for (const m of messages.filter((m) => m.from === openThread || m.to === openThread))
    box.append(el('div', 'bubble' + (m.from === me ? ' mine' : ''), m.body));
  box.scrollTop = box.scrollHeight;
}

function openConversation(number) {
  showTab('thread');
  openThread = number;
  for (const b of document.querySelectorAll('nav button')) b.classList.toggle('active', b.dataset.tab === 'messages');
  renderThread();
  // Mark what they sent as read (locally now, on the server in the background).
  let changed = false;
  for (const m of messages) if (m.from === number && m.to === me && !m.seen) { m.seen = true; changed = true; }
  if (changed) { post('markRead', { from: number }); render(); }
  $('body').focus();
}

// ---------- forms ----------
$('back').onclick = () => { showTab('messages'); render(); };
$('composer').onsubmit = (e) => {
  e.preventDefault();
  const body = $('body').value.trim();
  if (!body || !openThread) return;
  post('send', { to: openThread, body });
  $('body').value = '';
};
$('newMessage').onsubmit = (e) => {
  e.preventDefault();
  const n = $('newTo').value.trim();
  if (/^\d{3}-\d{4}$/.test(n)) { $('newTo').value = ''; openConversation(n); }
};
$('addContact').onsubmit = (e) => {
  e.preventDefault();
  post('addContact', { name: $('cName').value, number: $('cNumber').value.trim() });
  $('cName').value = $('cNumber').value = '';
};
$('payForm').onsubmit = (e) => {
  e.preventDefault();
  const amount = parseInt($('payAmount').value, 10);
  if (!amount) return;
  post('pay', { to: $('payTo').value.trim(), amount });
  $('payAmount').value = '';
};

// ---------- emergency ----------
function emergency(category) {
  const body = $('emgBody').value.trim();
  if (body.length < 4) {
    $('emgState').textContent = 'Describe the emergency first.';
    return;
  }
  $('emgState').textContent = 'Calling dispatch\u2026';
  post('emergency', { category, body });
}
$('emgPolice').onclick = () => emergency('police');
$('emgMedical').onclick = () => emergency('medical');
$('emgCancel').onclick = () => { $('emgState').textContent = 'Cancelling\u2026'; post('emergencyCancel'); };

function close() {
  $('phone').classList.add('hidden');
  post('close');
}
$('close').onclick = close;
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.preventDefault(); close(); } });

// ---------- messages from the game ----------
window.addEventListener('message', ({ data }) => {
  if (data.type === 'open') { showTab('messages'); $('phone').classList.remove('hidden'); }
  if (data.type === 'close') $('phone').classList.add('hidden');

  if (data.type === 'state') {
    me = data.number;
    contacts = data.contacts;
    messages = data.messages;
    $('bankBalance').textContent = money(data.bank);
    render();
  }
  if (data.type === 'message') {
    messages.push(data.msg);
    // A message arriving in the thread you're looking at is read immediately.
    if (openThread && data.msg.from === openThread && data.msg.to === me) {
      data.msg.seen = true;
      post('markRead', { from: openThread });
    }
    render();
  }
  if (data.type === 'balance') $('bankBalance').textContent = money(data.bank);
  if (data.type === 'emergency') {
    if (data.state === 'cancelled') {
      $('emgState').textContent = data.ok ? 'Call cancelled.' : 'You have no active call.';
    } else {
      $('emgState').textContent = data.ok
        ? 'Call placed. Dispatch has your location.'
        : 'The call could not be placed.';
      if (data.ok) $('emgBody').value = '';
    }
  }
});

// Plain-browser preview (outside the game): show the phone with sample data.
if (typeof GetParentResourceName !== 'function' && location.hash === '#demo') {
  window.postMessage({ type: 'open' }, '*');
  window.postMessage({ type: 'state', number: '555-0142', bank: 12500,
    contacts: [{ id: 1, name: 'Mia', number: '555-0100' }],
    messages: [
      { id: 1, from: '555-0100', to: '555-0142', body: 'Are you coming tonight?', at: 0, seen: false },
      { id: 2, from: '555-0142', to: '555-0100', body: 'Yes, see you at the garage', at: 0, seen: true },
    ] }, '*');
}
