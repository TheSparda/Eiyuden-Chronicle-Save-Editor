#!/usr/bin/env python3
"""Compare local Eiyuden saves against Steam's cloud manifest.

Hundred Heroes uses Steam **Auto-Cloud**: Steam itself syncs the save files (the game
makes no cloud API calls), driven by a manifest at

    <Steam>/userdata/<accountid>/1658280/remotecache.vdf

which records each synced file's size, SHA1, and local/remote timestamps. Steam
reconciles that manifest when a game starts and when it exits -- so an edited save is
only safe once Steam has uploaded it, and a save that is older than the cloud copy can be
overwritten on next launch.

This script reports, per file, whether the local copy matches what Steam last synced.

    py cloudcheck.py
"""
import hashlib, os, re, sys

import ecsave

APP_ID = "1658280"


# --- a minimal VDF reader (key-value text format, quoted tokens + braces) ----------
_TOKEN = re.compile(r'"((?:[^"\\]|\\.)*)"|([{}])')


def parse_vdf(text):
    stack = [{}]
    key = None
    for m in _TOKEN.finditer(text):
        tok, brace = m.group(1), m.group(2)
        if brace == "{":
            node = {}
            stack[-1][key] = node
            stack.append(node)
            key = None
        elif brace == "}":
            stack.pop()
        elif key is None:
            key = tok.replace('\\\\', '\\')
        else:
            stack[-1][key] = tok.replace('\\\\', '\\')
            key = None
    return stack[0]


def steam_roots():
    """Candidate Steam installs (the manifest lives under the main install, not a library).
    Deduplicated -- the literal and expanded forms of a path are the same install."""
    seen, out = set(), []
    for base in (r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam",
                 os.path.expandvars(r"%ProgramFiles(x86)%\Steam"),
                 os.path.expandvars(r"%ProgramFiles%\Steam")):
        key = os.path.normcase(os.path.abspath(base))
        if key in seen or not os.path.isdir(os.path.join(base, "userdata")):
            continue
        seen.add(key)
        out.append(base)
    return out


def find_remotecaches():
    seen, hits = set(), []
    for root in steam_roots():
        udir = os.path.join(root, "userdata")
        for account in os.listdir(udir):
            path = os.path.join(udir, account, APP_ID, "remotecache.vdf")
            key = os.path.normcase(os.path.abspath(path))
            if os.path.exists(path) and key not in seen:
                seen.add(key)
                hits.append((account, path))
    return hits


def sha1(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


# --- status model, shared by the CLI and the editor UI ---------------------------
STATE_SYNCED = "synced"        # local matches what Steam last synced
STATE_LOCAL = "local-newer"    # edited since the last sync; Steam should upload it
STATE_CLOUD = "cloud-newer"    # cloud copy is ahead -- launching may overwrite local
STATE_MISSING = "missing"      # tracked by Steam but not on disk

STATE_LABEL = {
    STATE_SYNCED: "in sync",
    STATE_LOCAL: "LOCAL NEWER",
    STATE_CLOUD: "CLOUD NEWER",
    STATE_MISSING: "MISSING locally",
}

STATE_NOTE = {
    STATE_SYNCED: "",
    STATE_LOCAL: "Steam should upload this on next game launch",
    STATE_CLOUD: "launching may OVERWRITE your local edit",
    STATE_MISSING: "cloud copy would be downloaded",
}


def status(save_dir=None):
    """Cloud state for every save Steam tracks.

    Returns {"available": bool, "reason": str, "files": {basename: {...}}, "counts": {...}}.
    `available` is False when no manifest exists -- meaning Steam Cloud is off for the
    game (or it was never launched here), in which case local saves are authoritative.
    """
    caches = find_remotecaches()
    if not caches:
        return {"available": False,
                "reason": "no Steam Cloud manifest for this game -- "
                          "cloud saves appear to be off, so local files win",
                "files": {}, "counts": {}}

    if save_dir is None:
        dirs = ecsave.find_save_dirs()
        if not dirs:
            return {"available": False, "reason": "no save folder found",
                    "files": {}, "counts": {}}
        save_dir = dirs[0]

    # Manifest paths are relative to %LOCALAPPDATA%Low (Steam "root 12").
    lowdir = os.path.abspath(os.path.join(save_dir, "..", "..", "..", ".."))

    files, counts = {}, {STATE_SYNCED: 0, STATE_LOCAL: 0, STATE_CLOUD: 0, STATE_MISSING: 0}
    account_ids = []
    for account, path in caches:
        account_ids.append(account)
        data = parse_vdf(open(path, encoding="utf-8", errors="replace").read())
        for name, meta in (data.get(APP_ID) or {}).items():
            if not isinstance(meta, dict):
                continue
            local = os.path.join(lowdir, name.replace("/", os.sep))
            short = os.path.basename(name)

            if not os.path.exists(local):
                state = STATE_MISSING
            else:
                same = (str(os.path.getsize(local)) == meta.get("size")
                        and sha1(local).lower() == (meta.get("sha") or "").lower())
                if same:
                    state = STATE_SYNCED
                elif int(os.path.getmtime(local)) > int(meta.get("remotetime", 0) or 0):
                    state = STATE_LOCAL
                else:
                    state = STATE_CLOUD

            files[short] = {"state": state, "label": STATE_LABEL[state],
                            "note": STATE_NOTE[state], "path": local}
            counts[state] += 1

    return {"available": True, "reason": "", "files": files, "counts": counts,
            "accounts": account_ids}


def main():
    st = status()
    if not st["available"]:
        print(st["reason"])
        return 0

    rows = sorted((name, m["label"], m["note"]) for name, m in st["files"].items())
    width = max((len(r[0]) for r in rows), default=10)
    print(f"Steam account(s): {', '.join(st.get('accounts', []))}\n")
    for name, label, note in rows:
        print(f"  {name:<{width}}  {label:<16} {note}")

    c = st["counts"]
    print(f"\n  {len(rows)} tracked files: {c[STATE_LOCAL]} pending upload, "
          f"{c[STATE_CLOUD]} at risk, {c[STATE_MISSING]} missing locally")

    if c[STATE_CLOUD]:
        print("\n  WARNING: at least one local save is older than the cloud copy.")
        print("  Launching the game may replace it. See the guidance in the README.")
        return 2
    if c[STATE_LOCAL]:
        print("\n  Your edits are newer than the cloud. Start the game through Steam")
        print("  and Steam should upload them; if a Cloud Conflict dialog appears,")
        print("  choose the LOCAL / 'Upload to Steam Cloud' option.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
