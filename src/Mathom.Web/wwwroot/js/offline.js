// IndexedDB outbox: captures that can't be sent now are stored and replayed
// when connectivity returns. The record id doubles as the server idempotency
// key, so replays never double-create.
window.mathomOutbox = (function () {
  const DB_NAME = 'mathom';
  const STORE = 'outbox';

  function openDb() {
    return new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, 1);
      req.onupgradeneeded = () => req.result.createObjectStore(STORE, { keyPath: 'id' });
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }

  async function put(rec) {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(rec);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }

  async function remove(id) {
    const db = await openDb();
    return new Promise((resolve) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).delete(id);
      tx.oncomplete = () => resolve();
    });
  }

  async function getAll() {
    const db = await openDb();
    return new Promise((resolve) => {
      const tx = db.transaction(STORE, 'readonly');
      const rq = tx.objectStore(STORE).getAll();
      rq.onsuccess = () => resolve(rq.result || []);
      rq.onerror = () => resolve([]);
    });
  }

  function post(rec) {
    if (rec.kind === 'voice') {
      const form = new FormData();
      form.append('audio', rec.blob, rec.filename || 'note.webm');
      form.append('idempotencyKey', rec.id);
      return fetch('/capture/voice', { method: 'POST', body: form });
    }
    return fetch('/capture', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: rec.text, idempotencyKey: rec.id }),
    });
  }

  let draining = false;
  async function drain() {
    if (draining || !navigator.onLine) return;
    draining = true;
    try {
      for (const rec of await getAll()) {
        try {
          const res = await post(rec);
          // ok = stored; 4xx = idempotent dup or permanent reject -> drop either way.
          if (res.ok || (res.status >= 400 && res.status < 500)) await remove(rec.id);
          else return; // 5xx -> keep, try again later
        } catch {
          return; // network error -> still offline, stop
        }
      }
    } finally {
      draining = false;
    }
  }

  async function send(rec) {
    if (navigator.onLine) {
      try {
        const res = await post(rec);
        if (res.ok) return { ok: true, queued: false, status: 'Filed ✓' };
      } catch { /* fall through to queue */ }
    }
    await put(rec);
    drain(); // best-effort, fire-and-forget
    return { ok: false, queued: true, status: "Saved offline — will sync when you're back." };
  }

  window.addEventListener('online', drain);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') drain();
  });
  document.addEventListener('DOMContentLoaded', drain);

  return { send, drain, getAll };
})();
