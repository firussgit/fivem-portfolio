// NUI side: sliders edit a *proposal*; the game previews it locally and the server prices it.
// The price shown here is whatever the server last answered. Nothing is bought without the server.
const $ = (id) => document.getElementById(id);
const resource = typeof GetParentResourceName === 'function' ? GetParentResourceName() : 'customs-cs';

const post = (name, data = {}) =>
  fetch(`https://${resource}/${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify(data),
  }).catch(() => {});

const CORE = ['p', 's', 't', 'e', 'b', 'x', 'u'];
// Body parts; the slider only appears if the vehicle model has variants of that part.
const PARTS = [['sp', 'Spoiler'], ['fb', 'Front bumper'], ['rb', 'Rear bumper'], ['sk', 'Side skirts'],
               ['ex', 'Exhaust'], ['gr', 'Grille'], ['hd', 'Hood'], ['rf', 'Roof']];
const KEYS = CORE.concat(PARTS.map(([k]) => k));

for (const [k, name] of PARTS) {
  $('parts').insertAdjacentHTML('beforeend',
    `<div id="row-${k}" class="hidden"><label>${name} <output id="${k}Val"></output></label>` +
    `<input id="${k}" type="range" min="0" max="0" step="1" value="0"></div>`);
}
const LABELS = { t: ['Stock', 'Pure black', 'Dark smoke', 'Light smoke', 'Stock tint', 'Limo', 'Green'],
                 e: ['Stock', 'Level 1', 'Level 2', 'Level 3', 'Level 4'],
                 b: ['Stock', 'Street', 'Sport', 'Race'],
                 x: ['Stock', 'Street', 'Sport', 'Race'] };
const money = (n) => '$' + Number(n).toLocaleString('en-US');

function read() {
  return KEYS.map((k) => `${k}=${k === 'u' ? ($('u').checked ? 1 : 0) : parseInt($(k).value, 10)}`).join(';');
}

function write(mods) {
  for (const part of mods.split(';')) {
    const [k, v] = part.split('=');
    if (!KEYS.includes(k)) continue;
    if (k === 'u') $('u').checked = v === '1'; else $(k).value = Math.min(Number(v), Number($(k).max));
  }
  labels();
}

function labels() {
  for (const k of ['p', 's']) $(k + 'Val').textContent = $(k).value;
  for (const k of ['t', 'e', 'b', 'x']) $(k + 'Val').textContent = LABELS[k][$(k).value] ?? $(k).value;
  for (const [k] of PARTS) $(k + 'Val').textContent = $(k).value === '0' ? 'Stock' : 'Style ' + $(k).value;
}

let timer = null;
function changed() {
  labels();
  clearTimeout(timer);
  timer = setTimeout(() => post('preview', { mods: read() }), 150); // preview + ask the server for the price
}

for (const k of KEYS) $(k).addEventListener('input', changed);

$('buy').onclick = () => post('buy', { mods: read() });

function close() {
  $('panel').classList.add('hidden');
  post('close');
}
$('close').onclick = close;
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.preventDefault(); close(); } });

window.addEventListener('message', ({ data }) => {
  if (data.type === 'open') {
    $('plate').textContent = data.plate;
    const caps = data.caps || {};
    let any = false;
    for (const [k] of PARTS) {
      const n = caps[k] || 0;
      $(k).max = n;
      $(k).value = 0;
      $('row-' + k).classList.toggle('hidden', n === 0);
      any = any || n > 0;
    }
    $('bodySection').classList.toggle('hidden', !any);
    write(data.mods || 'p=0;s=0;t=0;e=0;b=0;x=0;u=0');
    $('cost').textContent = money(0);
    $('discount').classList.add('hidden');
    $('panel').classList.remove('hidden');
  }
  if (data.type === 'quote') {
    $('cost').textContent = money(data.cost);
    $('discount').classList.toggle('hidden', !data.discount);
  }
  if (data.type === 'applied') {
    $('cost').textContent = money(0); // what's on the vehicle now has been paid for
    $('discount').classList.add('hidden');
  }
  if (data.type === 'close') $('panel').classList.add('hidden');
});
