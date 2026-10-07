// Usage: node ws_handshake.js <port> <host-header> <origin> [cookie]
// Prints the HTTP status line the server answers the WebSocket handshake with.
const net = require("net");
const [port, host, origin, cookie] = process.argv.slice(2);
const s = net.connect(Number(port), "127.0.0.1", () => s.write(
  `GET /live HTTP/1.1\r\nHost: ${host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
  `Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: ${origin}\r\n` +
  (cookie ? `Cookie: ${cookie}\r\n` : "") + "\r\n"));
let buf = "";
s.on("data", d => { buf += d.toString("latin1"); });
s.on("error", e => { console.log("ERROR " + e.message); process.exit(0); });
setTimeout(() => { console.log((buf.split("\r\n")[0] || "NO-REPLY").trim()); s.destroy(); }, 1200);
