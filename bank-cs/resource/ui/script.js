// NUI side: renders state sent by the game and posts user intent back.
// It holds no authority; the server validates every amount.
const $ = (id) => document.getElementById(id);
const resource = typeof GetParentResourceName === 'function' ? GetParentResourceName() : 'bank-cs';

const post = (name, data = {}) =>
  fetch(`https://${resource}/${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify(data),
  });

const money = (n) => '$' + Number(n).toLocaleString('en-US');

function readAmount() {
  const amount = parseInt($('amount').value, 10);
  if (!Number.isInteger(amount) || amount <= 0) {
    $('error').textContent = 'Enter a whole amount greater than 0.';
    return null;
  }
  $('error').textContent = '';
  return amount;
}

function close() {
  $('atm').classList.add('hidden');
  // Always tell the game, even if the request fails, so focus is released.
  post('close').catch(() => {});
}

window.addEventListener('message', ({ data }) => {
  if (data.type === 'open') {
    $('atm').classList.remove('hidden');
    $('amount').value = '';
    $('error').textContent = '';
    $('amount').focus();
  }
  if (data.type === 'balance') {
    $('cash').textContent = money(data.cash);
    $('bank').textContent = money(data.bank);
  }
  if (data.type === 'close') $('atm').classList.add('hidden');
});

$('deposit').onclick = () => { const a = readAmount(); if (a) post('deposit', { amount: a }); };
$('withdraw').onclick = () => { const a = readAmount(); if (a) post('withdraw', { amount: a }); };
$('transfer').onclick = () => {
  const a = readAmount();
  const target = parseInt($('target').value, 10);
  if (!a) return;
  if (!Number.isInteger(target) || target < 0) { $('error').textContent = 'Enter a valid player ID.'; return; }
  post('transfer', { amount: a, target });
};
$('close').onclick = close;
// Diagnostics: report every key the page receives (proves whether the page has focus).
const trace = (msg) => post('log', { msg }).catch(() => {});

document.addEventListener('keydown', (e) => {
  trace('keydown ' + e.key);
  if (e.key === 'Escape') { e.preventDefault(); close(); }
});
