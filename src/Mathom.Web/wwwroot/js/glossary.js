// Select word(s) in a note's body/transcript -> a small "+ glossary" trigger
// appears by the selection -> clicking it opens a daisyUI modal (bottom sheet on
// mobile, centered dialog on desktop) to correct and add the term. Uses event
// delegation so it survives HTMX swaps of #note-content. No-ops on pages without
// a note + glossary token + modal.
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

  // Show the trigger when text is selected inside a glossary source region.
  document.addEventListener('mouseup', function () {
    var m = modal();
    if (m && m.open) return;
    var sel = window.getSelection();
    var text = sel && sel.toString().trim();
    if (!text || sel.rangeCount === 0) { hideChip(); return; }
    if (!inSelectable(sel.anchorNode)) { hideChip(); return; }
    if (!token()) return;
    lastSelection = text;
    showChip(sel.getRangeAt(0).getBoundingClientRect());
  });
  document.addEventListener('mousedown', function (e) {
    if (chip && chip.style.display !== 'none' && e.target !== chip) hideChip();
  });
  document.addEventListener('scroll', hideChip, true);
})();
