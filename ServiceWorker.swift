// Service worker for the phone page: turns the Mac's pushes into notifications, opens the
// page when one is tapped, and keeps a copy of the page itself so the home-screen app still
// opens (and says "Lid Awake is off") while the Mac is off or out of reach, instead of a blank
// screen. Only the page at "/" is kept; nothing the page fetches is ever cached.
// Served at /sw.js; the file is web/sw.js.
// nil when the file is missing: /sw.js then answers 404 rather than an empty script that would replace a working one.
let serviceWorkerJS: String? = webFile("sw.js").flatMap { String(data: $0, encoding: .utf8) }
