"""Run store (PRD F10.1): one folder per run under %LOCALAPPDATA%\\B2M\\runs, holding the plan, every
tool result and the run record with versions, so any run can be inspected or replayed."""

from __future__ import annotations

import json
import secrets
import threading
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from b2m import paths


class Run:
    def __init__(self, directory: Path):
        self.dir = directory
        self.id = directory.name
        self._lock = threading.Lock()

    def save(self, name: str, data: Any) -> None:
        with self._lock:
            temp = self.dir / f".{name}.json.tmp"
            temp.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
            temp.replace(self.dir / f"{name}.json")

    def load(self, name: str) -> Any:
        path = self.dir / f"{name}.json"
        return json.loads(path.read_text(encoding="utf-8")) if path.exists() else None

    def append(self, name: str, entry: Any) -> None:
        """Append one JSON line, e.g. each tool call and its result."""
        with self._lock, (self.dir / f"{name}.jsonl").open("a", encoding="utf-8") as f:
            f.write(json.dumps(entry, ensure_ascii=False) + "\n")

    def update(self, **fields: Any) -> dict[str, Any]:
        record = self.load("run") or {"run_id": self.id}
        record.update(fields)
        self.save("run", record)
        return record


class RunStore:
    def __init__(self, root: Path | None = None):
        self.root = root or paths.runs()

    def create(self) -> Run:
        stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")
        directory = self.root / f"{stamp}-{secrets.token_hex(2)}"
        directory.mkdir(parents=True)
        return Run(directory)

    def get(self, run_id: str) -> Run | None:
        if not run_id or "/" in run_id or "\\" in run_id or run_id.startswith("."):
            return None
        directory = self.root / run_id
        return Run(directory) if (directory / "run.json").exists() else None

    def list(self, limit: int = 50) -> list[dict[str, Any]]:
        if not self.root.is_dir():
            return []
        records = []
        for directory in sorted(self.root.iterdir(), reverse=True)[:limit]:
            record = Run(directory).load("run")
            if record:
                records.append(record)
        return records
