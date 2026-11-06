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

function whole(input) {
  const value = parseInt(input.value, 10);
  return Number.isInteger(value) && value > 0 ? value : null;
}

function fail(message) {
  $('error').textContent = message;
  return null;
}

function readAmount() {
  const amount = whole($('amount'));
  if (!amount) return fail('Enter a whole amount greater than 0.');
  $('error').textContent = '';
  return amount;
}

function duration(seconds) {
  if (seconds <= 0) return 'due now';
  if (seconds >= 86400) return `${Math.floor(seconds / 86400)}d ${Math.floor((seconds % 86400) / 3600)}h`;
  if (seconds >= 3600) return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
  return `${Math.max(1, Math.floor(seconds / 60))}m`;
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
    $('loanAmount').value = '';
    $('loanPayAmount').value = '';
    $('error').textContent = '';
    $('amount').focus();
  }
  if (data.type === 'balance') {
    $('cash').textContent = money(data.cash);
    $('bank').textContent = money(data.bank);
  }
  if (data.type === 'loans') {
    $('credit').textContent = `Score ${data.score} \u00b7 limit ${money(data.limit)}`;

    if (!data.status) {
      $('loanInfo').textContent = 'No active loan.';
      $('loanApply').disabled = false;
    } else {
      const due = data.status === 'defaulted' ? 'DEFAULTED - repay now' : `next payment in ${duration(data.dueSeconds)}`;
      $('loanInfo').textContent = `${money(data.balance)} left of ${money(data.principal)} \u00b7 ` +
        `${money(data.installment)}/payment \u00b7 ${data.rate}% \u00b7 ${data.missed} missed \u00b7 ${due}`;
      // One loan at a time; the server enforces this too.
      $('loanApply').disabled = true;
    }
  }
  if (data.type === 'close') $('atm').classList.add('hidden');
});

$('deposit').onclick = () => { const a = readAmount(); if (a) post('deposit', { amount: a }); };
$('withdraw').onclick = () => { const a = readAmount(); if (a) post('withdraw', { amount: a }); };
$('transfer').onclick = () => {
  const a = readAmount();
  const target = parseInt($('target').value, 10);
  if (!a) return;
  if (!Number.isInteger(target) || target < 0) { fail('Enter a valid player ID.'); return; }
  post('transfer', { amount: a, target });
};

$('loanApply').onclick = () => {
  const amount = whole($('loanAmount'));
  const days = parseInt($('loanDays').value, 10);
  if (!amount) return fail('Enter a loan amount greater than 0.');
  if (!Number.isInteger(days) || days <= 0) return fail('Choose a loan term.');
  $('error').textContent = '';
  post('loanApply', { amount, days });
};

$('loanPay').onclick = () => {
  const amount = whole($('loanPayAmount'));
  if (!amount) return fail('Enter a repayment greater than 0.');
  $('error').textContent = '';
  post('loanPay', { amount });
};

$('close').onclick = close;
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') { e.preventDefault(); close(); }
});
