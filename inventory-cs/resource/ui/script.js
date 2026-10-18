// NUI side: renders the state the server sends and posts the player's *intent* back.
// It never edits inventory data itself; after every action the server replies with the new truth.
const $ = (id) => document.getElementById(id);
const resource = typeof GetParentResourceName === 'function' ? GetParentResourceName() : 'inventory-cs';

const post = (name, data = {}) =>
  fetch(`https://${resource}/${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify(data),
  }).catch(() => {});

let state = { slots: 30, maxWeight: 1, weight: 0, items: [] };
let selected = null; // slot number
let dragFrom = null;

const bySlot = (n) => state.items.find((i) => i.slot === n);
const kg = (g) => (g / 1000).toFixed(1);

function render() {
  const grid = $('grid');
  grid.innerHTML = '';

  for (let n = 1; n <= state.slots; n++) {
    const item = bySlot(n);
    const el = document.createElement('div');
    el.className = 'slot' + (item ? ' filled' : '') + (n === selected ? ' selected' : '');
    el.dataset.slot = n;
    el.innerHTML = `<span class="num">${n}</span>` +
      (item ? `<span class="name">${item.label}</span><span class="count">${item.count}</span>` : '');

    el.addEventListener('click', () => select(n));
    if (item) {
      el.draggable = true;
      el.addEventListener('dragstart', (e) => { dragFrom = n; e.dataTransfer.setData('text/plain', String(n)); });
    }
    el.addEventListener('dragover', (e) => { e.preventDefault(); el.classList.add('over'); });
    el.addEventListener('dragleave', () => el.classList.remove('over'));
    el.addEventListener('drop', (e) => {
      e.preventDefault();
      el.classList.remove('over');
      if (dragFrom === null || dragFrom === n) return;
      const src = bySlot(dragFrom);
      if (!src) return;
      // Holding Shift moves just the quantity box amount (a split); otherwise the whole stack.
      const wanted = parseInt($('qty').value, 10);
      const count = e.shiftKey && wanted > 0 && wanted < src.count ? wanted : src.count;
      post('move', { from: dragFrom, to: n, count });
      dragFrom = null;
    });
    grid.appendChild(el);
  }

  const pct = Math.min(100, Math.round((state.weight / state.maxWeight) * 100));
  $('weightText').textContent = `${kg(state.weight)} / ${kg(state.maxWeight)} kg`;
  $('weightFill').style.width = pct + '%';
  $('weightFill').classList.toggle('heavy', pct >= 80);

  renderDetails();
}

function select(n) {
  selected = bySlot(n) ? n : null;
  render();
}

function renderDetails() {
  const item = selected ? bySlot(selected) : null;
  document.querySelector('#details .empty').classList.toggle('hidden', !!item);
  $('info').classList.toggle('hidden', !item);
  if (!item) { selected = null; return; }

  $('itemName').textContent = item.label;
  $('itemMeta').textContent = `${item.count} / ${item.max} in stack · ${item.weight} g each`;
  $('use').disabled = !item.usable;
  const q = $('qty');
  q.max = item.count;
  if (!(parseInt(q.value, 10) >= 1) || parseInt(q.value, 10) > item.count) q.value = 1;
}

function qty() {
  const item = bySlot(selected);
  const n = parseInt($('qty').value, 10);
  return item && n >= 1 ? Math.min(n, item.count) : 1;
}

$('use').onclick = () => selected && post('use', { slot: selected });
$('give').onclick = () => selected && post('give', { slot: selected, count: qty() });
$('discard').onclick = () => selected && post('discard', { slot: selected, count: qty() });

function close() {
  $('inv').classList.add('hidden');
  post('close');
}
$('close').onclick = close;

document.addEventListener('keydown', (e) => {
  const typing = e.target && e.target.tagName === 'INPUT';
  if (e.key === 'Escape' || (!typing && e.key.toLowerCase() === 'i')) { e.preventDefault(); close(); }
});

window.addEventListener('message', ({ data }) => {
  if (data.type === 'open') { $('inv').classList.remove('hidden'); render(); }
  if (data.type === 'close') { $('inv').classList.add('hidden'); }
  if (data.type === 'state') { state = data; render(); }
});
