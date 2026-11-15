"""Find running Revit processes that have the B2M add-in loaded.

The add-in writes one file per Revit process to %LOCALAPPDATA%\\B2M\\discovery\\revit-<year>-<pid>.json
with its transport, pipe name or port, and session token.
"""

from __future__ import annotations

import ctypes
import json
import os
import sys
from dataclasses import dataclass
from pathlib import Path

SCHEMA_VERSION = 1


def default_directory() -> Path:
    return Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")) / "B2M" / "discovery"


@dataclass(frozen=True)
class Target:
    """One Revit process with the add-in listening."""

    path: Path
    pid: int
    revit_year: int | None
    addin_version: str
    transport: str  # "pipe" or "tcp"
    pipe_name: str | None
    port: int | None
    token: str

    @property
    def target_id(self) -> str:
        return f"revit-{self.revit_year}-{self.pid}"


def parse(path: Path, text: str) -> Target | None:
    """Return the target a discovery file describes, or None if the file is not usable."""
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        return None
    if not isinstance(data, dict) or data.get("schema_version") != SCHEMA_VERSION or data.get("product") != "b2m-revit":
        return None
    transport = data.get("transport")
    token = data.get("auth_token")
    pid = data.get("pid")
    if transport not in ("pipe", "tcp") or not token or not isinstance(pid, int):
        return None
    if transport == "pipe" and not data.get("pipe_name"):
        return None
    if transport == "tcp" and not isinstance(data.get("port"), int):
        return None
    return Target(
        path=path,
        pid=pid,
        revit_year=data.get("revit_year"),
        addin_version=str(data.get("addin_version", "")),
        transport=transport,
        pipe_name=data.get("pipe_name"),
        port=data.get("port"),
        token=token,
    )


def process_alive(pid: int) -> bool:
    """True if a process with this ID is running. Windows only; elsewhere assume alive."""
    if sys.platform != "win32":
        return True
    from ctypes import wintypes

    synchronize, query_limited, access_denied, wait_timeout = 0x00100000, 0x1000, 5, 0x102
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    handle = kernel32.OpenProcess(synchronize | query_limited, False, pid)
    if not handle:
        # Access denied still means it exists; anything else means it is gone.
        return ctypes.get_last_error() == access_denied
    try:
        kernel32.WaitForSingleObject.argtypes = (wintypes.HANDLE, wintypes.DWORD)
        return kernel32.WaitForSingleObject(handle, 0) == wait_timeout
    finally:
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel32.CloseHandle(handle)


def find_targets(directory: Path | None = None, *, remove_stale: bool = True) -> list[Target]:
    """All live targets, newest Revit year first. Files of dead processes are deleted."""
    directory = directory or default_directory()
    targets: list[Target] = []
    if not directory.is_dir():
        return targets
    for path in sorted(directory.glob("revit-*.json")):
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            continue
        target = parse(path, text)
        if target is None:
            continue
        if not process_alive(target.pid):
            if remove_stale:
                path.unlink(missing_ok=True)
            continue
        targets.append(target)
    targets.sort(key=lambda t: (t.revit_year or 0, t.pid), reverse=True)
    return targets
