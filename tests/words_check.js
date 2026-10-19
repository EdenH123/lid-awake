// No visible "iPhone" or "Face ID" left hard-coded in the page: they must come from word()
// (JS) or a <span data-w="..."> (HTML), so an Android phone sees its own words.
// Comments and the PHONE_WORDS table itself are allowed.
const fs = require("fs");
let html = fs.readFileSync(process.argv[2], "utf8");
html = html
  .replace(/<!--[\s\S]*?-->/g, "")
  .replace(/\/\*[\s\S]*?\*\//g, "")
  .replace(/^\s*\/\/.*$/gm, "")
  .replace(/\s\/\/ .*$/gm, "")
  .replace(/const PHONE = [\s\S]*?: "other";/, "")  // the browser check that tells an iPhone apart
  .replace(/const localize = [\s\S]*?word\("unlock"\)\);/, "")  // swaps the Mac's "Face ID" for the phone's own word
  .replace(/const kind = \/iPhone[^\n]*\n/, "")  // tells a listed iPhone/iPad session from Android and the rest
  .replace(/const ALL_PHONE_WORDS = \{[\s\S]*?\n  \};/, "")
  .replace(/<span data-w="[a-zA-Z]+">[^<]*<\/span>/g, "").replace(/data-w-placeholder="[^"]*"/g, "");
const bad = html.split("\n").map((l, i) => [i + 1, l]).filter(([, l]) => /iPhone|Face ID/.test(l));
bad.forEach(([n, l]) => console.log(`FAIL line ~${n}: ${l.trim().slice(0, 140)}`));
console.log(bad.length ? `FAIL ${bad.length} hard-coded phone words` : "PASS no hard-coded phone words");
process.exit(bad.length ? 1 : 0);
