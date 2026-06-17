const CACHE = 'mathom-shell-v1';
const SHELL = [
  '/Capture',
  '/css/mathom.css',
  '/js/offline.js',
  '/js/capture.js',
  '/lib/htmx.min.js',
  '/lib/alpine.min.js',
  '/manifest.webmanifest',
  '/icon-192.png',
  '/icon-512.png',
];

self.addEventListener('install', (e) => {
  e.waitUntil(
    caches.open(CACHE)
      // cache.add per item so one missing asset doesn't fail the whole install
      .then((c) => Promise.all(SHELL.map((u) => c.add(u).catch(() => {}))))
      .then(() => self.skipWaiting())
  );
});

self.addEventListener('activate', (e) => {
  e.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return; // never intercept captures (POST)
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;

  // Navigations: try network, fall back to the cached capture shell when offline.
  if (req.mode === 'navigate') {
    e.respondWith(fetch(req).catch(() => caches.match('/Capture')));
    return;
  }

  // Static assets: cache-first (ignoreSearch so ?v=hash query strings still match).
  e.respondWith(
    caches.match(req, { ignoreSearch: true }).then((cached) => {
      if (cached) return cached;
      return fetch(req).then((res) => {
        if (res.ok) {
          const copy = res.clone();
          caches.open(CACHE).then((c) => c.put(req, copy));
        }
        return res;
      });
    })
  );
});
