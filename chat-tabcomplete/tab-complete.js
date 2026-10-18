// Tab completion for the default FiveM chat.
// Type "/gi" and press Tab -> "/givecar ". Press Tab again to cycle through other matches.
// Reads the suggestion list the chat already renders, so it works for every registered command.
(function () {
  var cycle = null; // { matches: [...], index: n, last: "text we put in the box" }

  document.addEventListener('keydown', function (e) {
    if (e.key !== 'Tab') return;

    var box = document.activeElement;
    if (!box || box.tagName !== 'TEXTAREA') return;
    e.preventDefault(); // keep focus in the chat box

    var text = box.value;
    var cycling = cycle && text === cycle.last;
    // Only complete the command name: must start with "/" and not yet contain arguments.
    if (text.charAt(0) !== '/' || (text.indexOf(' ') !== -1 && !cycling)) return;

    if (cycle && text === cycle.last) {
      cycle.index = (cycle.index + 1) % cycle.matches.length;
    } else {
      var names = Array.prototype.map.call(
        document.querySelectorAll('.suggestions .suggestion > p > span:first-child'),
        function (el) { return el.textContent.trim(); }
      );
      var matches = names.filter(function (n) { return n.indexOf(text) === 0; });
      if (matches.length === 0) { cycle = null; return; }
      cycle = { matches: matches, index: 0, last: '' };
    }

    var completed = cycle.matches[cycle.index] + ' ';
    cycle.last = completed;
    box.value = completed;
    box.dispatchEvent(new Event('input', { bubbles: true })); // let Vue pick up the new value
  }, true);
})();
