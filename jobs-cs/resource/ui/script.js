// NUI side of the job HUD. The client sends the state; this page only renders it.
const $ = (id) => document.getElementById(id);
const hud = $('hud');
const summary = $('summary');
let summaryTimer = null;

const money = (n) => '$' + Number(n || 0).toLocaleString('en-US');
const clock = (s) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
const metres = (m) => m >= 1000 ? (m / 1000).toFixed(1) + ' km' : Math.round(m) + ' m';

function renderState(d) {
  if (!d.visible) {
    hud.classList.add('hidden');
    return;
  }

  hud.classList.remove('hidden');
  $('label').textContent = d.label || 'Work';
  $('objective').textContent = d.objective || '\u2014';
  $('status').textContent = d.status || '';
  $('payout').textContent = money(d.payout);
  $('step').textContent = d.total ? `Stop ${d.index}/${d.total}` : '';
  $('distance').textContent = metres(d.distance || 0);
  $('timer').textContent = clock(d.timeLeft || 0);
  $('timer').classList.toggle('low', (d.timeLeft || 0) <= 60);

  const done = Math.max(0, (d.index || 1) - 1);
  $('progress').style.width = d.total ? Math.min(100, Math.round(done / d.total * 100)) + '%' : '0%';
}

function reasonText(reason) {
  if (reason === 'expired') return 'You ran out of time.';
  if (reason === 'cancelled') return 'You stopped the job.';
  if (reason === 'abandoned') return 'The job was abandoned.';
  return 'The job is over.';
}

function showSummary(reason, payout) {
  hud.classList.add('hidden');
  summary.classList.remove('hidden');

  const completed = reason === 'completed';
  $('summaryTitle').textContent = completed ? 'JOB COMPLETE' : 'JOB ENDED';
  $('summaryReason').textContent = completed ? 'Payment received in cash.' : reasonText(reason);
  $('summaryPayout').textContent = completed ? money(payout) : '';
  $('summaryPayout').classList.toggle('hidden', !completed);

  clearTimeout(summaryTimer);
  summaryTimer = setTimeout(() => summary.classList.add('hidden'), 4500);
}

window.addEventListener('message', ({ data }) => {
  if (!data || !data.type) return;
  if (data.type === 'state') renderState(data);
  if (data.type === 'end') showSummary(data.reason, data.payout);
  if (data.type === 'hide') {
    hud.classList.add('hidden');
    summary.classList.add('hidden');
  }
});
