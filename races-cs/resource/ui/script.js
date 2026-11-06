// NUI side of the time trial. The client sends the state; this page only renders it.
const $ = (id) => document.getElementById(id);
const hud = $('hud');
const summary = $('summary');
const board = $('board');
let summaryTimer = null;

const clock = (ms) => {
  const value = Math.max(0, Number(ms) || 0);
  const minutes = Math.floor(value / 60000);
  const seconds = Math.floor(value / 1000) % 60;
  const millis = Math.floor(value % 1000);
  return `${minutes}:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`;
};
const metres = (m) => (m >= 1000 ? (m / 1000).toFixed(1) + ' km' : Math.round(m) + ' m');

function renderState(d) {
  if (!d.visible) {
    hud.classList.add('hidden');
    return;
  }

  hud.classList.remove('hidden');
  $('label').textContent = d.label || 'Time trial';
  $('timer').textContent = clock(d.elapsedMs);
  $('objective').textContent = d.objective || '\u2014';

  const total = Number(d.total) || 0;
  const index = Number(d.index) || 0;
  $('step').textContent = total ? `Checkpoint ${index}/${total}` : '';
  $('distance').textContent = metres(d.distance || 0);
  $('hint').textContent = index >= total && total > 0 ? 'Finish line ahead' : 'Reach the marked checkpoint';
  $('progress').style.width = total ? Math.min(100, Math.round((index - 1) / total * 100)) + '%' : '0%';
}

function reasonText(code) {
  if (code === 'expired') return 'You ran out of time.';
  if (code === 'stopped') return 'Race cancelled.';
  if (code === 'failed') return 'Race ended.';
  return 'Race complete.';
}

function renderEnd(d) {
  hud.classList.add('hidden');

  const finished = d.code === 'finished';
  const failed = d.code === 'failed' || d.code === 'expired';

  summary.classList.remove('hidden', 'good', 'bad');
  summary.classList.add(finished ? 'good' : failed ? 'bad' : 'good');
  $('summaryTitle').textContent = finished ? (d.pb ? 'NEW PERSONAL BEST' : 'RACE COMPLETE') : 'RACE OVER';
  $('summaryTime').textContent = finished ? (d.time || '\u2014') : '\u2014';

  let reason = reasonText(d.code);
  if (finished && d.best && !d.pb) reason = `Personal best ${d.best}.`;
  if (finished && d.pb && d.best) reason = `Previous best ${d.best}.`;
  $('summaryReason').textContent = reason;

  clearTimeout(summaryTimer);
  summaryTimer = setTimeout(() => summary.classList.add('hidden'), finished ? 7000 : 4500);
}

function renderBoard(d) {
  const list = $('boardList');
  const entries = Array.isArray(d.entries) ? d.entries : [];

  list.innerHTML = '';
  $('boardRoute').textContent = d.route || 'Leaderboard';
  $('boardEmpty').classList.toggle('hidden', entries.length > 0);

  entries.forEach((entry, i) => {
    const li = document.createElement('li');

    const rank = document.createElement('span');
    rank.className = 'rank';
    rank.textContent = `#${i + 1}`;

    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = entry.name || 'Unknown';

    const time = document.createElement('span');
    time.className = 'time';
    time.textContent = entry.time || clock(entry.ms);

    li.append(rank, name, time);
    list.appendChild(li);
  });

  board.classList.remove('hidden');
  clearTimeout(summaryTimer);
  summary.classList.add('hidden');

  clearTimeout(renderBoard._timer);
  renderBoard._timer = setTimeout(() => board.classList.add('hidden'), 20000);
}

function hideAll() {
  hud.classList.add('hidden');
  summary.classList.add('hidden');
  board.classList.add('hidden');
}

$('boardClose').addEventListener('click', () => board.classList.add('hidden'));

window.addEventListener('message', (event) => {
  const data = event.data || {};
  switch (data.type) {
    case 'state':
      renderState(data);
      break;
    case 'end':
      renderEnd(data);
      break;
    case 'board':
      renderBoard(data);
      break;
    case 'hide':
      hideAll();
      break;
  }
});
