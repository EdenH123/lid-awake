// The page the running app serves on 127.0.0.1:8787 is web/index.html (with the iPhone
// launch-screen links filled in), and every inline script is allowed by its CSP hashes.
const fs = require("fs");
const crypto = require("crypto");
const http = require("http");

const file = fs.readFileSync(process.argv[2], "utf8");
let failed = false;
const check = (name, ok, detail = "") => {
  console.log((ok ? "PASS " : "FAIL ") + name + (detail ? ` (${detail})` : ""));
  if (!ok) failed = true;
};

http.get({ host: "127.0.0.1", port: 8787, path: "/" }, res => {
  let body = "";
  res.setEncoding("utf8");
  res.on("data", d => (body += d));
  res.on("end", () => {
    check("status 200", res.statusCode === 200, String(res.statusCode));
    const splash = file.indexOf("<!--SPLASH-->");
    check("file has splash marker", splash > 0);
    check("served start matches file", body.startsWith(file.slice(0, splash)));
    check("served end matches file", body.endsWith(file.slice(splash + "<!--SPLASH-->".length)));
    check("splash marker filled in", !body.includes("<!--SPLASH-->"));
    const csp = res.headers["content-security-policy"] || "";
    const scripts = [...body.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(m => m[1]);
    check("has inline scripts", scripts.length >= 2, `${scripts.length}`);
    scripts.forEach((s, i) => {
      const h = "'sha256-" + crypto.createHash("sha256").update(s, "utf8").digest("base64") + "'";
      check(`script ${i + 1} allowed by CSP`, csp.includes(h));
    });
    process.exit(failed ? 1 : 0);
  });
}).on("error", e => { console.log("FAIL app not reachable on 127.0.0.1:8787: " + e.message); process.exit(1); });
