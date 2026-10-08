// Service worker for the phone page: turns the Mac's pushes into notifications, and opens
// the page when one is tapped. Served at /sw.js.
let serviceWorkerJS = #"""
self.addEventListener("install", () => self.skipWaiting());
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
