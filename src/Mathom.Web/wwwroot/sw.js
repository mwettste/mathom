const CACHE = 'mathom-shell-v6';
const SHELL = [
  '/Capture',
  '/css/mathom.css',
  '/js/offline.js',
  '/js/capture.js',
  '/lib/htmx.min.js',
  '/lib/idiomorph-ext.min.js',
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

// Only the static app shell is cached. Everything else (the page, the HTMX
// poll/search endpoints like /?handler=Timeline, /Note/{id}?handler=Content)
// must always hit the network so live data is never served stale.
function isStaticAsset(pathname) {
  return pathname.startsWith('/css/')
    || pathname.startsWith('/js/')
    || pathname.startsWith('/lib/')
    || pathname.startsWith('/icon-')
    || pathname === '/apple-touch-icon-180.png'
    || pathname === '/manifest.webmanifest';
}

// Stale-while-revalidate: serve the cached asset instantly (offline-capable),
// and refresh it in the background so a new deploy's assets are picked up.
function staleWhileRevalidate(req) {
  return caches.open(CACHE).then((cache) =>
    cache.match(req, { ignoreSearch: true }).then((cached) => {
      const network = fetch(req)
        .then((res) => {
          if (res.ok) cache.put(req, res.clone());
          return res;
        })
        .catch(() => cached);
      return cached || network;
    })
  );
}

self.addEventListener('fetch', (e) => {
  const req = e.request;
  if (req.method !== 'GET') return; // never intercept captures (POST)
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;

  // Navigations: network-first, fall back to the cached capture shell offline.
  if (req.mode === 'navigate') {
    e.respondWith(
      fetch(req).catch(() =>
        caches.match('/Capture').then(
          (r) => r || new Response('Offline', { status: 503, headers: { 'Content-Type': 'text/plain' } })
        )
      )
    );
    return;
  }

  // Static assets only — dynamic GETs fall through to the network untouched.
  if (isStaticAsset(url.pathname)) {
    e.respondWith(staleWhileRevalidate(req));
  }
});
