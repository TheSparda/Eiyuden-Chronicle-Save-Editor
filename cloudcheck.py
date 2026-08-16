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


def main():
    caches = find_remotecaches()
    if not caches:
        print("No Steam cloud manifest found for Eiyuden Chronicle (app 1658280).")
        print("Either the game was never launched on this machine, or Steam Cloud is off")
        print("for it -- in which case your local saves are already authoritative.")
        return 0

    save_dirs = ecsave.find_save_dirs()
    if not save_dirs:
        print("No save folder found.")
        return 1
    save_dir = save_dirs[0]
    # manifest paths are relative to %LOCALAPPDATA%Low (Steam "root 12" = WinAppDataLocalLow)
    lowdir = os.path.abspath(os.path.join(save_dir, "..", "..", "..", ".."))

    rc = 0
    for account, path in caches:
        print(f"Steam account {account}")
        print(f"  manifest: {path}\n")
        data = parse_vdf(open(path, encoding="utf-8", errors="replace").read())
        app = data.get(APP_ID, {})

        rows, pending, risky, missing = [], 0, 0, 0
        for name, meta in app.items():
            if not isinstance(meta, dict):
                continue
            local = os.path.join(lowdir, name.replace("/", os.sep))
            short = os.path.basename(name)
            if not os.path.exists(local):
                rows.append((short, "MISSING locally", "cloud copy would be downloaded"))
                missing += 1
                continue

            same_size = str(os.path.getsize(local)) == meta.get("size")
            same_hash = sha1(local).lower() == (meta.get("sha") or "").lower()
            mtime = int(os.path.getmtime(local))
            remote_t = int(meta.get("remotetime", 0) or 0)

            if same_hash and same_size:
                rows.append((short, "in sync", ""))
            elif mtime > remote_t:
                rows.append((short, "LOCAL NEWER",
                             "Steam should upload this on next game launch"))
                pending += 1
            else:
                rows.append((short, "CLOUD NEWER",
                             "launching may OVERWRITE your local edit"))
                risky += 1

        width = max(len(r[0]) for r in rows) if rows else 10
        for name, state, note in sorted(rows):
            print(f"  {name:<{width}}  {state:<16} {note}")

        print()
        print(f"  {len(rows)} tracked files: {pending} pending upload, "
              f"{risky} at risk, {missing} missing locally")
        if risky:
            print("\n  WARNING: at least one local save is older than the cloud copy.")
            print("  Launching the game may replace it. See the guidance in the README.")
            rc = 2
        elif pending:
            print("\n  Your edits are newer than the cloud. Start the game through Steam")
            print("  and Steam should upload them; if a Cloud Conflict dialog appears,")
            print("  choose the LOCAL / 'Upload to Steam Cloud' option.")
    return rc


if __name__ == "__main__":
    sys.exit(main())
