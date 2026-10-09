// Service worker for the phone page: turns the Mac's pushes into notifications, opens the
// page when one is tapped, and keeps a copy of the page itself so the home-screen app still
// opens (and says "Lid Awake is off") while the Mac is off or out of reach, instead of a blank
// screen. Only the page at "/" is kept; nothing the page fetches is ever cached.
// Served at /sw.js.
let serviceWorkerJS = #"""
const PAGE_CACHE = "lidawake-page-v1";
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("fetch", e => {
  const req = e.request;
  const url = new URL(req.url);
  if (req.mode !== "navigate" || url.origin !== self.location.origin || url.pathname !== "/") return;
  e.respondWith((async () => {
    const cache = await caches.open(PAGE_CACHE);
    // The Mac first (always the newest page); the kept copy if it doesn't answer within 3 s.
    const live = fetch(req).then(res => {
      if (res.ok && res.type === "basic") cache.put("/", res.clone());
      return res;
    });
    const late = new Promise(resolve => setTimeout(resolve, 3000));
    try {
      const first = await Promise.race([live, late]);
      if (first) return first;
    } catch (err) {}
    const kept = await cache.match("/");
    if (kept) return kept;
    try { return await live; } catch (err) { return Response.error(); }
  })());
});
self.addEventListener("activate", e => e.waitUntil(self.clients.claim()));
self.addEventListener("push", e => {
  let d = {};
  try { d = e.data ? e.data.json() : {}; } catch (err) { d = { body: e.data && e.data.text() }; }
  e.waitUntil(self.registration.showNotification(d.title || "Lid Awake", {
    body: d.body || "", tag: d.tag || "lidawake", icon: "/apple-touch-icon.png", badge: "/apple-touch-icon.png",
  }));
});
self.addEventListener("notificationclick", e => {
  e.notification.close();
  e.waitUntil(self.clients.matchAll({ type: "window", includeUncontrolled: true }).then(list =>
    list.length ? list[0].focus() : self.clients.openWindow("/")));
});
"""#
