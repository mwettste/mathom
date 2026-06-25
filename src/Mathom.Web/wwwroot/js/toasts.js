// Lightweight toast notifications using daisyUI `toast` + `alert`.
// Usage: window.toast('Saved'), window.toast('Failed', 'error').
// kind: 'success' (default) | 'info' | 'warning' | 'error'.
// Note: full class literals (not string concatenation) so Tailwind detects them.
(function () {
  var CLS = {
    success: 'alert-success',
    info: 'alert-info',
    warning: 'alert-warning',
    error: 'alert-error',
  };

  function container() {
    var c = document.getElementById('toasts');
    if (!c) {
      c = document.createElement('div');
      c.id = 'toasts';
      c.className = 'toast toast-end z-[100]';
      document.body.appendChild(c);
    }
    return c;
  }

  window.toast = function (message, kind) {
    var el = document.createElement('div');
    el.className = 'alert ' + (CLS[kind] || CLS.success) + ' shadow-lg transition-opacity duration-500';
    el.setAttribute('role', 'status');
    el.textContent = message;
    container().appendChild(el);
    setTimeout(function () {
      el.classList.add('opacity-0');
      setTimeout(function () { el.remove(); }, 500);
    }, 2600);
  };
})();
