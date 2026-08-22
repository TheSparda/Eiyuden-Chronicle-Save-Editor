# Eiyuden Chronicle Save Editor — Web version

A browser-based twin of the desktop editor. It opens a save you pick, lets you edit it,
and downloads the edited copy back. **Everything runs in your browser — the save file
never leaves your device**, and it installs to a phone home screen and works offline
after the first load.

Live (once GitHub Pages is enabled — see below):
`https://thesparda.github.io/Eiyuden-Chronicle-Save-Editor/web/`

## How it works

It runs the repo's own save module — [`editor/ecsave.py`](../editor/ecsave.py) and
[`editor/pydes.py`](../editor/pydes.py) — **unchanged**, inside
[Pyodide](https://pyodide.org) (CPython compiled to WebAssembly). The uploaded save is
written into Pyodide's in-memory filesystem and edited by the exact same code the desktop
app uses; the edited bytes are read straight back out and handed to the browser as a
download. TripleDES-CBC crypto, JSON (de)serialization, and the write-time verification
round-trip are all done by the trusted module — nothing is reimplemented in JavaScript.

The UI is the desktop editor's own interface, reused verbatim. The only change is the
transport: instead of `fetch()`ing a local Python HTTP server, `api()` in
[`app.js`](app.js) dispatches to thin Python glue that mirrors the same `/api/*`
endpoints. On non-Windows platforms (including WASM) `pydes` automatically falls back to
its pure-Python 3DES, so no native dependency is needed.

## Files

```
web/
  index.html            shell + PWA <head> tags + file-drop UI
  style.css             the desktop editor's theme, verbatim (+ a few drop-zone rules)
  app.js                Pyodide bootstrap, Python glue, the api() shim, download, PWA
  manifest.webmanifest  PWA metadata
  sw.js                 service worker (installable + offline)
  icons/                192, 512, and maskable-512 PNGs
```

At runtime the page fetches `../editor/ecsave.py`, `../editor/pydes.py`, and the
`../editor/ec_*.json` name tables, so **the site must be served from the repo root** (not
from `web/` as the site root). A root-level `.nojekyll` is committed so GitHub Pages
serves the `.py` files verbatim.

## Run it locally

Serve the **repo root** so `../editor/…` resolves, then open the `web/` path:

```bash
python3 -m http.server 8791
```

Then open <http://localhost:8791/web/>.

## Using it (desktop)

1. Wait for **"Engine ready"** (first load pulls the Pyodide runtime, ~10 MB).
2. Drop a `UserData`*`NN`*`.dat` file on the drop zone, or **Choose file…**
3. Edit across the Overview / Characters / Recruit / Inventory / Raw JSON tabs.
4. **Write save** → an edited `.dat` downloads (same filename).
5. **Close the game**, then copy the edited file back into your save folder, replacing the
   original. (The game caches saves in memory and overwrites them on exit, so it must be
   closed first.)

Your original file is never modified — you always download a copy.

## Install on Android / offline

1. Open the Pages URL in Chrome on Android; wait for "Engine ready".
2. Tap **⬇ Install app** (or ⋮ → *Install app*) to add it to the home screen.
3. After the first online load it works offline. (Pyodide's runtime is cached on first
   run; the app shell is cached immediately.)

iOS Safari has no install prompt — use *Share → Add to Home Screen*.

## A note on speed

Pure-Python 3DES in WebAssembly is correct but not fast: decrypting or writing a large
story save can take noticeably longer than the desktop app (writing also re-decrypts to
verify). The UI shows a status while it works. Small/early saves are near-instant.

## Deploying (GitHub Pages)

Pages must build from the **repo root** (branch `main`, `/ (root)`), because the app
fetches `../editor/…`. With `.nojekyll` committed at the root:

```bash
gh api -X POST /repos/TheSparda/Eiyuden-Chronicle-Save-Editor/pages \
  -H "Accept: application/vnd.github+json" \
  -f 'source[branch]=main' -f 'source[path]=/'
```

Or **Settings → Pages → Deploy from a branch → `main` / `/ (root)`**. The site then lives
at `https://thesparda.github.io/Eiyuden-Chronicle-Save-Editor/web/`.

If you bump the pinned Pyodide version in `index.html`, bump the `CACHE` string in
`sw.js` too so clients drop the old cache.
