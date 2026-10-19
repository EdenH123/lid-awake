// No visible "Mac" left hard-coded in the page: it must come from word("computer") (JS) or a
// <span data-w="computer"> (HTML), so a Windows PC sees "PC". Whole-line comments, CSS, the device table,
// localize() are allowed; a trailing comment after code is not skipped (put it on its own line). "MacBook"
// is flagged too: the drawing code only uses lowercase class names, never the word.
const fs = require("fs");
let html = fs.readFileSync(process.argv[2], "utf8");
const blank = m => m.replace(/[^\n]/g, "");  // blank a stretch out but keep its line breaks, so line numbers stay true
html = html
  .replace(/<!--[\s\S]*?-->/g, blank)
  .replace(/\/\*[\s\S]*?\*\//g, blank)
  .replace(/^[ \t]*\/\/.*$/gm, "")
  .replace(/<style>[\s\S]*?<\/style>/g, blank)
  .replace(/const COMPUTER_WORDS = \{[\s\S]*?\n  \};/, blank)
  .replace(/const localize = text => \{[\s\S]*?\n  \};/, blank)  // swaps the Mac's "Mac" for the PC's own word
  .replace(/<span data-w="computer">[^<]*<\/span>/g, "");
const bad = html.split("\n").map((l, i) => [i + 1, l]).filter(([, l]) => /\bMac\b|MacBook/.test(l));
bad.forEach(([n, l]) => console.log(`FAIL line ${n}: ${l.trim().slice(0, 140)}`));
console.log(bad.length ? `FAIL ${bad.length} hard-coded computer words` : "PASS no hard-coded computer words");
process.exit(bad.length ? 1 : 0);
