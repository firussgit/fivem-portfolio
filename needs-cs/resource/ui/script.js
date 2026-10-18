// Display only: shows the values the server sends. It can't change a need.
const $ = (id) => document.getElementById(id);

function show(name, value) {
  $(name + 'Fill').style.width = value + '%';
  $(name + 'Value').textContent = value;
  $(name + 'Box').classList.toggle('low', value < 20);
}

window.addEventListener('message', ({ data }) => {
  if (data.type !== 'needs') return;
  $('hud').classList.remove('hidden');
  show('hunger', data.hunger);
  show('thirst', data.thirst);
});
