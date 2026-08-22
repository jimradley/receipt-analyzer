// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations
//
// build-stamp: 2026-08-08T00:00Z-api-fetch-bypass — this comment MUST change on every deploy that touches
// client assets. The browser detects a service-worker update only via a byte-for-byte compare of
// THIS file's own text (never the imported service-worker-assets.js manifest, and never anything
// served through this worker's own fetch handler, which serves index.html cache-first and so can
// never observe a fresh deploy on its own). Without a bytes-level change here, every future
// redeploy is a silent no-op for anyone who already has the app installed. See docs/Learnings.md.

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => {
    // Don't intercept API calls at all (not even to pass them through via respondWith(fetch(...))).
    // Re-forwarding an intercepted Request that carries a body (e.g. a multipart receipt-photo
    // upload) via event.respondWith(fetch(event.request)) is unreliable in standalone/installed PWA
    // mode and can throw a bare "TypeError: Failed to fetch" before any HTTP response exists, even
    // though the exact same request works fine outside the service worker. Not calling
    // respondWith() lets the browser handle the request natively, sidestepping that class of bug.
    if (new URL(event.request.url).pathname.startsWith('/api/')) return;
    event.respondWith(onFetch(event));
});

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

async function onInstall(event) {
    console.info('Service worker: Install');

    // Fetch and cache all matching items from the assets manifest
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));

    // Activate this updated worker immediately instead of waiting for all tabs to close,
    // so a redeploy takes effect on the next load rather than getting stuck on the old shell.
    await self.skipWaiting();
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    // Take control of open clients right away (pairs with skipWaiting above).
    await self.clients.claim();
}

async function onFetch(event) {
    // /api/* never reaches here — the fetch listener above returns early for those so the
    // browser handles them natively, bypassing this cache logic entirely.
    let cachedResponse = null;
    if (event.request.method === 'GET') {
        // For all navigation requests, try to serve index.html from cache,
        // unless that request is for an offline resource.
        // If you need some URLs to be server-rendered, edit the following check to exclude those URLs
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);

        const request = shouldServeIndexHtml ? 'index.html' : event.request;
        const cache = await caches.open(cacheName);
        cachedResponse = await cache.match(request);
    }

    return cachedResponse || fetch(event.request);
}
