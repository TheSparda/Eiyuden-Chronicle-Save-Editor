/* Fast static checks — no browser, no Python. Verifies every client JS file parses,
   the app shell is wired, and the playbook features are present in source.
   Run: node tests/validate.mjs   (from web/) */
import { execSync } from "node:child_process";
import { readFileSync, existsSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const WEB = dirname(dirname(fileURLToPath(import.meta.url)));
let fails = 0;
const ok = (c, m) => { console.log((c ? "  ok  " : " FAIL ") + m); if (!c) fails++; };
const read = f => readFileSync(join(WEB, f), "utf8");

// 1) every client JS parses
for (const f of ["app.js", "sw.js"]) {
  try { execSync(`node --check "${join(WEB, f)}"`); ok(true, `${f} parses`); }
  catch (e) { ok(false, `${f} parses`); }
}

// 2) app shell wiring
const html = read("index.html");
ok(/pyodide\/v\d+\.\d+\.\d+\/full\/pyodide\.js/.test(html), "index.html pins a Pyodide version");
ok(html.includes('src="app.js"'), "index.html loads app.js");
ok(html.includes('rel="manifest"'), "index.html links the manifest");
ok(html.includes('id="lastOpened"'), "index.html has the last-opened chip host");
ok(/viewport-fit=cover/.test(html), "index.html viewport is notch-aware");

// 3) manifest: icons + share_target
const man = JSON.parse(read("manifest.webmanifest"));
ok(Array.isArray(man.icons) && man.icons.some(i => i.purpose === "maskable"), "manifest has a maskable icon");
ok(man.share_target && man.share_target.method === "POST", "manifest declares a POST share_target");

// 4) service worker: precache shell + share handler + preserves share cache
const sw = read("sw.js");
for (const f of ["./index.html", "./app.js", "./style.css", "./manifest.webmanifest"])
  ok(sw.includes(f), `sw precaches ${f}`);
ok(sw.includes("share-target"), "sw handles the share-target POST");
ok(sw.includes("SHARE_CACHE") && sw.includes("k !== SHARE_CACHE"), "sw never purges the share cache");

// 5) CSS: mobile-first markers
const css = read("style.css");
ok(/@media\s*\(max-width:760px\)/.test(css), "css stacks the sidebar on phones");
ok(css.includes("env(safe-area-inset"), "css respects safe-area insets");
ok(/\.tabs\{[^}]*overflow-x:auto/.test(css), "css tab strip scrolls horizontally");

// 6) app.js: playbook features present
const app = read("app.js");
for (const [needle, label] of [
  ["function openPicker", "searchable picker"],
  ["function buildReview", "review-before-write"],
  ["function openModal", "modal system"],
  ["idbGet", "IndexedDB last-opened"],
  ["function bootProgress", "staged boot progress"],
  ["navigator.share", "Web Share out"],
  ["maybeConsumeSharedFile", "Web Share in"],
  ["showOpenFilePicker", "File System Access save-in-place"],
  ["writeToHandle", "save-in-place writer"],
  ["beforeunload", "unsaved-changes guard"],
  ["canShareFiles", "share capability gating"],
  // v2 depth
  ["function buildReferenceTab", "read-only Reference tab"],
  ["function decorateV2", "inline reference notes"],
  ["function nearestSample", "stat-curve lookup"],
  ["function runPreset", "presets"],
  ["function undo", "undo/redo"],
  ["function forceRefresh", "PWA force-refresh"],
  ["versionBehindCheck", "version-behind check"],
  ["api_unitref", "unit reference glue"],
]) ok(app.includes(needle), `app.js: ${label}`);

// 6b) v2 shell wiring
ok(/name="app-version"\s+content="[^"]+"/.test(html), "index.html carries an app-version meta");
ok(html.includes('data-t="ref2"'), "index.html has the Reference tab");
ok(html.includes('id="forceRefresh"'), "index.html has the Force-refresh button");
ok(/\.fnote\{/.test(css), "css styles inline field notes");
ok(/\.presets\{/.test(css), "css styles the presets bar");

// 7) reference JSON tables the engine loads exist (fetched from ../editor/ at runtime)
for (const j of ["ec_item_names.json", "ec_unit_names.json", "ec_equip_slots.json"])
  ok(existsSync(join(WEB, "..", "editor", j)), `editor/${j} present`);

console.log(fails ? `\n${fails} check(s) FAILED` : "\nvalidate: all checks passed");
process.exit(fails ? 1 : 0);
