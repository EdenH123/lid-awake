// Independent RFC 8291 receiver: what the iPhone does with the Mac's alert.
const crypto = require("crypto");
const v = JSON.parse(require("fs").readFileSync(0, "utf8"));
const b = k => Buffer.from(v[k], "base64");
const body = b("body"), auth = b("auth"), uaPublic = b("phonePublic");
const salt = body.subarray(0, 16), rs = body.readUInt32BE(16), idlen = body[20];
const asPublic = body.subarray(21, 21 + idlen), record = body.subarray(21 + idlen);
const ecdh = crypto.createECDH("prime256v1"); ecdh.setPrivateKey(b("phonePrivate"));
const secret = ecdh.computeSecret(asPublic);
const hkdf = (salt, ikm, info, len) => Buffer.from(crypto.hkdfSync("sha256", ikm, salt, info, len));
const ikm = hkdf(auth, secret, Buffer.concat([Buffer.from("WebPush: info\0"), uaPublic, asPublic]), 32);
const cek = hkdf(salt, ikm, Buffer.from("Content-Encoding: aes128gcm\0"), 16);
const nonce = hkdf(salt, ikm, Buffer.from("Content-Encoding: nonce\0"), 12);
const d = crypto.createDecipheriv("aes-128-gcm", cek, nonce);
d.setAuthTag(record.subarray(record.length - 16));
const plain = Buffer.concat([d.update(record.subarray(0, record.length - 16)), d.final()]);
const text = plain.subarray(0, plain.length - 1).toString(), pad = plain[plain.length - 1];
console.log(JSON.stringify({ recordSize: rs, keyLength: idlen, lastRecordMarker: pad === 2, matches: text === v.expected, text }));
