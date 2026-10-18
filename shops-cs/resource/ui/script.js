// NUI side: renders what the server sends and posts the player's intent back.
// Prices and stock shown here are informational only: the server re-checks every purchase.
const $ = (id) => document.getElementById(id);
const resource = typeof GetParentResourceName === 'function' ? GetParentResourceName() : 'shops-cs';

const post = (name, data = {}) =>
  fetch(`https://${resource}/${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify(data),
  }).catch(() => {});

const money = (n) => '$' + Number(n).toLocaleString('en-US');
const account = () => document.querySelector('input[name="account"]:checked').value;

function render(state) {
  $('shopName').textContent = state.label;
  $('cash').textContent = money(state.cash);
  $('bank').textContent = money(state.bank);

  const rows = $('rows');
  rows.innerHTML = '';
  for (const item of state.items) {
    const tr = document.createElement('tr');
    const stockClass = item.stock === 0 ? 'out' : item.stock <= 3 ? 'low' : '';
    tr.innerHTML =
      `<td>${item.label}</td>` +
      `<td class="price">${money(item.price)}</td>` +
      `<td class="stock ${stockClass}">${item.stock}</td>` +
      `<td><input class="qty" type="number" min="1" max="20" value="1" ${item.stock === 0 ? 'disabled' : ''}></td>` +
      `<td><button class="buy" ${item.stock === 0 ? 'disabled' : ''}>Buy</button></td>`;

    tr.querySelector('.buy').onclick = () => {
      const quantity = parseInt(tr.querySelector('.qty').value, 10);
      if (!Number.isInteger(quantity) || quantity < 1) return;
      post('buy', { item: item.item, quantity, account: account() });
    };
    rows.appendChild(tr);
  }
}

function close() {
  $('shop').classList.add('hidden');
  post('close');
}
$('close').onclick = close;
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.preventDefault(); close(); } });

window.addEventListener('message', ({ data }) => {
  if (data.type === 'open') $('shop').classList.remove('hidden');
  if (data.type === 'close') $('shop').classList.add('hidden');
  if (data.type === 'state') render(data);
});
