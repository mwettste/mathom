// Select word(s) in a note's body/transcript -> popup to correct and add the
// term to the glossary. Uses event delegation so it survives HTMX swaps of
// #note-content. No-ops on pages without a note + glossary token.
(function () {
  function token() {
    var el = document.querySelector('#glossary-token input[name="__RequestVerificationToken"]');
    return el ? el.value : null;
  }

  // The selectable regions.
  function inSelectable(node) {
    while (node && node !== document.body) {
      if (node.classList && (node.classList.contains('note__body') || node.classList.contains('note__transcript'))) return true;
      node = node.parentNode;
    }
    return false;
  }

  var pop = null;
  function ensurePop() {
    if (pop) return pop;
    pop = document.createElement('div');
    pop.id = 'glossary-pop';
    pop.innerHTML =
      '<div class="glossary-pop__lbl">Add to glossary</div>' +
      '<input class="field" id="glossary-pop__input" autocomplete="off" />' +
      '<div class="glossary-pop__row">' +
        '<button type="button" class="btn btn--ghost" id="glossary-pop__cancel">Cancel</button>' +
        '<button type="button" class="btn btn--primary" id="glossary-pop__add">Add</button>' +
      '</div>' +
      '<div class="glossary-pop__done" id="glossary-pop__done"></div>';
    document.body.appendChild(pop);
    pop.querySelector('#glossary-pop__cancel').addEventListener('click', hide);
    pop.querySelector('#glossary-pop__add').addEventListener('click', add);
    return pop;
  }

  function hide() { if (pop) pop.style.display = 'none'; }

  function showFor(text, rect) {
    ensurePop();
    pop.querySelector('#glossary-pop__done').textContent = '';
    var input = pop.querySelector('#glossary-pop__input');
    input.value = text;
    pop.style.display = 'block';
    pop.style.top = (window.scrollY + rect.bottom + 8) + 'px';
    var left = window.scrollX + rect.left;
    pop.style.left = Math.max(8, Math.min(left, document.documentElement.clientWidth - 260)) + 'px';
    input.focus(); input.select();
  }

  async function add() {
    var t = token();
    var term = pop.querySelector('#glossary-pop__input').value.trim();
    if (!term || !t) { hide(); return; }
    try {
      var res = await fetch('/Glossary?handler=Add', {
        method: 'POST',
        headers: { 'RequestVerificationToken': t, 'Content-Type': 'application/x-www-form-urlencoded' },
        body: 'term=' + encodeURIComponent(term),
      });
      var done = pop.querySelector('#glossary-pop__done');
      done.textContent = res.ok
        ? '✓ "' + term + '" added — future captures will be corrected.'
        : 'Could not add the term.';
      window.getSelection().removeAllRanges();
      setTimeout(hide, 1600);
    } catch (e) { hide(); }
  }

  document.addEventListener('mouseup', function () {
    if (pop && pop.style.display === 'block') return;
    var sel = window.getSelection();
    var text = sel && sel.toString().trim();
    if (!text || sel.rangeCount === 0) return;
    if (!inSelectable(sel.anchorNode)) return;
    if (!token()) return;
    showFor(text, sel.getRangeAt(0).getBoundingClientRect());
  });

  document.addEventListener('mousedown', function (e) {
    if (pop && pop.style.display === 'block' && !pop.contains(e.target)) hide();
  });
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') hide(); });
})();
