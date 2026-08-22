/* Service worker: makes the editor installable and offline-capable.
   - App shell + the editor/ Python & JSON files are network-first (fresh when online,
     cached fallback when offline).
   - The large, version-pinned Pyodide CDN assets are cache-first (download once).
   Bump CACHE to force clients to drop old caches after a deploy. */
const CACHE = "ec-save-editor-v2";
const SHELL = [
  "./", "./index.html", "./style.css", "./app.js", "./manifest.webmanifest",
  "./icons/icon-192.png", "./icons/icon-512.png", "./icons/icon-maskable-512.png",
];

self.addEventListener("install", (e) => {
  e.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

const SHARE_CACHE = "ec-share";   // holds a file shared into the PWA; never purged here

self.addEventListener("activate", (e) => {
  e.waitUntil(caches.keys()
    .then((ks) => Promise.all(
      ks.filter((k) => k !== CACHE && k !== SHARE_CACHE).map((k) => caches.delete(k))))
    .then(() => self.clients.claim()));
});

self.addEventListener("fetch", (e) => {
  const req = e.request;
  const url = new URL(req.url);

  // Web Share Target: a file shared into the installed PWA arrives as a POST.
  // Stash it, then redirect to ?shared=1 so the app can pick it up on load.
  if (req.method === "POST" && url.pathname.endsWith("/share-target")) {
    e.respondWith((async () => {
      try {
        const form = await req.formData();
        const file = form.get("save") || form.get("file");
        if (file) {
          const cache = await caches.open(SHARE_CACHE);
          await cache.put("shared-file", new Response(file,
            { headers: { "x-filename": (file.name || "UserData.dat") } }));
        }
      } catch (err) { /* fall through to redirect regardless */ }
      return Response.redirect("./?shared=1", 303);
    })());
    return;
  }

  if (req.method !== "GET") return;
  const sameOrigin = new URL(req.url).origin === self.location.origin;
  e.respondWith((async () => {
    const cache = await caches.open(CACHE);
    if (sameOrigin) {                              // network-first (shell + editor/ files)
      try {
        const res = await fetch(req);
        if (res && res.status === 200) cache.put(req, res.clone());
        return res;
      } catch (err) {
        const hit = (await cache.match(req)) ||
          (req.mode === "navigate" ? await cache.match("./index.html") : null);
        if (hit) return hit;
        throw err;
      }
    }
    const hit = await cache.match(req);            // cross-origin (Pyodide CDN): cache-first
    if (hit) return hit;
    const res = await fetch(req);
    if (res && res.status === 200 && (res.type === "basic" || res.type === "cors")) {
      cache.put(req, res.clone());
    }
    return res;
  })());
});
