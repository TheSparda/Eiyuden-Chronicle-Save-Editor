/* Headless end-to-end against a real local server (so ../editor resolves). Loads the
   real Pyodide engine, injects a synthetic save, and asserts decode/render, a searchable
   picker, the review modal, and — the key mobile check — no horizontal overflow at
   320/360px. Self-skips (exit 0) if playwright-core/Chromium isn't installed, or if the
   Pyodide CDN can't be reached, so minimal CI never breaks.
   Run: node tests/e2e.mjs   (from web/) */
import { createServer } from "node:http";
import { readFileSync, existsSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join, extname } from "node:path";

const WEB = dirname(dirname(fileURLToPath(import.meta.url)));
const ROOT = dirname(WEB);                     // repo root — serves web/ and editor/
const SAMPLE_B64 =
  "gMO9GjbM9OkXeRi3wBCb0WML4GXyfLq11sqnwqB6zuyCnCw0qHt3vJvNCJXxWwmYkkR3YSTL6Cb+" +
  "60bX49QaKylWe8/BNha2SEQUgfozQ2Q5dd2s2XBwoXPzB5U1wnw1waYEWnTZrbOjTiEaI+36dLXyf9" +
  "kEJ0Byc2DH5lmJtRFeSOkcoeBz4v77A56VCaRGWjj5FS/m71DRIm+4Mhhgcyp7MM+4XGDGmTtMq1CH" +
  "Ceuuh4EouDFavS/TPxVEKXPx58ljRerPvWYLoNQX/kDUtZGlvCaMIRmdEzcoAcZmM3igX+3nzmF8fL" +
  "F3Uo6vrshA6tmniAI+LGWASgxDDjELhKuWArW+G3+rK7CfbKkxQcnuuCOCkyBIO0Az+hN9QeL2RCTn" +
  "Na1fQ32m+b/2Z7VzxPprkXTMjeYiWrD9JXC23RZrSNxKHJghZItuPlH5s8gQAmXMjT56uDH34tc8BR" +
  "+KcQk+U/XItqViTJfsB/XKEOOFkWPCxjRzRBxEZoCwbhcD9Y4uB146WwZxWXyKAIrIPuTMqshDzc7D" +
  "+CF13iYefrAPBDldOEiOboNsm37uBI/28xggF2WAwhOQ7cycT7pZ6JxLCq07VPYPf/L1N56MrWm7RA" +
  "BGD3zWk+UmcjwJ8u8NL0Obkaa2j5HnSZeNwVovov48vT+iAHUOIimzJXCfqH39fG+QSxEYdMr5BJBX" +
  "vz1b9LDTtQfX7LKUM5FDrQQXgC37QfCeBGRofi0kxqM6CSP8v0CfIp3Tc0ZNj/y3wCJcACEec/GFwg" +
  "2FWxutCRkBEFMqNprOlK4x/44qKX5lhb6Ed+zVVYch6lbtopytp+Ry57eh2Ea2Ou7se+7mdWeKbQ==";

let chromium;
try { ({ chromium } = await import("playwright-core")); }
catch { console.log("e2e: playwright-core not installed — skipping (this is fine)."); process.exit(0); }

const MIME = { ".html": "text/html", ".js": "text/javascript", ".css": "text/css",
  ".json": "application/json", ".webmanifest": "application/manifest+json",
  ".png": "image/png", ".py": "text/plain", ".dat": "application/octet-stream" };
const server = createServer((req, res) => {
  const p = join(ROOT, decodeURIComponent(req.url.split("?")[0]));
  if (!p.startsWith(ROOT) || !existsSync(p) || !statSync(p).isFile()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { "Content-Type": MIME[extname(p)] || "application/octet-stream" });
  res.end(readFileSync(p));
});
await new Promise(r => server.listen(0, r));
const port = server.address().port;

let browser, fails = 0;
const ok = (c, m) => { console.log((c ? "  ok  " : " FAIL ") + m); if (!c) fails++; };
try {
  try { browser = await chromium.launch(); }
  catch { console.log("e2e: no Chromium available — skipping (this is fine)."); server.close(); process.exit(0); }
  const page = await browser.newPage();
  page.setDefaultTimeout(120000);
  await page.goto(`http://localhost:${port}/web/`, { waitUntil: "domcontentloaded" });

  try { await page.waitForFunction(() => window.PY, null, { timeout: 120000 }); }
  catch { console.log("e2e: engine did not boot (offline / CDN blocked) — skipping."); await browser.close(); server.close(); process.exit(0); }
  ok(true, "engine booted");

  await page.evaluate(async (b64) => {
    const bin = atob(b64), bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    await handleFile(new File([bytes], "UserDataSample.dat", { type: "application/octet-stream" }));
  }, SAMPLE_B64);
  await page.waitForSelector("#f_money", { timeout: 60000 });
  ok((await page.inputValue("#f_money")) === "1000", "decoded money renders (1000)");

  for (const w of [320, 360]) {
    await page.setViewportSize({ width: w, height: 720 });
    const overflow = await page.evaluate(() =>
      document.documentElement.scrollWidth > document.documentElement.clientWidth);
    ok(!overflow, `no horizontal overflow at ${w}px`);
  }
  await page.setViewportSize({ width: 900, height: 720 });

  await page.evaluate(() => {
    document.querySelector('.tab[data-t="chars"]').click();
    const el = document.querySelector("#sp-chars input.pick[data-eq]");
    if (el) el.click();
  });
  ok((await page.$(".modal-backdrop .pickersearch")) !== null, "searchable picker opens");
  await page.keyboard.press("Escape");

  await page.evaluate(() => {
    const f = document.getElementById("f_money");
    f.value = "999999"; f.dispatchEvent(new Event("input")); f.dispatchEvent(new Event("change"));
    write();
  });
  ok((await page.$(".modal-backdrop .reviewlist")) !== null, "review-changes modal appears");
  const rl = await page.textContent(".reviewlist");
  ok(rl != null && rl.includes("999999"), "review shows the new value");
} finally {
  if (browser) await browser.close();
  server.close();
}
console.log(fails ? `\n${fails} e2e check(s) FAILED` : "\ne2e: all checks passed");
process.exit(fails ? 1 : 0);
