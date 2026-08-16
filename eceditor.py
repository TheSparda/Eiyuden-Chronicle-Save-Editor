#!/usr/bin/env python3
"""Eiyuden Chronicle: Hundred Heroes save editor -- local web app, stdlib only.

Run:  py eceditor.py     then open http://127.0.0.1:8751

Nothing leaves the machine; the server only touches the save you point it at, always
backs a file up before its first write, and refuses to write anything that does not
decrypt back to exactly the data it intended to save.
"""
import http.server, socketserver, json, os, urllib.parse, threading, webbrowser, time

import ecsave

PORT = 8751

# Decrypting a 275 KB story save takes a few seconds in pure Python, so every save that
# gets decrypted stays parsed in memory for the life of the process. Entries are keyed by
# (path, mtime) so a file edited outside the editor is re-read, but simply reopening a
# slot -- or reopening one just written -- costs nothing.
_cache = {}
_cache_lock = threading.Lock()
_stats = {"hits": 0, "misses": 0}


def get_obj(path, force=False):
    key = os.path.abspath(path)
    mtime = os.path.getmtime(path)
    with _cache_lock:
        hit = _cache.get(key)
        if hit and hit[0] == mtime and not force:
            _stats["hits"] += 1
            return hit[1]
        _stats["misses"] += 1
    obj = ecsave.load_json(path)
    with _cache_lock:
        _cache[key] = (mtime, obj)
    return obj


def is_cached(path):
    key = os.path.abspath(path)
    with _cache_lock:
        hit = _cache.get(key)
    return bool(hit and hit[0] == os.path.getmtime(path))


def remember(path, obj):
    """Seed the cache with data we just wrote.

    write_save already verified this object round-trips through decryption, so it is
    exactly what the file holds -- no need to spend seconds decrypting it back.
    """
    with _cache_lock:
        _cache[os.path.abspath(path)] = (os.path.getmtime(path), obj)


PAGE = r"""<!doctype html>
<meta charset="utf-8">
<title>Eiyuden Chronicle Save Editor</title>
<style>
/* Eiyuden Chronicle theme: lacquered brown, brass filigree, parchment text --
   drawn from the key art's gold pocket-watch frame over a warm dark ground. */
:root{
  --bg:#150e05; --bg2:#2a1c09;
  --panel:#241806; --panel2:#33230c;
  --line:#6b4f1c; --line2:#8a6a26;
  --gold:#d9b449; --gold-hi:#f2dd9a; --gold-dim:#a2802c;
  --fg:#f4e7c8; --muted:#bda276;
  --accent:#d9b449; --ok:#9ec46b; --warn:#e2a33f; --err:#c8503f;
}
*{box-sizing:border-box}
body{margin:0;color:var(--fg);
  font:14px/1.5 "Segoe UI",system-ui,sans-serif;
  background:
    radial-gradient(1200px 620px at 22% -8%, #5a3d12 0%, rgba(90,61,18,0) 62%),
    radial-gradient(900px 520px at 108% 4%, #7a5312 0%, rgba(122,83,18,0) 55%),
    linear-gradient(160deg, var(--bg2) 0%, var(--bg) 58%);
  background-attachment:fixed;min-height:100vh}

/* Brass rule used under the header and around panels */
header{padding:14px 22px;display:flex;align-items:center;gap:16px;
  position:sticky;top:0;z-index:10;
  background:linear-gradient(180deg,#3a2709 0%,#241806 100%);
  border-bottom:2px solid transparent;
  border-image:linear-gradient(90deg,transparent,var(--gold-dim) 12%,var(--gold-hi) 50%,
    var(--gold-dim) 88%,transparent) 1;
  box-shadow:0 3px 14px rgba(0,0,0,.55)}
h1{font-size:19px;margin:0;font-weight:700;letter-spacing:.02em;
  font-family:"Trajan Pro","Georgia","Times New Roman",serif;
  background:linear-gradient(180deg,var(--gold-hi) 0%,var(--gold) 48%,#9c7722 100%);
  -webkit-background-clip:text;background-clip:text;color:transparent;
  text-shadow:0 1px 0 rgba(0,0,0,.4)}
h1::before{content:"❖";-webkit-text-fill-color:var(--gold);margin-right:10px;font-size:15px}
.sub{color:var(--muted);font-size:12px}
.wrap{display:flex;gap:16px;padding:16px;align-items:flex-start}
.side{width:280px;flex:0 0 280px}
.main{flex:1;min-width:0}
.card{background:linear-gradient(180deg,rgba(58,39,12,.85) 0%,var(--panel) 100%);
  border:1px solid var(--line);border-radius:6px;margin-bottom:14px;
  box-shadow:0 2px 12px rgba(0,0,0,.45), inset 0 1px 0 rgba(242,221,154,.10)}
.card h2{font-size:12px;margin:0;padding:10px 14px;
  border-bottom:1px solid var(--line);
  font-family:Georgia,"Times New Roman",serif;
  color:var(--gold);text-transform:uppercase;letter-spacing:.14em;font-weight:700}
.card h2::after{content:"";display:block;height:1px;margin-top:8px;
  background:linear-gradient(90deg,var(--gold-dim),transparent)}
.card .body{padding:12px 14px}
.slot{padding:9px 12px;border-bottom:1px solid rgba(107,79,28,.5);cursor:pointer;
  border-left:3px solid transparent;transition:background .12s,border-color .12s}
.slot:last-child{border-bottom:none}
.slot:hover{background:rgba(217,180,73,.09);border-left-color:var(--gold-dim)}
.slot.active{background:linear-gradient(90deg,rgba(217,180,73,.20),rgba(217,180,73,.04));
  border-left-color:var(--gold-hi)}
.slot.active .n{color:var(--gold-hi)}
.slot .n{font-weight:600}
.slot .m{color:var(--muted);font-size:12px}
.slot .when{font-size:11px;opacity:.75}
.diffrow{display:flex;align-items:center;gap:14px;flex-wrap:wrap;margin-top:4px}
.chk{display:flex;align-items:center;gap:6px;color:var(--fg);font-size:13px}
.chk input{width:auto;margin:0}
.pill{display:inline-block;padding:3px 12px;border-radius:99px;font-size:11px;
  font-weight:700;letter-spacing:.09em;text-transform:uppercase;
  font-family:Georgia,serif;border:1px solid}
.pill.normal{background:rgba(158,196,107,.12);color:#b6d98a;border-color:#5c7a3c}
.pill.hard{background:rgba(200,80,63,.16);color:#e79080;border-color:#8d3d2f}

/* edited-value affordances */
.ctl{display:flex;align-items:center;gap:4px}
.ctl input,.ctl select{flex:1;min-width:0}
input.dirty,select.dirty{border-color:var(--gold-hi);
  background:linear-gradient(180deg,rgba(242,221,154,.16),rgba(217,180,73,.07));
  box-shadow:inset 3px 0 0 var(--gold-hi)}
.chk input.dirty{outline:2px solid var(--gold-hi);outline-offset:2px}
.revert{display:none;flex:0 0 auto;width:22px;height:22px;padding:0;line-height:1;
  border-radius:50%;background:rgba(217,180,73,.12);color:var(--gold-hi);
  border:1px solid var(--gold-dim);box-shadow:none;
  font-size:13px;font-weight:400;cursor:pointer;align-items:center;justify-content:center}
.revert.show{display:inline-flex}
.revert:hover{background:var(--gold);color:#2a1b04}
#dirtycount{color:var(--gold-hi);font-size:12px;font-weight:600}
.itemname{color:var(--muted);font-size:12px}
/* Slot pickers sit in dense table cells: let them shrink, and lay the four equipment
   slots / seven rune holes out in a grid so a unit row stays one or two lines tall
   instead of eleven. */
.pick{min-width:0;width:100%;font-size:12px;padding:3px 6px}
.tablewrap{overflow-x:auto;max-width:100%}
td{vertical-align:top}
.slotgrid{display:grid;grid-template-columns:repeat(2,minmax(150px,1fr));gap:3px 8px}
.slotgrid.runes{grid-template-columns:repeat(2,minmax(140px,1fr))}
.slotcell{display:flex;align-items:center;gap:5px}
.slotcell .lab{color:var(--muted);font-size:10px;width:52px;flex:0 0 52px;
  text-transform:uppercase;letter-spacing:.05em}
.slotcell.locked{color:var(--muted);font-size:11px;font-style:italic;opacity:.6}
.additem{display:flex;gap:10px;align-items:flex-end;flex-wrap:wrap;
  padding:12px;border:1px solid var(--line);border-radius:5px;margin-top:10px;
  background:linear-gradient(180deg,rgba(217,180,73,.07),rgba(0,0,0,.18))}
.additem .f{display:flex;flex-direction:column;gap:3px}
.rm{background:transparent;border:1px solid rgba(200,80,63,.55);color:#d97a68;
  border-radius:4px;padding:3px 10px;font-size:11px;font-weight:600;cursor:pointer;
  box-shadow:none;letter-spacing:.06em;text-transform:uppercase}
.rm:hover{background:var(--err);color:#180a08;border-color:var(--err)}
tr.removed td{opacity:.4;text-decoration:line-through}
tr.added td{background:rgba(158,196,107,.12)}
.hint{color:var(--muted);font-size:11px;margin-top:2px;font-style:italic}
.chk{color:var(--fg)}
label{display:block;color:var(--muted);font-size:12px;margin-bottom:3px;
  letter-spacing:.03em}
input,select{background:rgba(11,7,3,.62);color:var(--fg);
  border:1px solid var(--line);border-radius:4px;padding:6px 8px;font:inherit;width:100%}
input:focus,select:focus{outline:none;border-color:var(--gold);
  box-shadow:0 0 0 2px rgba(217,180,73,.20)}
select option{background:#241806;color:var(--fg)}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:10px}
table{width:100%;border-collapse:collapse}
th,td{padding:6px 8px;text-align:left;border-bottom:1px solid rgba(107,79,28,.45);
  font-size:13px}
tbody tr:hover td,table tr:hover td{background:rgba(217,180,73,.05)}
th{color:var(--gold);font-weight:700;font-size:11px;text-transform:uppercase;
  letter-spacing:.10em;font-family:Georgia,serif;
  border-bottom:1px solid var(--line2)}
td input{padding:4px 6px}
.narrow{width:82px}
h3{font-family:Georgia,"Times New Roman",serif;letter-spacing:.12em;
  color:var(--gold)!important;text-transform:uppercase}
button{border:1px solid var(--gold-dim);border-radius:4px;padding:8px 16px;
  font:inherit;font-weight:700;cursor:pointer;letter-spacing:.04em;
  color:#2a1b04;
  background:linear-gradient(180deg,var(--gold-hi) 0%,var(--gold) 46%,#a9812a 100%);
  box-shadow:0 1px 0 rgba(242,221,154,.5) inset, 0 2px 8px rgba(0,0,0,.45)}
button:hover{filter:brightness(1.08)}
button:active{transform:translateY(1px)}
button.ghost{background:linear-gradient(180deg,rgba(217,180,73,.14),rgba(217,180,73,.05));
  color:var(--fg);border:1px solid var(--line2);box-shadow:none}
button.ghost:hover{background:rgba(217,180,73,.20)}
button:disabled{opacity:.45;cursor:not-allowed}
.bar{position:sticky;bottom:0;padding:12px 16px;display:flex;gap:12px;align-items:center;
  margin:0 -16px -16px;
  background:linear-gradient(180deg,#2c1d07,#1b1104);
  border-top:2px solid transparent;
  border-image:linear-gradient(90deg,transparent,var(--gold-dim) 15%,var(--gold-hi) 50%,
    var(--gold-dim) 85%,transparent) 1;
  box-shadow:0 -3px 14px rgba(0,0,0,.5)}
#msg{font-size:13px}
.ok{color:var(--ok)}.err{color:var(--err)}.warn{color:var(--warn)}
textarea{width:100%;height:460px;background:rgba(8,5,2,.75);color:var(--fg);
  border:1px solid var(--line);border-radius:4px;padding:10px;
  font:12px/1.5 Consolas,monospace;resize:vertical}
textarea:focus{outline:none;border-color:var(--gold)}
.tabs{display:flex;gap:4px;padding:10px 14px 0}
.tab{padding:7px 18px;border-radius:4px 4px 0 0;cursor:pointer;color:var(--muted);
  font-size:12px;font-family:Georgia,serif;letter-spacing:.09em;text-transform:uppercase;
  border:1px solid transparent;border-bottom:none}
.tab:hover{color:var(--fg)}
.tab.active{background:linear-gradient(180deg,rgba(217,180,73,.20),rgba(217,180,73,.05));
  color:var(--gold-hi);border-color:var(--line2)}
.note{color:var(--muted);font-size:12px;padding:0 14px 12px}
.spin{color:var(--gold)}
</style>

<header>
  <h1>Eiyuden Chronicle · Save Editor</h1>
  <span class="sub" id="hdr">TripleDES-CBC · verified round-trip · local only</span>
</header>

<div class="wrap">
  <div class="side">
    <div class="card">
      <h2>Save slots</h2>
      <div id="slots"><div class="body sub">scanning…</div></div>
    </div>
    <div class="card">
      <h2>Safety</h2>
      <div class="body sub">
        Close the game before writing — it caches saves in memory and will overwrite
        your changes on exit.<br><br>
        A <code>.bak</code> is made before the first write to any file.
      </div>
    </div>
  </div>

  <div class="main">
    <div class="card">
      <div class="tabs">
        <div class="tab active" data-t="edit">Editor</div>
        <div class="tab" data-t="raw">Raw JSON</div>
      </div>
      <div id="pane-edit">
        <div class="body" id="editor"><span class="sub">Pick a save slot on the left.</span></div>
      </div>
      <div id="pane-raw" style="display:none">
        <div class="note">Full decrypted save. Editing here overwrites the whole file —
          it must stay valid JSON.</div>
        <div class="body">
          <textarea id="rawbox" spellcheck="false"></textarea>
          <div style="margin-top:10px"><button id="writeraw">Write raw JSON</button></div>
        </div>
      </div>
    </div>
  </div>
</div>

<script>
let cur = null, data = null, cat = null;
let pendingRemove = new Set(), pendingAdd = [];

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
  host.innerHTML = mk("dl_equip", cat.equipment) + mk("dl_rune", cat.runes) +
                   mk("dl_item", cat.items);
}

const labelFor = (id, names) => {
  const hit = names.find(o => o.id === id);
  return hit ? `${id} — ${hit.name}` : String(id);
};
/* "6011 — Dragonscale Helmet" -> 6011; a bare number works too. */
const idFromLabel = s => {
  const m = String(s).trim().match(/^(\d+)/);
  return m ? parseInt(m[1], 10) : null;
};

const api = async (u, opt) => {
  const r = await fetch(u, opt);
  const t = await r.text();
  try { return JSON.parse(t); } catch(e) { throw new Error(t.slice(0,400)); }
};

function fmtTime(s){
  if (s == null) return "";
  s = Math.floor(s);
  return Math.floor(s/3600) + "h" + String(Math.floor((s%3600)/60)).padStart(2,"0") + "m";
}

async function loadSlots(){
  const j = await api("/api/saves");
  const el = document.getElementById("slots");
  if (!j.dirs.length){ el.innerHTML = '<div class="body err">No saves found.</div>'; return; }
  let h = "";
  for (const d of j.dirs)
    for (const s of d.saves){
      const meta = [s.level!=null?("Lv"+s.level):"", s.playtime!=null?fmtTime(s.playtime):"",
                    (s.size/1024).toFixed(0)+" KB"].filter(Boolean).join(" · ");
      h += `<div class="slot" data-p="${encodeURIComponent(s.path)}">
              <div class="n">${s.name}</div><div class="m">${meta}</div>
              <div class="m when">${s.saved || ""}</div></div>`;
    }
  el.innerHTML = h;
  el.querySelectorAll(".slot").forEach(n =>
    n.onclick = () => open(decodeURIComponent(n.dataset.p), n));
}

async function open(path, node){
  document.querySelectorAll(".slot").forEach(n => n.classList.remove("active"));
  if (node) node.classList.add("active");
  document.getElementById("editor").innerHTML =
     '<span class="spin">decrypting…</span>';
  cur = path;
  pendingRemove = new Set();
  pendingAdd = [];
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

function render(){
  const s = data.summary;
  let h = `<div class="grid">
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

  if (s.units.length){
    h += `<h3 style="font-size:13px;color:var(--muted);margin:18px 0 6px">
            UNITS (${s.units.length})</h3>
      <div class="hint">Equipment and runes accept a name or a raw id — start typing to search.</div>
      <div class="tablewrap"><table><tr><th>#</th><th>Unit</th><th>EXP</th><th>HP</th><th>MP</th>
      <th>Wpn</th><th>Equipment</th><th>Runes</th></tr>`;
    for (const u of s.units){
      const equip = `<div class="slotgrid">` + u.equipment.map((e,i) =>
        `<div class="slotcell"><span class="lab">${SLOTS[i]||("s"+i)}</span>
           <input class="pick" list="dl_equip" data-u="${u.index}" data-eq="${i}"
                  value="${labelFor(e, cat.equipment)}"></div>`).join("") + `</div>`;

      const runes = `<div class="slotgrid runes">` + u.runeHoles.map((r,i) =>
        u.runeReleased[i]
          ? `<div class="slotcell"><input class="pick" list="dl_rune"
               data-u="${u.index}" data-rune="${i}"
               value="${labelFor(r, cat.runes)}"></div>`
          : `<div class="slotcell locked">slot ${i+1} locked</div>`
      ).join("") + `</div>`;

      h += `<tr><td>${u.index}</td>
        <td>${u.name}<div class="itemname">${u.id}</div></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_exp" value="${u.exp}"></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_hp" value="${u.hp}"></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_mp" value="${u.mp}"></td>
        <td><input class="narrow" type="number" data-u="${u.index}" data-k="_weaponLevel" value="${u.weaponLevel}"></td>
        <td>${equip}</td><td>${runes}</td></tr>`;
    }
    h += `</table></div>`;
  }

  h += `<h3 style="font-size:13px;color:var(--muted);margin:18px 0 6px">
          INVENTORY (${s.items.length})</h3>
    <div class="additem">
      <div class="f"><label>Item</label>
        <input id="add_item" class="pick" list="dl_item" placeholder="type a name or id…"></div>
      <div class="f"><label>Quantity</label>
        <input id="add_qty" class="narrow" type="number" value="1" min="1"></div>
      <button class="ghost" id="add_btn" type="button">Add to inventory</button>
      <span class="hint" id="add_note">Stacks larger than the item's max are split automatically.</span>
    </div>
    <div class="tablewrap"><table id="invtable">
      <tr><th>#</th><th>Item</th><th>Count</th><th>Max</th><th></th></tr>`;
  for (const it of s.items){
    h += `<tr data-row="${it.index}"><td>${it.index}</td>
      <td>${it.name || "?"} <span class="itemname">(${it.id})</span></td>
      <td><input class="narrow" type="number" data-i="${it.index}" data-k="_count" value="${it.count}"></td>
      <td><input class="narrow" type="number" data-i="${it.index}" data-k="_max" value="${it.max}"></td>
      <td><button class="rm" type="button" data-rm="${it.index}">remove</button></td></tr>`;
  }
  h += `</table></div><div id="pendingadds"></div>`;

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
  decorate();
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
  const n = document.querySelectorAll("#editor .dirty").length
          + pendingRemove.size + pendingAdd.length;
  const el = document.getElementById("dirtycount");
  if (el) el.textContent = n ? `${n} unsaved change${n>1?"s":""}` : "";
}

function decorate(){
  document.querySelectorAll("#editor input, #editor select").forEach(el => {
    if (el.dataset.orig !== undefined) return;
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
  return {top, town, units, items, difficulty, removeItems, addItems};
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
      msg.className="ok";
      msg.textContent = `wrote ${j.bytes} bytes · ${j.changed} fields changed` +
                        (j.backup ? " · backup created" : "");
      await open(cur);
    }
  } catch(e){ msg.className="err"; msg.textContent = e.message; }
  btn.disabled = false;
}

document.querySelectorAll(".tab").forEach(t => t.onclick = () => {
  document.querySelectorAll(".tab").forEach(x => x.classList.remove("active"));
  t.classList.add("active");
  document.getElementById("pane-edit").style.display = t.dataset.t==="edit"?"":"none";
  document.getElementById("pane-raw").style.display  = t.dataset.t==="raw"?"":"none";
});

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

(async () => {
  cat = await api("/api/catalog");
  buildDatalists();
  document.getElementById("hdr").textContent =
    `${cat.items.length} items · ${cat.equipment.length} equipment · ${cat.runes.length} runes`;
  await loadSlots();
})();
</script>
"""


class Handler(http.server.BaseHTTPRequestHandler):
    def _send(self, code, body, ctype="application/json"):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *a):
        pass

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(u.query)
        try:
            if u.path == "/":
                return self._send(200, PAGE, "text/html; charset=utf-8")

            if u.path == "/api/catalog":
                return self._send(200, ecsave.catalog())

            if u.path == "/api/saves":
                dirs = []
                for d in ecsave.find_save_dirs():
                    dirs.append({"dir": d, "saves": ecsave.list_saves(d)})
                return self._send(200, {"dirs": dirs})

            if u.path == "/api/save":
                path = q.get("path", [""])[0]
                if not os.path.exists(path):
                    return self._send(200, {"error": "file not found"})
                cached = is_cached(path)
                t0 = time.time()
                obj = get_obj(path)
                return self._send(200, {"summary": ecsave.summarize(obj), "raw": obj,
                                        "cached": cached,
                                        "ms": int((time.time() - t0) * 1000)})

            self._send(404, {"error": "not found"})
        except Exception as e:
            self._send(200, {"error": f"{type(e).__name__}: {e}"})

    def do_POST(self):
        u = urllib.parse.urlparse(self.path)
        try:
            n = int(self.headers.get("Content-Length", 0))
            payload = json.loads(self.rfile.read(n) or b"{}")
            path = payload.get("path", "")
            if not os.path.exists(path):
                return self._send(200, {"error": "file not found"})

            if u.path == "/api/write":
                obj = get_obj(path)
                changed = ecsave.apply_edits(obj, payload.get("edits") or {})
                had_bak = os.path.exists(path + ".bak")
                size = ecsave.write_save(path, obj)
                remember(path, obj)
                return self._send(200, {"ok": True, "bytes": size, "changed": changed,
                                        "backup": not had_bak})

            if u.path == "/api/writeraw":
                obj = payload.get("json")
                if not isinstance(obj, dict):
                    return self._send(200, {"error": "payload must be a JSON object"})
                had_bak = os.path.exists(path + ".bak")
                size = ecsave.write_save(path, obj)
                remember(path, obj)
                return self._send(200, {"ok": True, "bytes": size,
                                        "backup": not had_bak})

            self._send(404, {"error": "not found"})
        except Exception as e:
            self._send(200, {"error": f"{type(e).__name__}: {e}"})


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == "__main__":
    url = f"http://127.0.0.1:{PORT}"
    print("Eiyuden Chronicle save editor")
    print("  ", url)
    print("   (Ctrl+C to stop)")
    for d in ecsave.find_save_dirs():
        print("   saves:", d)
    threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    with Server(("127.0.0.1", PORT), Handler) as srv:
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            print("\nbye")
