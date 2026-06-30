// Tap a word (or highlight several) in a note's body/transcript -> a small
// "+ glossary" trigger appears by the selection -> clicking it opens a daisyUI
// modal (bottom sheet on mobile, centered dialog on desktop) to correct and add
// the term. Uses event delegation so it survives HTMX swaps of #note-content.
// No-ops on pages without a note + glossary token + modal.
(function () {
  function token() {
    var el = document.querySelector('#glossary-token input[name="__RequestVerificationToken"]');
    return el ? el.value : null;
  }
  function noteId() {
    var el = document.getElementById('glossary-token');
    return el ? el.getAttribute('data-note-id') : null;
  }
  function modal() { return document.getElementById('glossary-modal'); }

  // Selectable regions are marked with [data-glossary-source] (decoupled from styling).
  function inSelectable(node) {
    var el = node && (node.nodeType === 1 ? node : node.parentElement);
    return !!(el && el.closest && el.closest('[data-glossary-source]'));
  }

  var lastSelection = '';

  // --- transient trigger chip, anchored under the selection ---
  var chip = null;
  function ensureChip() {
    if (chip) return chip;
    chip = document.createElement('button');
    chip.type = 'button';
    chip.className = 'btn btn-xs btn-primary shadow-lg';
    chip.textContent = '+ glossary';
    chip.style.position = 'absolute';
    chip.style.zIndex = '60';
    chip.style.display = 'none';
    document.body.appendChild(chip);
    chip.addEventListener('mousedown', function (e) { e.preventDefault(); }); // keep the selection
    chip.addEventListener('click', function (e) { e.preventDefault(); openModal(); });
    return chip;
  }
  function showChip(rect) {
    ensureChip();
    chip.style.top = (window.scrollY + rect.bottom + 8) + 'px';
    chip.style.left = Math.max(8, Math.min(window.scrollX + rect.left, document.documentElement.clientWidth - 120)) + 'px';
    chip.style.display = 'inline-flex';
  }
  function hideChip() { if (chip) chip.style.display = 'none'; }

  function resetDone() {
    var m = modal();
    if (!m) return;
    var d = m.querySelector('#glossary-modal__done');
    if (d) { d.className = 'text-sm mt-3 empty:hidden'; d.textContent = ''; }
  }

  function openModal() {
    var m = modal();
    if (!m || !lastSelection || !token()) return;
    resetDone();
    var input = m.querySelector('#glossary-modal__input');
    input.value = lastSelection;
    hideChip();
    m.showModal();
    input.focus(); input.select();
  }

  async function reprocess(id) {
    var t = token(), m = modal();
    if (!t) { if (m) m.close(); return; }
    try {
      var res = await fetch('/Note/' + encodeURIComponent(id) + '?handler=Reprocess', {
        method: 'POST', headers: { 'RequestVerificationToken': t },
      });
      if (res.ok && window.htmx) window.htmx.ajax('GET', '/Note/' + id + '?handler=Content', '#note-content');
    } catch (e) { /* ignore */ }
    if (m) m.close();
  }

  async function add() {
    var t = token(), m = modal();
    if (!m) return;
    var done = m.querySelector('#glossary-modal__done');
    var term = m.querySelector('#glossary-modal__input').value.trim();
    if (!term || !t) { m.close(); return; }
    try {
      var res = await fetch('/Glossary?handler=Add', {
        method: 'POST',
        headers: { 'RequestVerificationToken': t, 'Content-Type': 'application/x-www-form-urlencoded' },
        body: 'term=' + encodeURIComponent(term) + '&variant=' + encodeURIComponent(lastSelection),
      });
      if (!res.ok) { done.className = 'text-sm mt-3 text-error'; done.textContent = 'Could not add the term.'; return; }
      window.getSelection().removeAllRanges();
      done.className = 'text-sm mt-3 text-success';
      done.textContent = '✓ "' + term + '" added.';
      if (window.toast) toast('Added "' + term + '" to glossary');
      var id = noteId();
      if (id) {
        var rb = document.createElement('button');
        rb.type = 'button';
        rb.className = 'btn btn-sm btn-primary mt-2';
        rb.textContent = 'Re-process this note';
        rb.addEventListener('click', function () { reprocess(id); });
        done.appendChild(document.createElement('br'));
        done.appendChild(rb);
      }
    } catch (e) { m.close(); }
  }

  // Modal Add button + Enter-to-add (delegated, so it works regardless of swaps).
  document.addEventListener('click', function (e) {
    if (e.target && e.target.id === 'glossary-modal__add') add();
  });
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && e.target && e.target.id === 'glossary-modal__input') { e.preventDefault(); add(); }
  });

  // Remember the current selection and anchor the chip under it, if it's a usable
  // selection inside a glossary source. Shared by the highlight and tap paths.
  function showForCurrentSelection() {
    var m = modal();
    if (m && m.open) return;
    var sel = window.getSelection();
    var text = sel && sel.toString().trim();
    if (!text || sel.rangeCount === 0) { hideChip(); return; }
    if (!inSelectable(sel.anchorNode)) { hideChip(); return; }
    if (!token()) return;
    lastSelection = text;
    showChip(sel.getRangeAt(0).getBoundingClientRect());
  }

  // (a) Highlight gesture — drag, double-click, or mobile long-press. We listen to
  // `selectionchange` (debounced) rather than `mouseup` so it also fires for touch
  // selection on mobile, where no mouse events fire. The debounce also keeps the chip
  // alive through the brief selection-collapse when the chip itself is tapped, so its
  // click still lands before we'd otherwise hide it.
  var selTimer = null;
  document.addEventListener('selectionchange', function () {
    if (selTimer) clearTimeout(selTimer);
    selTimer = setTimeout(showForCurrentSelection, 250);
  });

  // (b) Single tap / click on a word — selects just that word and shows the chip
  // immediately. A plain click/tap creates no selection on its own, so without this
  // the feature would require an explicit highlight gesture (the reason it appeared
  // broken). Works for both desktop clicks and mobile taps (which synthesize a click).
  function caretNodeOffset(x, y) {
    if (document.caretPositionFromPoint) {
      var p = document.caretPositionFromPoint(x, y);
      return p && { node: p.offsetNode, offset: p.offset };
    }
    if (document.caretRangeFromPoint) {
      var r = document.caretRangeFromPoint(x, y);
      return r && { node: r.startContainer, offset: r.startOffset };
    }
    return null;
  }
  function isWordChar(ch) { return !!ch && /[\p{L}\p{N}_'’-]/u.test(ch); }
  function wordRangeAt(x, y) {
    var c = caretNodeOffset(x, y);
    if (!c || !c.node || c.node.nodeType !== 3) return null; // text nodes only
    var text = c.node.nodeValue || '';
    var start = c.offset, end = c.offset;
    while (start > 0 && isWordChar(text[start - 1])) start--;
    while (end < text.length && isWordChar(text[end])) end++;
    if (start === end) return null; // tapped whitespace / punctuation, not a word
    var range = document.createRange();
    range.setStart(c.node, start);
    range.setEnd(c.node, end);
    return range;
  }
  document.addEventListener('click', function (e) {
    var m = modal();
    if (m && m.open) return;
    if (!inSelectable(e.target)) return;                 // clicks outside note text
    if (!e.clientX && !e.clientY) return;                // keyboard-synthesized click
    var sel = window.getSelection();
    // Respect an existing multi-word highlight — don't collapse it to one word.
    if (sel && sel.rangeCount && !sel.isCollapsed && sel.toString().trim()) return;
    if (!token()) return;
    var range = wordRangeAt(e.clientX, e.clientY);
    if (!range) return;
    sel.removeAllRanges();
    sel.addRange(range);
    lastSelection = sel.toString().trim();
    if (lastSelection) showChip(range.getBoundingClientRect());
  });

  // Desktop: hide instantly when pressing elsewhere. On touch this is covered by the
  // debounced selectionchange above (tapping away collapses the selection).
  document.addEventListener('mousedown', function (e) {
    if (chip && chip.style.display !== 'none' && e.target !== chip) hideChip();
  });
  document.addEventListener('scroll', hideChip, true);
})();
