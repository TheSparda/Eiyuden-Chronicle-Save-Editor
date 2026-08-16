"""Shared helpers for the test scripts.

Saves are located by discovery rather than a hardcoded path, so the tests carry no
machine-specific or account-specific details (the save folder is named after the
player's SteamID64).
"""
import os
import ecsave


def save_dir():
    dirs = ecsave.find_save_dirs()
    if not dirs:
        raise SystemExit("No Eiyuden Chronicle save folder found on this machine.")
    return dirs[0]


def save_path(filename):
    path = os.path.join(save_dir(), filename)
    if not os.path.exists(path):
        raise SystemExit(f"{filename} not found in {save_dir()}")
    return path


def pick_save(min_size=0, max_size=float("inf"), newest=True):
    """A save in a size range -- lets tests target 'a small one' / 'a big one'
    without naming slots that only exist on one machine."""
    saves = [s for s in ecsave.list_saves(save_dir())
             if min_size <= s["size"] <= max_size]
    if not saves:
        raise SystemExit("no save matched the requested size range")
    saves.sort(key=lambda s: s["mtime"], reverse=newest)
    return saves[0]["path"]
