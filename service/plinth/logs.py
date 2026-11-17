"""Service logging into the shared Plinth log folder, next to the add-in's logs (PRD: Supportability)."""

from __future__ import annotations

import logging
from datetime import datetime

from plinth import paths


def configure(level: int = logging.INFO) -> None:
    folder = paths.logs()
    folder.mkdir(parents=True, exist_ok=True)
    handler = logging.FileHandler(folder / f"service-{datetime.now():%Y%m%d}.log", encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s [service] [%(name)s] %(levelname)s %(message)s"))
    root = logging.getLogger()
    root.setLevel(level)
    if not any(isinstance(h, logging.FileHandler) and h.baseFilename == handler.baseFilename for h in root.handlers):
        root.addHandler(handler)
