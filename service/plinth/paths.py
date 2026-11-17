"""Folders shared with the add-in: %LOCALAPPDATA%\\Plinth\\{discovery,logs,runs,imports,cache,tools}. PLINTH_HOME overrides
the root (tests)."""

from __future__ import annotations

import os
from pathlib import Path


def root() -> Path:
    override = os.environ.get("PLINTH_HOME")
    if override:
        return Path(override)
    return Path(os.environ.get("LOCALAPPDATA", Path.home() / "AppData" / "Local")) / "Plinth"


def discovery() -> Path:
    return root() / "discovery"


def logs() -> Path:
    return root() / "logs"


def runs() -> Path:
    return root() / "runs"


def imports() -> Path:
    """Drawing-import projects (PRD Step 9), one folder each."""
    return root() / "imports"


def dxf_cache() -> Path:
    """DXF files converted from DWG, named by the DWG's content hash."""
    return root() / "cache" / "dxf"


def tools() -> Path:
    """Portable helper programs, e.g. tools\\oda\\ODAFileConverter.exe."""
    return root() / "tools"
