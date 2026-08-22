/* ============================================================================
   Eiyuden Chronicle Save Editor — WEB PORT prelude
   ----------------------------------------------------------------------------
   Runs the repo's own stdlib-only save module (editor/ecsave.py + pydes.py)
   unchanged inside Pyodide (CPython -> WebAssembly). The uploaded save is
   written into Pyodide's in-memory filesystem and edited by the exact same
   code the desktop app uses; the edited bytes are read back out and handed to
   the browser as a download. The save file never leaves the device.

   The rest of this file (below the prelude) is the desktop editor's own UI,
   reused verbatim. The only transport change is api(): instead of fetch()ing a
   local Python HTTP server, it dispatches to Pyodide glue that mirrors the same
   /api/* endpoints byte-for-byte.
   ============================================================================ */

const EDITOR_BASE = "../editor/";
const PY_MODULES  = ["ecsave.py", "pydes.py"];      // winbcrypt.py is Windows-only;
                                                    // pydes falls back to pure-Python.
const PY_JSON = [
  "ec_item_names.json", "ec_item_maxes.json", "ec_unit_names.json",
  "ec_unit_roles.json", "ec_unit_runeholes.json", "ec_unit_stats.json",
  "ec_equip_slots.json",
];

let PY = null;            // the Pyodide instance (set once ready)
let pyReady = null;       // Promise that resolves when the engine + module are loaded
let LOADED = [];          // [{path, name}] of saves written into Pyodide's MEMFS

/* Thin Python glue: import the real module and expose JSON-in / JSON-out helpers
   that reproduce eceditor.py's HTTP handlers exactly. All byte-twiddling,
   crypto, checksums and verification stay inside the trusted module. */
const GLUE = `
import json, os
import ecsave

_cache = {}   # abspath -> (mtime, obj); decrypting a story save costs seconds

def _get_obj(path, force=False):
    key = os.path.abspath(path)
    mtime = os.path.getmtime(path)
    hit = _cache.get(key)
    if hit and hit[0] == mtime and not force:
        return hit[1]
    obj = ecsave.load_json(path)
    _cache[key] = (mtime, obj)
    return obj

def _is_cached(path):
    key = os.path.abspath(path)
    hit = _cache.get(key)
    try:
        return bool(hit and hit[0] == os.path.getmtime(path))
    except OSError:
        return False

def _remember(path, obj):
    # write_save already verified this obj round-trips, so seed the cache with it
    _cache[os.path.abspath(path)] = (os.path.getmtime(path), obj)

def _cloud():
    # Steam Cloud state has no meaning in the browser; degrade gracefully.
    return {"available": False,
            "reason": "Steam Cloud status is not available in the browser editor.",
            "files": {}, "counts": {}}

def api_catalog():
    return json.dumps(ecsave.catalog())

def api_cloud():
    return json.dumps(_cloud())

def api_saves(loaded_json):
    loaded = json.loads(loaded_json)          # [{"path":..,"name":..}]
    saves = []
    for entry in loaded:
        p = entry.get("path")
        name = entry.get("name") or (os.path.basename(p) if p else "save.dat")
        if not p or not os.path.exists(p):
            continue
        try:
            s = ecsave.summarize(_get_obj(p))
            saves.append({
                "path": p, "name": name, "slot": None,
                "size": os.path.getsize(p),
                "saved": s.get("savedAt") or "",
                "level": None,                 # not present outside UserDataInfo.dat
                "playtime": s.get("seconds"),
                "fortressTownLevel": s.get("fortressTownLevel"),
            })
        except Exception as e:
            saves.append({"path": p, "name": name, "size": os.path.getsize(p),
                          "saved": "", "level": None, "playtime": None,
                          "error": "%s: %s" % (type(e).__name__, e)})
    dirs = [{"dir": "Uploaded save", "saves": saves}] if saves else []
    return json.dumps({"dirs": dirs, "cloud": _cloud()})

def api_save(path):
    if not path or not os.path.exists(path):
        return json.dumps({"error": "file not found"})
    try:
        cached = _is_cached(path)
        obj = _get_obj(path)
        return json.dumps({"summary": ecsave.summarize(obj), "raw": obj,
                           "cached": cached, "ms": 0})
    except Exception as e:
        return json.dumps({"error": "%s: %s" % (type(e).__name__, e)})

def api_write(path, edits_json):
    if not path or not os.path.exists(path):
        return json.dumps({"error": "file not found"})
    try:
        edits = json.loads(edits_json) or {}
        obj = _get_obj(path)
        changed = ecsave.apply_edits(obj, edits)
        size = ecsave.write_save(path, obj, make_backup=False)
        _remember(path, obj)
        return json.dumps({"ok": True, "bytes": size, "changed": changed,
                           "backup": False, "cloud": _cloud()})
    except Exception as e:
        return json.dumps({"error": "%s: %s" % (type(e).__name__, e)})

def api_writeraw(path, obj_json):
    if not path or not os.path.exists(path):
        return json.dumps({"error": "file not found"})
    try:
        obj = json.loads(obj_json)
        if not isinstance(obj, dict):
            return json.dumps({"error": "payload must be a JSON object"})
        size = ecsave.write_save(path, obj, make_backup=False)
        _remember(path, obj)
        return json.dumps({"ok": True, "bytes": size, "backup": False})
    except Exception as e:
        return json.dumps({"error": "%s: %s" % (type(e).__name__, e)})
`;

async function grab(url) {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`fetch ${url} (${r.status})`);
  return r;
}

async function bootPyodide() {
  const py = await loadPyodide();
  for (const m of PY_MODULES)
    py.FS.writeFile(m, await (await grab(EDITOR_BASE + m)).text());
  for (const j of PY_JSON)
    py.FS.writeFile(j, await (await grab(EDITOR_BASE + j)).text());
  py.runPython(GLUE);
  PY = py;
  return py;
}

/* Call a Python glue function; args cross as native strings, result is a JSON string. */
function pyCall(name, ...args) {
  const fn = PY.globals.get(name);
  try { return fn(...args); }
  finally { fn.destroy(); }
}

/* Let the browser paint a pending status message before a multi-second, main-thread
   blocking crypto call (pure-Python 3DES in WASM is slow but correct). A plain timeout
   is used rather than requestAnimationFrame so it still resolves when the tab is
   backgrounded (rAF is paused/throttled for hidden tabs). */
const nextPaint = () => new Promise(r => setTimeout(r, 32));

/* Drop-in replacement for the desktop app's fetch()-based api(): same URLs, same
   request/response shapes, dispatched to the in-browser Python glue instead. */
async function api(u, opt) {
  await pyReady;
  opt = opt || {};
  const url = new URL(u, "http://local");
  const p = url.pathname;
  const body = opt.body ? JSON.parse(opt.body) : null;

  if (p === "/api/catalog") return JSON.parse(pyCall("api_catalog"));
  if (p === "/api/cloud")   return JSON.parse(pyCall("api_cloud"));
  if (p === "/api/saves")   return JSON.parse(pyCall("api_saves", JSON.stringify(LOADED)));

  if (p === "/api/save") {
    const path = url.searchParams.get("path");
    await nextPaint();
    const t0 = performance.now();
    const res = JSON.parse(pyCall("api_save", path));
    if (!res.error && !res.cached) res.ms = Math.round(performance.now() - t0);
    return res;
  }
  if (p === "/api/write") {
    await nextPaint();
    const res = JSON.parse(pyCall("api_write", body.path, JSON.stringify(body.edits || {})));
    if (res.ok) downloadSave(body.path);
    return res;
  }
  if (p === "/api/writeraw") {
    await nextPaint();
    const res = JSON.parse(pyCall("api_writeraw", body.path, JSON.stringify(body.json || {})));
    if (res.ok) downloadSave(body.path);
    return res;
  }
  return { error: "not found: " + p };
}

/* Pull the edited bytes back out of MEMFS and hand them to the browser as a download,
   keeping the original filename so it can be copied straight back into the save folder. */
function downloadSave(path) {
  try {
    const bytes = PY.FS.readFile(path);          // Uint8Array
    const name = (LOADED.find(x => x.path === path) || {}).name || "UserData.dat";
    const url = URL.createObjectURL(new Blob([bytes], { type: "application/octet-stream" }));
    const a = document.createElement("a");
    a.href = url; a.download = name;
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 4000);
  } catch (e) { console.error("download failed", e); }
}

/* User picked / dropped a file: write it into MEMFS under its real name, register it,
   refresh the slot list, then open it. */
async function handleFile(file) {
  await pyReady;
  const editor = document.getElementById("editor");
  editor.innerHTML = '<span class="spin">reading file…</span>';
  try {
    const buf = new Uint8Array(await file.arrayBuffer());
    try { PY.FS.mkdir("/saves"); } catch (e) { /* already exists */ }
    const path = "/saves/" + file.name;
    PY.FS.writeFile(path, buf);
    if (!LOADED.find(x => x.path === path)) LOADED.push({ path, name: file.name });
    await loadSlots();
    const node = document.querySelector(`.slot[data-p="${encodeURIComponent(path)}"]`);
    await open(path, node);
  } catch (e) {
    editor.innerHTML = '<span class="err">Could not open that file: ' + e.message + '</span>';
  }
}

async function boot() {
  const engineStatus = document.getElementById("engineStatus");
  try {
    await pyReady;
    cat = await api("/api/catalog");
    buildNameMap();
    buildDatalists();
    document.getElementById("hdr").textContent =
      `${cat.items.length} items · ${cat.equipment.length} equipment · ${cat.runes.length} runes`;
    engineStatus.innerHTML = '<span class="ok">Engine ready — open a save file.</span>';
    document.getElementById("pickBtn").disabled = false;
  } catch (e) {
    engineStatus.innerHTML = '<span class="err">Engine failed to start: ' + e.message + '</span>';
    throw e;
  }
}

function registerPWA() {
  if ("serviceWorker" in navigator)
    navigator.serviceWorker.register("sw.js").catch(e => console.warn("SW register failed", e));
  const installBtn = document.getElementById("installBtn");
  const standalone = matchMedia("(display-mode: standalone)").matches || navigator.standalone;
  let deferredPrompt = null;
  if (!standalone) {
    window.addEventListener("beforeinstallprompt", e => {
      e.preventDefault(); deferredPrompt = e; installBtn.classList.remove("hidden");
    });
    installBtn.onclick = async () => {
      if (!deferredPrompt) return;
      deferredPrompt.prompt(); await deferredPrompt.userChoice;
      deferredPrompt = null; installBtn.classList.add("hidden");
    };
    window.addEventListener("appinstalled", () => installBtn.classList.add("hidden"));
  }
}

/* ===================== desktop editor UI (reused verbatim) ===================== */
let cur = null, data = null, cat = null, cloud = null;
let pendingRemove = new Set(), pendingAdd = [];
let pendingUnlock = new Set();          // "unitIndex:slot" for rune holes to unlock

const cloudOf = path => {
  if (!cloud || !cloud.available) return null;
  const base = path.split(/[\\/]/).pop();
  return cloud.files[base] || null;
};

/* Shared datalists keep the DOM small: one <option> set for hundreds of inputs,
   instead of a full <select> per slot across 120 units. */
function buildDatalists(){
  const mk = (id, list) =>
    `<datalist id="${id}">` +
    list.map(o => `<option value="${o.id} — ${o.name}">`).join("") +
    `</datalist>`;
  let host = document.getElementById("datalists");
  if (!host){
    host = document.createElement("div");
    host.id = "datalists";
    document.body.appendChild(host);
  }
  // One list per equipment slot, so the Head field offers only head gear, etc.
  let html = mk("dl_rune", cat.runes) + mk("dl_item", cat.items);
  for (const s of Object.keys(cat.equipBySlot))
    html += mk("dl_equip" + s, cat.equipBySlot[s]);
  // ...and one per item category, so the add-item picker can be narrowed
  for (const c of cat.categories)
    html += mk("dl_cat_" + slug(c), cat.items.filter(o => o.category === c));
  host.innerHTML = html;
}

const slug = s => s.replace(/[^a-z0-9]+/gi, "_").toLowerCase();

/* One id -> name map for every label, so a field still reads properly even if the item
   isn't in that slot's shortlist. */
let nameById = new Map();
function buildNameMap(){
  nameById = new Map();
  for (const o of cat.items) nameById.set(o.id, o.name);
  for (const o of cat.runes) if (!nameById.has(o.id)) nameById.set(o.id, o.name);
  for (const o of cat.equipment) if (!nameById.has(o.id)) nameById.set(o.id, o.name);
}
const labelFor = id =>
  nameById.has(id) ? `${id} — ${nameById.get(id)}` : String(id);
/* "6011 — Dragonscale Helmet" -> 6011; a bare number works too. */
const idFromLabel = s => {
  const m = String(s).trim().match(/^(\d+)/);
  return m ? parseInt(m[1], 10) : null;
};

/* api() is defined in the Pyodide prelude above (web port) */

function fmtTime(s){
  if (s == null) return "";
  s = Math.floor(s);
  return Math.floor(s/3600) + "h" + String(Math.floor((s%3600)/60)).padStart(2,"0") + "m";
}

async function loadSlots(){
  const j = await api("/api/saves");
  cloud = j.cloud || null;
  const el = document.getElementById("slots");
  if (!j.dirs.length){ el.innerHTML = '<div class="body err">No saves found.</div>'; return; }
  let h = "";
  for (const d of j.dirs)
    for (const s of d.saves){
      const meta = [s.level!=null?("Lv"+s.level):"", s.playtime!=null?fmtTime(s.playtime):"",
                    (s.size/1024).toFixed(0)+" KB"].filter(Boolean).join(" · ");
      const c = cloudOf(s.path);
      // "in sync" is the boring default -- only badge the states worth acting on
      const badge = (c && c.state !== "synced")
        ? `<div><span class="cloud ${c.state}" title="${c.note}">${c.label}</span></div>` : "";
      h += `<div class="slot" data-p="${encodeURIComponent(s.path)}">
              <div class="n">${s.name}</div><div class="m">${meta}</div>
              <div class="m when">${s.saved || ""}</div>${badge}</div>`;
    }
  el.innerHTML = h;
  el.querySelectorAll(".slot").forEach(n =>
    n.onclick = () => open(decodeURIComponent(n.dataset.p), n));
  renderCloudSummary();
}

function renderCloudSummary(note){
  const host = document.getElementById("cloudsummary");
  if (!host) return;

  const head = `<div class="cloudhead"><b>Steam Cloud</b>
      <button class="refresh" id="cloudrefresh" type="button"
              title="Re-check Steam Cloud now">⟳</button></div>`;

  let body;
  if (!cloud || !cloud.available){
    body = `<span class="sub">${cloud ? cloud.reason : "unknown"}</span>`;
  } else {
    const c = cloud.counts || {};
    const bits = [];
    if (c["cloud-newer"]) bits.push(
      `<span class="cloud cloud-newer">${c["cloud-newer"]} at risk</span>`);
    if (c["local-newer"]) bits.push(
      `<span class="cloud local-newer">${c["local-newer"]} to upload</span>`);
    if (c["missing"]) bits.push(`<span class="cloud missing">${c["missing"]} missing</span>`);
    if (!bits.length) bits.push(`<span class="cloud synced">all in sync</span>`);
    body = bits.join(" ");
  }
  host.innerHTML = head + body +
    (note ? `<div class="hint" style="margin-top:5px">${note}</div>` : "");
  const btn = document.getElementById("cloudrefresh");
  if (btn) btn.onclick = refreshCloud;
}

/* Steam rewrites its manifest when the game starts or exits, so the state can change
   while the editor is open. Re-read it on demand and repaint anything that shows it. */
async function refreshCloud(){
  const btn = document.getElementById("cloudrefresh");
  if (btn){ btn.disabled = true; btn.classList.add("spinning"); }
  const before = JSON.stringify(cloud && cloud.files || {});
  try{
    const fresh = await api("/api/cloud");
    const changed = JSON.stringify(fresh.files || {}) !== before;
    cloud = fresh;
    await loadSlots();                       // badges
    refreshBanner();   // swap the banner in place -- a full render() would discard
                       // whatever the user is part-way through editing
    renderCloudSummary(changed ? "updated — cloud state changed"
                               : "checked — no change");
  } catch(e){
    renderCloudSummary(`check failed: ${e.message}`);
  } finally {
    const b = document.getElementById("cloudrefresh");
    if (b){ b.disabled = false; b.classList.remove("spinning"); }
  }
}

async function open(path, node){
  document.querySelectorAll(".slot").forEach(n => n.classList.remove("active"));
  if (node) node.classList.add("active");
  document.getElementById("editor").innerHTML =
     '<span class="spin">decrypting…</span>';
  cur = path;
  pendingRemove = new Set();
  pendingAdd = [];
  pendingUnlock = new Set();
  data = await api("/api/save?path=" + encodeURIComponent(path));
  if (data.error){ document.getElementById("editor").innerHTML =
     '<span class="err">'+data.error+'</span>'; return; }
  render();
  document.getElementById("hdr").textContent = data.cached
    ? `loaded from cache (${data.ms} ms) — no re-decrypt`
    : `decrypted in ${(data.ms/1000).toFixed(1)}s — cached for this session`;
  document.getElementById("rawbox").value = JSON.stringify(data.raw, null, 2);
}

function num(id, label, val, step){
  return `<div><label>${label}</label>
    <input id="${id}" type="number" value="${val ?? ""}" ${step?`step="${step}"`:""}></div>`;
}

function cloudBanner(){
  const c = cloudOf(cur);
  if (!c) return "";
  if (c.state === "cloud-newer")
    return `<div class="banner risk" id="cloudbanner"><b>Steam Cloud is ahead of this file.</b>
      Launching the game may overwrite it — and any edit you make here.
      Consider disabling cloud saves for the game, or launching once to settle the
      conflict (choose the local copy) before editing.</div>`;
  if (c.state === "local-newer")
    return `<div class="banner info" id="cloudbanner"><b>Edited since Steam last synced.</b>
      Start the game through Steam to upload it; if a Cloud Conflict dialog appears,
      pick the local / “Upload to Steam Cloud” option.</div>`;
  return "";
}

/* Replace just the cloud banner, leaving every edited field untouched. Targeted by id so
   it never disturbs the roster's own warning banner. */
function refreshBanner(){
  const host = document.getElementById("sp-meta");
  if (!host || !cur || !data) return;
  const existing = document.getElementById("cloudbanner");
  if (existing) existing.remove();
  const html = cloudBanner();
  if (html) host.insertAdjacentHTML("afterbegin", html);
}

function render(){
  const s = data.summary;
  let h = `<div class="subpane active" id="sp-meta">` + cloudBanner() + `<div class="grid">
    ${num("f_money","Money",s.money)}
    ${num("f_seconds","Playtime (seconds)",s.seconds,"0.001")}
    ${num("f_town","Fortress town level",s.fortressTownLevel)}
    ${num("f_pop","Population",s.population)}
    ${num("f_lap","New Game+ count",s.lapPlayCount)}
  </div>
  <div class="sub" style="margin-top:8px">
    version ${s.versionCode} · game ${s.appVersionCode} ·
    playtime ${fmtTime(s.seconds)}${s.savedAt ? " · saved " + s.savedAt : ""}
  </div>`;

  const flagBoxes = Object.entries(s.difficultyFlagLabels).map(([k,label]) =>
    `<label class="chk"><input type="checkbox" data-df="${k}"
       ${s.difficultyFlags[k] ? "checked" : ""}> ${label}</label>`).join("");

  h += `<h3 style="font-size:13px;color:var(--muted);margin:18px 0 6px">DIFFICULTY</h3>
    <div class="diffrow">
      <div style="width:170px">
        <select id="f_diff">
          <option value="1" ${s.difficulty===1?"selected":""}>Normal</option>
          <option value="0" ${s.difficulty===0?"selected":""}>Hard</option>
        </select>
      </div>
      <span class="pill ${s.difficulty===0?"hard":"normal"}">currently ${s.difficultyName}</span>
    </div>
    <div class="diffrow" style="margin-top:10px">${flagBoxes}</div>
    <div class="sub" style="margin-top:6px">
      The five toggles are independent modifiers the game lists under the difficulty menu.
    </div>`;

  const SLOTS = ["Head","Body","Hands","Accessory"];

  // --- Recruit pane: the full roster
  h += `</div><div class="subpane" id="sp-roster">`;

  const rostPct = s.knownCount ? Math.round(100 * s.recruitedCount / s.knownCount) : 0;
  h += `<h3 style="font-size:13px;color:var(--muted);margin:4px 0 6px">ROSTER</h3>
    <div class="rosterprog">
      <div class="bar"><div class="fill" style="width:${rostPct}%"></div></div>
      <span class="n">${s.recruitedCount} / ${s.knownCount} recruited (${rostPct}%)</span>
    </div>
    <div class="banner info"><b>Recruiting works, but treat it as experimental.</b>
      Confirmed in-game: a recruited character loads into the party and into battle. EXP
      matches your player character, so they arrive at the party's level. HP/MP/weapon
      level are that character's <i>own</i> real numbers — not your player character's —
      scaled to match, so an early recruit doesn't inherit a stat block from wherever
      they were last seen recruited. Rune holes match that character's real count. What's
      <i>not</i> set: equipment (starts empty) and any recruitment-specific story flag, so
      content gated on "how you recruited them" may not trigger. A backup is made before
      writing.</div>
    <div class="rosterbar">
      <input type="search" id="rostfilter" placeholder="filter by name or id…">
      <select id="rostroletype">
        <option value="">All roles</option>
        <option value="Battle">Battle</option>
        <option value="Support">Support</option>
        <option value="Hybrid">Hybrid</option>
        <option value="Other">Castle-only</option>
      </select>
      <label class="chk"><input type="checkbox" id="rostonlymissing"> only missing</label>
      <button class="ghost" type="button" id="rostall">Recruit all</button>
      <button class="ghost" type="button" id="rostnone">Undo roster changes</button>
      <span class="hint" id="rostnote"></span>
    </div>
    <div class="rostergrid" id="rostergrid">`;
  for (const c of s.roster){
    const cls = (c.recruited ? "have" : "miss") + (c.protected ? " locked" : "");
    const holes = c.runeHoles == null ? "" : `${c.runeHoles} rune slot${c.runeHoles===1?"":"s"}`;
    const meta = c.protected ? `<span class="lockbadge">🔒 in party</span>`
                             : [holes].filter(Boolean).join(" · ");
    const role = c.role || "";
    const roleBadge = role
      ? `<span class="role ${role.toLowerCase()}">${role === "Other" ? "Castle" : role}</span>` : "";
    h += `<label class="rost ${cls}" data-rost="${c.id}" data-role="${role}"
             title="${c.protected ? "In your party or the player character — remove in-game first"
                                  : c.name}">
            <input type="checkbox" data-recruit="${c.id}"
                   ${c.recruited ? "checked" : ""} ${c.protected ? "disabled" : ""}>
            <span class="sw"></span>
            <span class="info">
              <span class="name">${c.name}${roleBadge}</span>
              <span class="meta"><span>#${c.id}</span>${meta ? " · "+meta : ""}</span>
            </span></label>`;
  }
  h += `</div>`;

  // --- Characters pane: per-unit stats and gear
  h += `</div><div class="subpane" id="sp-chars">`;

  if (s.units.length){
    h += `<h3 style="font-size:13px;color:var(--muted);margin:4px 0 6px">
            UNITS (${s.units.length})</h3>
      <div class="hint">Equipment and runes accept a name or a raw id — start typing to search.</div>
      <div class="tablewrap"><table><tr><th>#</th><th>Unit</th><th>EXP</th><th>HP</th><th>MP</th>
      <th>Wpn</th><th>Equipment</th><th>Runes</th></tr>`;
    for (const u of s.units){
      const equip = `<div class="slotgrid">` + u.equipment.map((e,i) =>
        `<div class="slotcell"><span class="lab">${SLOTS[i]||("s"+i)}</span>
           <input class="pick" list="dl_equip${i}" data-u="${u.index}" data-eq="${i}"
                  value="${labelFor(e)}"></div>`)
        .join("") + `</div>`;

      const runes = `<div class="slotgrid runes">` + u.runeHoles.map((r,i) =>
        u.runeReleased[i]
          ? `<div class="slotcell"><input class="pick" list="dl_rune"
               data-u="${u.index}" data-rune="${i}"
               value="${labelFor(r)}"></div>`
          : `<div class="slotcell locked" data-holecell="${u.index}:${i}">
               <button class="unlock" type="button"
                       data-unlock="${u.index}:${i}" data-slot="${i}"
                       title="Unlock this rune slot">🔒 slot ${i+1}</button></div>`
      ).join("") + `</div>`;

      const roleBadge = u.role
        ? `<span class="role ${u.role.toLowerCase()}">${u.role === "Other" ? "Castle" : u.role}</span>` : "";
      h += `<tr><td>${u.index}</td>
        <td>${u.name}${roleBadge}<div class="itemname">${u.id}</div></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_exp" value="${u.exp}"></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_hp" value="${u.hp}"></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_mp" value="${u.mp}"></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_weaponLevel" value="${u.weaponLevel}"></td>
        <td>${equip}</td><td>${runes}</td></tr>`;
    }
    h += `</table></div>`;
  }

  // --- Inventory pane
  h += `</div><div class="subpane" id="sp-inv">`;
  h += `<h3 style="font-size:13px;color:var(--muted);margin:4px 0 6px">
          INVENTORY — ${s.items.length} stacks</h3>
    <div class="additem">
      <div class="f"><label>Category</label>
        <select id="add_cat"><option value="">All categories</option>` +
        cat.categories.map(c => `<option>${c}</option>`).join("") + `</select></div>
      <div class="f"><label>Item</label>
        <input id="add_item" class="pick" list="dl_item" placeholder="type a name or id…"></div>
      <div class="f"><label>Quantity</label>
        <input id="add_qty" class="narrow" type="number" value="1" min="1"></div>
      <button class="ghost" id="add_btn" type="button">Add to inventory</button>
      <span class="hint" id="add_note">Stacks larger than the item's max are split automatically.</span>
    </div>
    <div class="invbar">
      <input type="search" id="invfilter" placeholder="filter items…">
      <button class="ghost" type="button" id="invexpand">Expand all</button>
      <button class="ghost" type="button" id="invcollapse">Collapse all</button>
      <span class="hint" id="invnote"></span>
    </div>`;

  // group the stacks by category -- a full save carries hundreds of them (220 Beigoma,
  // 122 cards), which is unreadable as one flat list
  const groups = new Map();
  for (const it of s.items){
    if (!groups.has(it.category)) groups.set(it.category, []);
    groups.get(it.category).push(it);
  }
  const order = cat.categories.filter(c => groups.has(c))
                  .concat([...groups.keys()].filter(c => !cat.categories.includes(c)));

  for (const c of order){
    const rows = groups.get(c);
    const total = rows.reduce((n, it) => n + (it.count || 0), 0);
    h += `<details class="catgroup" data-cat="${c}">
      <summary>${c}<span class="catcount">${rows.length} stack${rows.length>1?"s":""}
        · ${total} total</span></summary>
      <div class="tablewrap"><table>
        <tr><th>#</th><th>Item</th><th>Count</th><th>Max</th><th></th></tr>`;
    for (const it of rows){
      h += `<tr data-row="${it.index}" data-name="${(it.name||"").toLowerCase()} ${it.id}">
        <td>${it.index}</td>
        <td>${it.name || "?"} <span class="itemname">(${it.id})</span></td>
        <td><input class="narrow" type="number" data-i="${it.index}" data-k="_count" value="${it.count}"></td>
        <td><input class="narrow" type="number" data-i="${it.index}" data-k="_max" value="${it.max}"></td>
        <td><button class="rm" type="button" data-rm="${it.index}">remove</button></td></tr>`;
    }
    h += `</table></div></details>`;
  }
  h += `<div id="pendingadds"></div></div>`;

  h += `<div class="bar"><button id="write">Write save</button>
        <button class="ghost" id="revertall">Revert all</button>
        <button class="ghost" id="reload">Reload</button>
        <span id="dirtycount"></span>
        <span id="msg" class="sub">${cur}</span></div>`;

  document.getElementById("editor").innerHTML = h;
  document.getElementById("write").onclick = write;
  document.getElementById("reload").onclick = () => open(cur);
  document.getElementById("revertall").onclick = revertAll;
  wireInventory();
  wireRoster();
  wireUnlocks();
  decorate();
  showTab(activeTab === "raw" ? "meta" : activeTab);   // keep the tab across reloads
}

/* Clicking a locked rune slot marks it to be unlocked and swaps in a live rune picker,
   so the slot can be unlocked and filled in the same write. Clicking the small ↩ puts it
   back to locked (and drops anything typed, matching the game's own invariant that a
   locked hole never holds a rune). */
function wireUnlocks(){
  document.querySelectorAll("[data-unlock]").forEach(btn => {
    btn.onclick = () => {
      const key = btn.dataset.unlock;
      const [uIdx, slot] = key.split(":");
      const cell = document.querySelector(`[data-holecell="${key}"]`);
      pendingUnlock.add(key);
      cell.classList.remove("locked");
      cell.classList.add("unlocking");
      cell.innerHTML =
        `<input class="pick" list="dl_rune" data-u="${uIdx}" data-rune="${slot}"
                value="0 — Nothing">
         <button class="relock" type="button" data-relock="${key}"
                 title="Leave this slot locked">↩</button>`;
      wireRelock(cell);
      decorate();
      countDirty();
    };
  });
}

function wireRelock(cell){
  const btn = cell.querySelector("[data-relock]");
  if (!btn) return;
  btn.onclick = () => {
    const key = btn.dataset.relock;
    const [uIdx, slot] = key.split(":");
    pendingUnlock.delete(key);
    cell.classList.remove("unlocking");
    cell.classList.add("locked");
    cell.innerHTML = `<button class="unlock" type="button"
        data-unlock="${key}" data-slot="${slot}"
        title="Unlock this rune slot">🔒 slot ${Number(slot)+1}</button>`;
    wireUnlocks();
    countDirty();
  };
}

function wireRoster(){
  const grid = document.getElementById("rostergrid");
  if (!grid) return;

  const mark = box => {
    const row = box.closest(".rost");
    const changed = String(box.checked) !== box.dataset.orig;
    row.classList.toggle("changed", changed);
    row.classList.toggle("have", box.checked);
    row.classList.toggle("miss", !box.checked && !row.classList.contains("locked"));
    rosterNote();
  };
  grid.querySelectorAll("[data-recruit]").forEach(box => {
    box.dataset.orig = String(box.checked);
    box.addEventListener("change", () => mark(box));
  });

  const applyFilter = () => {
    const q = (document.getElementById("rostfilter").value || "").toLowerCase().trim();
    const onlyMissing = document.getElementById("rostonlymissing").checked;
    const role = document.getElementById("rostroletype").value;
    grid.querySelectorAll(".rost").forEach(row => {
      const box = row.querySelector("[data-recruit]");
      const hay = row.textContent.toLowerCase();
      const show = (!q || hay.includes(q)) && (!onlyMissing || !box.checked)
                 && (!role || row.dataset.role === role);
      row.style.display = show ? "" : "none";
    });
  };
  document.getElementById("rostfilter").addEventListener("input", applyFilter);
  document.getElementById("rostonlymissing").addEventListener("change", applyFilter);
  document.getElementById("rostroletype").addEventListener("change", applyFilter);

  document.getElementById("rostall").onclick = () => {
    grid.querySelectorAll("[data-recruit]:not(:disabled)").forEach(b => {
      if (!b.checked){ b.checked = true; mark(b); }
    });
    applyFilter();
  };
  document.getElementById("rostnone").onclick = () => {
    grid.querySelectorAll("[data-recruit]").forEach(b => {
      b.checked = b.dataset.orig === "true";
      b.closest(".rost").classList.remove("changed");
    });
    rosterNote();
    applyFilter();
  };
  rosterNote();
}

function rosterChanges(){
  const out = {};
  document.querySelectorAll("[data-recruit]").forEach(b => {
    if (String(b.checked) !== b.dataset.orig) out[b.dataset.recruit] = b.checked;
  });
  return out;
}

function rosterNote(){
  const c = rosterChanges();
  const add = Object.values(c).filter(Boolean).length;
  const rem = Object.values(c).length - add;
  const el = document.getElementById("rostnote");
  if (el) el.textContent = (add || rem)
    ? `${add} to recruit, ${rem} to remove` : "";

  // the progress bar reflects the pending state, not just what's on disk
  const total = document.querySelectorAll("[data-recruit]").length;
  const checked = document.querySelectorAll("[data-recruit]:checked").length;
  const fill = document.querySelector(".rosterprog .fill");
  const label = document.querySelector(".rosterprog .n");
  if (fill && total){
    const pct = Math.round(100 * checked / total);
    fill.style.width = pct + "%";
    label.textContent = `${checked} / ${total} recruited (${pct}%)`;
  }
  countDirty();
}

function wireInventory(){
  document.querySelectorAll("[data-rm]").forEach(b => b.onclick = () => {
    const i = +b.dataset.rm;
    const row = document.querySelector(`tr[data-row="${i}"]`);
    if (pendingRemove.has(i)){
      pendingRemove.delete(i); row.classList.remove("removed"); b.textContent = "remove";
    } else {
      pendingRemove.add(i); row.classList.add("removed"); b.textContent = "undo";
    }
    countDirty();
  });

  // category picker narrows which items the add field offers
  const catSel = document.getElementById("add_cat");
  const addField = document.getElementById("add_item");
  if (catSel && addField){
    catSel.onchange = () => {
      addField.setAttribute("list",
        catSel.value ? "dl_cat_" + slug(catSel.value) : "dl_item");
      addField.value = "";
      addField.placeholder = catSel.value
        ? `type a ${catSel.value.toLowerCase()} name or id…` : "type a name or id…";
      addField.focus();
    };
  }

  // filter + expand/collapse across the grouped stacks
  const groups = [...document.querySelectorAll(".catgroup")];
  const filt = document.getElementById("invfilter");
  if (filt){
    filt.addEventListener("input", () => {
      const q = filt.value.toLowerCase().trim();
      let shown = 0;
      for (const g of groups){
        let any = false;
        g.querySelectorAll("tr[data-row]").forEach(tr => {
          const hit = !q || (tr.dataset.name || "").includes(q);
          tr.style.display = hit ? "" : "none";
          if (hit) any = true;
        });
        g.style.display = any ? "" : "none";
        if (any && q) g.open = true;          // reveal matches as you type
        shown += any ? 1 : 0;
      }
      document.getElementById("invnote").textContent =
        q ? `${shown} categor${shown === 1 ? "y" : "ies"} match` : "";
    });
  }
  const setAll = open => groups.forEach(g => { g.open = open; });
  const ex = document.getElementById("invexpand");
  const co = document.getElementById("invcollapse");
  if (ex) ex.onclick = () => setAll(true);
  if (co) co.onclick = () => setAll(false);

  const btn = document.getElementById("add_btn");
  if (!btn) return;
  btn.onclick = () => {
    const field = document.getElementById("add_item");
    const id = idFromLabel(field.value);
    const qty = Math.max(1, +document.getElementById("add_qty").value || 1);
    if (!id) { alert("Pick an item (type a name or an id)."); return; }
    const known = cat.items.find(o => o.id === id);
    pendingAdd.push({_id: id, _count: qty, name: known ? known.name : "(unknown id)"});
    field.value = "";
    renderPendingAdds();
    countDirty();
  };
}

function renderPendingAdds(){
  const host = document.getElementById("pendingadds");
  if (!host) return;
  if (!pendingAdd.length){ host.innerHTML = ""; return; }
  host.innerHTML = `<div class="hint" style="margin-top:8px">To be added on write:</div>
    <table>` + pendingAdd.map((a,i) =>
      `<tr class="added"><td>new</td><td>${a.name} <span class="itemname">(${a._id})</span></td>
       <td>x${a._count}</td><td></td>
       <td><button class="rm" type="button" data-cancel="${i}">cancel</button></td></tr>`
    ).join("") + `</table>`;
  host.querySelectorAll("[data-cancel]").forEach(b => b.onclick = () => {
    pendingAdd.splice(+b.dataset.cancel, 1);
    renderPendingAdds();
    countDirty();
  });
}

/* Give every control a remembered original value, a dirty highlight, and its own
   revert button. Done after render so the field templates stay simple. */
const valOf = el => el.type === "checkbox" ? String(el.checked) : el.value;

function refresh(el, btn){
  const dirty = valOf(el) !== el.dataset.orig;
  el.classList.toggle("dirty", dirty);
  btn.classList.toggle("show", dirty);
  countDirty();
}

function countDirty(){
  const rosterN = document.querySelectorAll("#editor .rost.changed").length;
  const n = document.querySelectorAll("#editor .dirty").length
          + pendingRemove.size + pendingAdd.length + rosterN + pendingUnlock.size;
  const el = document.getElementById("dirtycount");
  if (el) el.textContent = n ? `${n} unsaved change${n>1?"s":""}` : "";
}

/* The roster grid does its own change tracking and has its own bulk controls, so it is
   left out of the generic dirty/revert decoration -- 121 revert buttons in a checkbox
   grid would be noise. Its filter controls aren't save data at all. */
const SKIP_DECORATE = "[data-recruit],#rostfilter,#rostonlymissing";

function decorate(){
  document.querySelectorAll("#editor input, #editor select").forEach(el => {
    if (el.dataset.orig !== undefined) return;
    if (el.matches(SKIP_DECORATE)) return;
    el.dataset.orig = valOf(el);

    const ctl = document.createElement(el.type === "checkbox" ? "span" : "span");
    ctl.className = "ctl";
    el.parentNode.insertBefore(ctl, el);
    ctl.appendChild(el);

    const btn = document.createElement("button");
    btn.className = "revert";
    btn.type = "button";
    btn.textContent = "↺";                    // ↺
    btn.title = "Revert to original value";
    ctl.appendChild(btn);

    btn.onclick = () => {
      if (el.type === "checkbox") el.checked = el.dataset.orig === "true";
      else el.value = el.dataset.orig;
      refresh(el, btn);
    };
    el.addEventListener("input", () => refresh(el, btn));
    el.addEventListener("change", () => refresh(el, btn));

    /* A datalist filters its options by what's already in the box, so a field
       pre-filled with "6011 — Dragonscale Helmet" matches only itself and the
       dropdown looks broken. Empty the box on the way in (before the click opens
       the popup, hence mousedown) so the full list shows, and put the old value
       back if nothing was chosen. */
    if (el.classList.contains("pick")){
      const ph0 = el.placeholder || "";          // e.g. the add-item field's own hint
      const stash = () => {
        if (el.value !== ""){
          el.dataset.prev = el.value;
          el.placeholder = el.value;
          el.value = "";
        }
      };
      const restore = () => {
        if (el.value.trim() === "" && el.dataset.prev !== undefined)
          el.value = el.dataset.prev;
        el.placeholder = ph0;
        refresh(el, btn);
      };
      el.addEventListener("mousedown", stash);
      el.addEventListener("focus", stash);
      el.addEventListener("blur", restore);
      el.addEventListener("keydown", e => { if (e.key === "Escape") { el.blur(); } });
    }
  });
  countDirty();
}

function revertAll(){
  document.querySelectorAll("#editor input, #editor select").forEach(el => {
    if (el.dataset.orig === undefined) return;
    if (el.type === "checkbox") el.checked = el.dataset.orig === "true";
    else el.value = el.dataset.orig;
    el.classList.remove("dirty");
  });
  document.querySelectorAll("#editor .revert").forEach(b => b.classList.remove("show"));
  // roster, pending adds and pending removals are tracked separately
  document.querySelectorAll("[data-recruit]").forEach(b => {
    b.checked = b.dataset.orig === "true";
    b.closest(".rost").classList.remove("changed");
  });
  pendingRemove = new Set();
  pendingAdd = [];
  document.querySelectorAll("[data-relock]").forEach(b => b.click());   // re-lock slots
  document.querySelectorAll("tr.removed").forEach(r => r.classList.remove("removed"));
  document.querySelectorAll("[data-rm]").forEach(b => b.textContent = "remove");
  renderPendingAdds();
  rosterNote();
  countDirty();
}

function collect(){
  const top = {}, town = {}, units = {}, items = {}, difficulty = {};
  const g = id => document.getElementById(id).value;
  top._money = +g("f_money");
  top._seconds = +g("f_seconds");
  top._lapPlayCount = +g("f_lap");
  if (g("f_town") !== "") town._fortressTownLevel = +g("f_town");
  if (g("f_pop") !== "") town._population = +g("f_pop");

  document.querySelectorAll("[data-u]").forEach(el => {
    const i = el.dataset.u;
    units[i] = units[i] || {};
    if (el.dataset.k) units[i][el.dataset.k] = +el.value;
    else if (el.dataset.eq !== undefined){
      const id = idFromLabel(el.value);
      if (id !== null){
        units[i]._equipment = units[i]._equipment || [];
        units[i]._equipment[+el.dataset.eq] = id;
      }
    } else if (el.dataset.rune !== undefined){
      const id = idFromLabel(el.value);
      if (id !== null){
        units[i]._runeHoles = units[i]._runeHoles || [];
        units[i]._runeHoles[+el.dataset.rune] = id;
      }
    }
  });

  for (const key of pendingUnlock){
    const [i, slot] = key.split(":");
    units[i] = units[i] || {};
    units[i]._runeHoleReleased = units[i]._runeHoleReleased || {};
    units[i]._runeHoleReleased[slot] = true;
  }
  document.querySelectorAll("[data-i]").forEach(el => {
    const i = el.dataset.i;
    items[i] = items[i] || {};
    items[i][el.dataset.k] = +el.value;
  });

  difficulty._difficulty = +g("f_diff");
  document.querySelectorAll("[data-df]").forEach(el => {
    difficulty[el.dataset.df] = el.checked;
  });

  const removeItems = [...pendingRemove];
  const addItems = pendingAdd.map(a => ({_id: a._id, _count: a._count}));
  return {top, town, units, items, difficulty, removeItems, addItems,
          recruit: rosterChanges()};
}

async function write(){
  const btn = document.getElementById("write"), msg = document.getElementById("msg");
  btn.disabled = true; msg.className = "spin"; msg.textContent = "encrypting + verifying…";
  try{
    const j = await api("/api/write", {method:"POST",
      headers:{"Content-Type":"application/json"},
      body: JSON.stringify({path: cur, edits: collect()})});
    if (j.error){ msg.className="err"; msg.textContent = j.error; }
    else {
      if (j.cloud) cloud = j.cloud;
      const c = cloudOf(cur);
      msg.className="ok";
      msg.textContent = `wrote ${j.bytes} bytes · ${j.changed} fields changed` +
                        (j.backup ? " · backup created" : "") +
                        (c && c.state === "local-newer"
                           ? " · launch via Steam to upload" : "");
      await open(cur);
      loadSlots();          // refresh the badges now that this file has changed
    }
  } catch(e){ msg.className="err"; msg.textContent = e.message; }
  btn.disabled = false;
}

/* Overview / Characters / Inventory are sub-panes inside the editor (they share one
   render and one Write bar); Raw JSON is a separate pane. */
let activeTab = "meta";
const SUBPANES = {meta: "sp-meta", chars: "sp-chars", roster: "sp-roster",
                  inv: "sp-inv"};

function showTab(name){
  activeTab = name;
  document.querySelectorAll(".tab").forEach(x =>
    x.classList.toggle("active", x.dataset.t === name));
  const isRaw = name === "raw";
  document.getElementById("pane-edit").style.display = isRaw ? "none" : "";
  document.getElementById("pane-raw").style.display  = isRaw ? "" : "none";
  for (const [key, id] of Object.entries(SUBPANES)){
    const el = document.getElementById(id);
    if (el) el.classList.toggle("active", key === name);
  }
}

document.querySelectorAll(".tab").forEach(t => t.onclick = () => showTab(t.dataset.t));

document.getElementById("writeraw").onclick = async () => {
  if (!cur) return alert("Open a save first.");
  let obj;
  try { obj = JSON.parse(document.getElementById("rawbox").value); }
  catch(e){ return alert("Invalid JSON: " + e.message); }
  const j = await api("/api/writeraw", {method:"POST",
    headers:{"Content-Type":"application/json"},
    body: JSON.stringify({path: cur, json: obj})});
  alert(j.error ? ("Error: " + j.error) : `Wrote ${j.bytes} bytes.`);
  if (!j.error) open(cur);
};

/* boot is driven from the web-port wiring below */

/* ===================== web-port boot wiring (runs last) ===================== */
(function () {
  const drop = document.getElementById("drop");
  const fileInput = document.getElementById("file");
  const pickBtn = document.getElementById("pickBtn");

  pickBtn.onclick = () => fileInput.click();
  fileInput.onchange = () => { if (fileInput.files[0]) handleFile(fileInput.files[0]); };

  ["dragenter", "dragover"].forEach(ev =>
    drop.addEventListener(ev, e => { e.preventDefault(); drop.classList.add("hot"); }));
  ["dragleave", "drop"].forEach(ev =>
    drop.addEventListener(ev, e => { e.preventDefault(); drop.classList.remove("hot"); }));
  drop.addEventListener("drop", e => {
    const f = e.dataTransfer.files && e.dataTransfer.files[0];
    if (f) handleFile(f);
  });

  pyReady = bootPyodide();
  boot();
  registerPWA();
})();
