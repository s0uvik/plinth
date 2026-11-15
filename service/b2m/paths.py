"""Folders shared with the add-in: %LOCALAPPDATA%\\B2M\\{discovery,logs,runs}. B2M_HOME overrides the root (tests)."""

from __future__ import annotations

import os
from pathlib import Path


def root() -> Path:
    override = os.environ.get("B2M_HOME")
    if override:
        return Path(override)
    return Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")) / "B2M"


def discovery() -> Path:
    return root() / "discovery"


def logs() -> Path:
    return root() / "logs"


def runs() -> Path:
    return root() / "runs"
