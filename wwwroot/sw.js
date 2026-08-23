// ──────────────────────────────────────────────────────
// Facturix Web — Service Worker (PWA Offline Support)
// ──────────────────────────────────────────────────────

const CACHE_NAME = 'facturix-v1';
const PATH_BASE = '/facturix';

// Core shell assets to pre-cache on install
const PRECACHE_URLS = [
    PATH_BASE + '/',
    PATH_BASE + '/css/site.css',
    PATH_BASE + '/js/site.js',
    PATH_BASE + '/lib/bootstrap/dist/css/bootstrap.min.css',
    PATH_BASE + '/lib/bootstrap/dist/js/bootstrap.bundle.min.js',
    PATH_BASE + '/lib/jquery/dist/jquery.min.js',
    PATH_BASE + '/img/facturix-logo.png',
    PATH_BASE + '/img/icons/icon-512x512.png',
    PATH_BASE + '/favicon.ico',
    PATH_BASE + '/manifest.json'
];

// Install: pre-cache essential assets
self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_NAME)
            .then(cache => cache.addAll(PRECACHE_URLS))
            .then(() => self.skipWaiting())
            .catch(err => {
                console.warn('[SW] Pre-cache failed for some assets:', err);
                return self.skipWaiting();
            })
    );
});

// Activate: clean up old caches
self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(cacheNames => {
            return Promise.all(
                cacheNames
                    .filter(name => name !== CACHE_NAME)
                    .map(name => caches.delete(name))
            );
        }).then(() => self.clients.claim())
    );
});

// Fetch: Network-first strategy for HTML/API, Cache-first for static assets
self.addEventListener('fetch', event => {
    const { request } = event;
    const url = new URL(request.url);

    // Skip non-GET requests (POST forms, etc.)
    if (request.method !== 'GET') return;

    // Skip external requests (CDN fonts, analytics, etc.)
    if (url.origin !== self.location.origin) return;

    // For navigation requests (HTML pages): Network-first with cache fallback
    if (request.mode === 'navigate') {
        event.respondWith(
            fetch(request)
                .then(response => {
                    // Cache the latest version
                    const clone = response.clone();
                    caches.open(CACHE_NAME).then(cache => cache.put(request, clone));
                    return response;
                })
                .catch(() => {
                    // Offline: serve from cache
                    return caches.match(request).then(cached => {
                        return cached || caches.match(PATH_BASE + '/');
                    });
                })
        );
        return;
    }

    // For static assets (JS, CSS, images): Cache-first with network fallback
    if (url.pathname.match(/\.(js|css|png|jpg|jpeg|gif|svg|ico|woff2?|ttf|eot)(\?|$)/)) {
        event.respondWith(
            caches.match(request).then(cached => {
                if (cached) {
                    // Return cached, but also update cache in background (stale-while-revalidate)
                    fetch(request).then(response => {
                        if (response.ok) {
                            caches.open(CACHE_NAME).then(cache => cache.put(request, response));
                        }
                    }).catch(() => {});
                    return cached;
                }
                // Not in cache: fetch and cache
                return fetch(request).then(response => {
                    if (response.ok) {
                        const clone = response.clone();
                        caches.open(CACHE_NAME).then(cache => cache.put(request, clone));
                    }
                    return response;
                });
            })
        );
        return;
    }

    // For API/data requests: Network-first
    event.respondWith(
        fetch(request)
            .then(response => {
                const clone = response.clone();
                caches.open(CACHE_NAME).then(cache => cache.put(request, clone));
                return response;
            })
            .catch(() => caches.match(request))
    );
});
