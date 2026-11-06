// NUI side of the dealership. Everything here is display: the server owns the catalogue,
// prices, balances, ownership and the trade-in offer, and re-checks every request.
const $ = (id) => document.getElementById(id);
const resource = typeof GetParentResourceName === 'function' ? GetParentResourceName() : 'dealer-cs';

const post = (name, data = {}) =>
  fetch(`https://${resource}/${name}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json; charset=UTF-8' },
    body: JSON.stringify(data),
  }).catch(() => {});

const money = (n) => '$' + Number(n).toLocaleString('en-US');

let state = null;
let confirmId = 0;
let confirmTimer = 0;

// ---------- rendering ----------

function setState(next) {
  state = next;
  confirmId = 0;
  clearTimeout(confirmTimer);

  $('showroom').textContent = next.showroom || 'Premium Deluxe Motorsport';
  $('cash').textContent = money(next.cash);
  $('bank').textContent = money(next.bank);
  $('capacity').textContent = `${next.ownedCount}/${next.maxVehicles}`;

  const full = Number(next.ownedCount) >= Number(next.maxVehicles);
  $('capacityChip').classList.toggle('full', full);
  $('fullNote').classList.toggle('hidden', !full);

  renderCatalog();
  renderRent();
  renderOwned();
}

function renderCatalog() {
  const root = $('catalog');
  root.innerHTML = '';
  if (!state || !Array.isArray(state.catalog)) return;

  const full = Number(state.ownedCount) >= Number(state.maxVehicles);
  const cash = Number(state.cash) || 0;
  const bank = Number(state.bank) || 0;

  for (const car of state.catalog) {
    const card = document.createElement('article');
    card.className = 'card';

    const top = document.createElement('div');
    top.className = 'card-top';

    const name = document.createElement('h2');
    name.textContent = car.label;

    const price = document.createElement('span');
    price.className = 'price';
    price.textContent = money(car.price);

    top.append(name, price);

    const model = document.createElement('p');
    model.className = 'tag';
    model.textContent = car.model;

    const actions = document.createElement('div');
    actions.className = 'actions';

    const test = document.createElement('button');
    test.className = 'ghost';
    test.textContent = 'Test drive';
    test.onclick = () => post('testdrive', { model: car.model });

    const buyCash = document.createElement('button');
    buyCash.className = 'buy';
    buyCash.textContent = 'Cash';
    buyCash.disabled = full || cash < Number(car.price);
    buyCash.title = full ? 'Your garage is full' : cash < Number(car.price) ? 'Not enough cash' : '';
    buyCash.onclick = () => post('buy', { model: car.model, account: 'cash' });

    const buyBank = document.createElement('button');
    buyBank.className = 'buy';
    buyBank.textContent = 'Bank';
    buyBank.disabled = full || bank < Number(car.price);
    buyBank.title = full ? 'Your garage is full' : bank < Number(car.price) ? 'Not enough bank' : '';
    buyBank.onclick = () => post('buy', { model: car.model, account: 'bank' });

    actions.append(test, buyCash, buyBank);
    card.append(top, model, actions);
    root.appendChild(card);
  }
}

function renderRent() {
  const root = $('rentals');
  root.innerHTML = '';
  if (!state || !Array.isArray(state.rent)) return;

  const cash = Number(state.cash) || 0;
  const bank = Number(state.bank) || 0;
  const active = state.rental;
  const minutes = Number(state.rentMinutes) || 0;

  $('rentMinutes').textContent = minutes;
  $('rentalActive').classList.toggle('hidden', !active);

  if (active) {
    $('rentalBannerModel').textContent = active.label || active.model || 'Vehicle';
    $('rentalBannerMeta').textContent = `${active.plate} · ${money(active.deposit)} deposit held`;
  }

  for (const car of state.rent) {
    const card = document.createElement('article');
    card.className = 'card';

    const top = document.createElement('div');
    top.className = 'card-top';

    const name = document.createElement('h2');
    name.textContent = car.label;

    const fee = document.createElement('span');
    fee.className = 'price';
    fee.textContent = money(car.fee);

    top.append(name, fee);

    const terms = document.createElement('p');
    terms.className = 'tag';
    terms.textContent = `${minutes} min · deposit ${money(car.deposit)} refundable`;

    const total = Number(car.fee) + Number(car.deposit);
    const busy = Boolean(active);

    const actions = document.createElement('div');
    actions.className = 'actions';

    const rentCash = document.createElement('button');
    rentCash.className = 'buy';
    rentCash.textContent = 'Cash';
    rentCash.disabled = busy || cash < total;
    rentCash.title = busy
      ? 'Return your current rental first'
      : cash < total ? `Needs ${money(total)} in cash` : '';
    rentCash.onclick = () => post('rent', { model: car.model, account: 'cash' });

    const rentBank = document.createElement('button');
    rentBank.className = 'buy';
    rentBank.textContent = 'Bank';
    rentBank.disabled = busy || bank < total;
    rentBank.title = busy
      ? 'Return your current rental first'
      : bank < total ? `Needs ${money(total)} in the bank` : '';
    rentBank.onclick = () => post('rent', { model: car.model, account: 'bank' });

    actions.append(rentCash, rentBank);
    card.append(top, terms, actions);
    root.appendChild(card);
  }
}

function renderOwned() {
  const root = $('owned');
  root.innerHTML = '';

  const owned = state && Array.isArray(state.owned) ? state.owned : [];
  $('ownedEmpty').classList.toggle('hidden', owned.length > 0);

  for (const car of owned) {
    const card = document.createElement('article');
    card.className = 'card';

    const top = document.createElement('div');
    top.className = 'card-top';

    const name = document.createElement('h2');
    name.textContent = car.label;

    const value = document.createElement('span');
    value.className = 'price';
    value.textContent = money(car.value);

    top.append(name, value);

    const plate = document.createElement('p');
    plate.className = 'tag';
    plate.textContent = `${car.plate} · condition ${car.condition}%`;

    const condition = document.createElement('div');
    condition.className = 'condition';
    const bar = document.createElement('span');
    bar.style.width = Math.max(0, Math.min(100, car.condition)) + '%';
    bar.className = car.condition < 40 ? 'low' : car.condition < 75 ? 'mid' : 'good';
    condition.appendChild(bar);

    const actions = document.createElement('div');
    actions.className = 'actions';

    const sell = document.createElement('button');
    sell.className = confirmId === car.id ? 'buy confirm' : 'buy';
    sell.textContent = confirmId === car.id ? `Confirm · ${money(car.value)}` : 'Sell';
    sell.onclick = () => {
      if (confirmId !== car.id) {
        confirmId = car.id;
        clearTimeout(confirmTimer);
        confirmTimer = setTimeout(() => {
          confirmId = 0;
          renderOwned();
        }, 4000);
        renderOwned();
        return;
      }

      confirmId = 0;
      clearTimeout(confirmTimer);
      post('sell', { id: car.id });
    };

    actions.appendChild(sell);
    card.append(top, plate, condition, actions);
    root.appendChild(card);
  }
}

function toast(text, ok) {
  if (!text) return;

  const el = document.createElement('div');
  el.className = 'toast ' + (ok ? 'ok' : 'bad');
  el.textContent = text;
  $('toasts').appendChild(el);

  setTimeout(() => {
    el.classList.add('out');
    setTimeout(() => el.remove(), 250);
  }, 4000);
}

function showTestDrive(data) {
  $('testdrive').classList.remove('hidden');
  $('tdModel').textContent = data.label || data.model || 'Vehicle';

  const seconds = Math.max(0, Number(data.remaining) || 0);
  $('tdTime').textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  $('tdTime').classList.toggle('low', seconds <= 10);
}

function showRental(data) {
  $('rental').classList.remove('hidden');
  $('rentalModel').textContent = data.label || data.model || 'Vehicle';

  const seconds = Math.max(0, Number(data.remaining) || 0);
  $('rentalTime').textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  $('rentalTime').classList.toggle('low', seconds <= 60);
}

function close() {
  $('dealer').classList.add('hidden');
  post('close');
}

// ---------- static wiring ----------

document.querySelectorAll('.tab').forEach((tab) => {
  tab.onclick = () => {
    document.querySelectorAll('.tab').forEach((t) => t.classList.toggle('active', t === tab));
    const name = tab.dataset.tab;
    $('showroomTab').classList.toggle('hidden', name !== 'showroom');
    $('rentTab').classList.toggle('hidden', name !== 'rent');
    $('sellTab').classList.toggle('hidden', name !== 'sell');
  };
});

$('close').onclick = close;
$('returnCar').onclick = () => post('returncar');
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') {
    event.preventDefault();
    close();
  }
});

window.addEventListener('message', ({ data }) => {
  if (data.type === 'open') $('dealer').classList.remove('hidden');
  if (data.type === 'close') $('dealer').classList.add('hidden');
  if (data.type === 'state') setState(data);
  if (data.type === 'toast') toast(data.text, data.ok);
  if (data.type === 'testdrive') showTestDrive(data);
  if (data.type === 'testdriveEnd') $('testdrive').classList.add('hidden');
  if (data.type === 'rental') showRental(data);
  if (data.type === 'rentalEnd') $('rental').classList.add('hidden');
});
