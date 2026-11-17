"""The service-side gateway (PRD F11.2, F11.3): direct catalog calls for Claude Desktop, and the 17 tools that ran
in rvt-mcp's own .NET gateway process, rebuilt here on files under %LOCALAPPDATA%\\Plinth.

- Targets: which Revit process and document direct calls go to. Writes need a confirmed document (switch_target
  with verify), exactly like builds.
- Every direct call is logged to logs/revit-calls.jsonl; operation status, usage patterns and failure records are
  read from that log.
- Change history: reasons attached to logged calls (record_change), with title aliases after Save As
  (resolve_history_identity).
- Code modules and baked-tool suggestions: developer-mode features, stored as files.
"""

from __future__ import annotations

import hashlib
import json
import re
import secrets
import threading
import time
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from plinth import paths
from plinth.revit import RevitClient, RevitError, find_targets
from plinth.revit.errors import HINTS, hint

MAX_LOGGED_PARAMS = 4000
SUGGEST_AFTER = 3  # identical send_code snippets before suggesting a baked tool


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def developer_mode() -> bool:
    """Same switch the add-in reads: %LOCALAPPDATA%\\Plinth\\addin-settings.json {"developer_mode": true}."""
    try:
        return bool(json.loads((paths.root() / "addin-settings.json").read_text(encoding="utf-8")).get("developer_mode"))
    except (OSError, ValueError):
        return False


class GatewayError(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


def _read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    rows = []
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            rows.append(json.loads(line))
        except ValueError:
            continue
    return rows


def _append_jsonl(path: Path, row: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(row, ensure_ascii=False) + "\n")


class Gateway:
    def __init__(self, lock: threading.Lock, connect: Callable[..., RevitClient] | None = None):
        self._lock = lock  # shared with the build service: one Revit operation at a time
        self._connect = connect or (lambda target=None: RevitClient.connect(target))
        self.pinned_pid: int | None = None
        self.document_id: str | None = None
        self.document_title: str | None = None
        self.commands_cache: list[dict[str, Any]] | None = None

    # ---- files
    @property
    def calls_log(self) -> Path:
        return paths.logs() / "revit-calls.jsonl"

    @property
    def changes_log(self) -> Path:
        return paths.root() / "history" / "changes.jsonl"

    @property
    def aliases_file(self) -> Path:
        return paths.root() / "history" / "aliases.json"

    # ---- targets
    def list_targets(self) -> list[dict[str, Any]]:
        return [{"target_id": t.target_id, "pid": t.pid, "revit_year": t.revit_year, "addin_version": t.addin_version,
                 "transport": t.transport, "pinned": t.pid == self.pinned_pid} for t in find_targets()]

    def _target(self):
        targets = find_targets()
        if self.pinned_pid is not None:
            for t in targets:
                if t.pid == self.pinned_pid:
                    return t
            raise GatewayError(409, f"The pinned Revit (pid {self.pinned_pid}) is no longer running; call switch_target.")
        if not targets:
            raise GatewayError(503, "No Revit with the Plinth add-in is running.")
        if len(targets) > 1:
            raise GatewayError(409, "More than one Revit is running; choose one with switch_target.")
        return targets[0]

    def current_target(self) -> dict[str, Any]:
        return {"pinned_pid": self.pinned_pid, "confirmed_document": self.document_title,
                "document_confirmed": self.document_id is not None}

    def switch_target(self, target: str = "auto", verify: bool = True) -> dict[str, Any]:
        """target: "auto", "pid:<n>" or a Revit year such as "2026". verify confirms the active document for writes."""
        targets = find_targets()
        if target == "auto":
            chosen = targets[0] if len(targets) == 1 else None
            if chosen is None:
                raise GatewayError(409, f"{len(targets)} Revit processes are running; name one as pid:<n> or a year.")
        elif target.startswith("pid:"):
            chosen = next((t for t in targets if str(t.pid) == target[4:]), None)
        else:
            chosen = next((t for t in targets if str(t.revit_year) == target), None)
        if chosen is None:
            raise GatewayError(404, f"No running Revit matches {target!r}.")
        self.pinned_pid, self.document_id, self.document_title = chosen.pid, None, None
        result: dict[str, Any] = {"target_id": chosen.target_id, "pid": chosen.pid, "revit_year": chosen.revit_year}
        if verify:
            with self._session(chosen) as revit:
                self.document_title = revit.confirm_document()
                self.document_id = revit.document_id
            result["confirmed_document"] = self.document_title
        return result

    def _session(self, target=None) -> RevitClient:
        client = self._connect(target or self._target())
        client.document_id = self.document_id
        return client

    # ---- direct catalog calls
    def commands(self, refresh: bool = False) -> list[dict[str, Any]]:
        if self.commands_cache is None or refresh:
            self.commands_cache = self.call("list_commands", {}, log=False)["data"]["commands"]
        return self.commands_cache

    def call(self, command: str, params: dict[str, Any] | None, timeout_seconds: int | None = None, log: bool = True) -> dict[str, Any]:
        """Run one add-in command. Never raises for an add-in error: the response says success false, with a hint."""
        if not self._lock.acquire(blocking=False):
            raise GatewayError(409, "A build or removal is running; try again when it finishes.")
        call_id = secrets.token_hex(8)
        started = time.monotonic()
        try:
            try:
                with self._session() as revit:
                    response = revit.request(command, params or {}, timeout_seconds=timeout_seconds)
            except RevitError as e:
                response = e.response
            response = {**response, "call_id": call_id}
            if not response.get("success"):
                response["hint"] = hint(response.get("error"))
            return response
        finally:
            self._lock.release()
            if log:
                self._log_call(call_id, command, params, locals().get("response"), time.monotonic() - started)

    def _log_call(self, call_id: str, command: str, params: dict | None, response: dict | None, seconds: float) -> None:
        text = json.dumps(params or {}, ensure_ascii=False)
        target = (response or {}).get("_target") or {}
        _append_jsonl(self.calls_log, {
            "call_id": call_id, "at": _now(), "command": command,
            "params": text if len(text) <= MAX_LOGGED_PARAMS else text[:MAX_LOGGED_PARAMS] + "…",
            "success": bool(response and response.get("success")), "error": (response or {}).get("error"),
            "pending": bool(response and response.get("pending")), "failures": (response or {}).get("failures"),
            "ms": round(seconds * 1000), "document_title": target.get("document_title"), "pid": target.get("pid")})

    # ---- operation status, usage, failures, messages
    def operation_status(self, call_id: str) -> dict[str, Any]:
        for row in reversed(_read_jsonl(self.calls_log)):
            if row.get("call_id") == call_id:
                state = "still_running" if row.get("pending") else ("succeeded" if row.get("success") else "failed")
                return {**row, "state": state}
        raise GatewayError(404, f"No logged call {call_id}.")

    def usage_patterns(self, last: int = 1000) -> dict[str, Any]:
        rows = _read_jsonl(self.calls_log)[-last:]
        by_command: dict[str, list[dict]] = defaultdict(list)
        for row in rows:
            by_command[row["command"]].append(row)
        commands = sorted(({"command": c, "calls": len(r), "failed": sum(not x["success"] for x in r),
                            "avg_ms": round(sum(x.get("ms", 0) for x in r) / len(r))} for c, r in by_command.items()),
                          key=lambda x: -x["calls"])
        errors = Counter((row.get("error") or "").split(":", 1)[0] for row in rows if not row["success"])
        return {"calls": len(rows), "commands": commands, "error_codes": [{"code": c, "count": n} for c, n in errors.most_common() if c]}

    def failure_records(self, last: int = 100) -> list[dict[str, Any]]:
        rows = [r for r in _read_jsonl(self.calls_log) if r.get("failures") or not r.get("success")]
        return rows[-last:]

    @staticmethod
    def lookup_message(text: str) -> dict[str, Any]:
        """Explain an add-in error code or message. Revit's own dialog catalogue is not bundled."""
        code = text.split(":", 1)[0].strip().upper()
        if code in HINTS:
            return {"code": code, "explanation": HINTS[code]}
        matches = [{"code": c, "explanation": h} for c, h in HINTS.items() if text.lower() in h.lower() or c in text.upper()]
        return {"code": None, "matches": matches, "note": "Known Plinth error codes only; Revit's dialog catalogue is not bundled."}

    # ---- change history
    def record_change(self, call_ids: list[str], reason: str) -> dict[str, Any]:
        logged = {r["call_id"]: r for r in _read_jsonl(self.calls_log)}
        unknown = [c for c in call_ids if c not in logged]
        if unknown:
            raise GatewayError(404, f"Unknown call ids: {', '.join(unknown)}. Nothing was recorded.")
        taken = {r["call_id"] for r in _read_jsonl(self.changes_log)}
        already = [c for c in call_ids if c in taken]
        if already:
            raise GatewayError(409, f"Already recorded: {', '.join(already)}. Nothing was recorded.")
        for c in call_ids:
            _append_jsonl(self.changes_log, {"call_id": c, "at": _now(), "reason": reason.strip() or "unknown",
                                             "command": logged[c]["command"], "document_title": logged[c].get("document_title")})
        return {"recorded": len(call_ids)}

    def change_records(self, document_title: str | None = None, since: str | None = None) -> list[dict[str, Any]]:
        aliases = self._aliases()
        titles = {document_title} | {old for old, new in aliases.items() if new == document_title} if document_title else None
        return [r for r in _read_jsonl(self.changes_log)
                if (titles is None or r.get("document_title") in titles) and (since is None or r["at"] >= since)]

    def _aliases(self) -> dict[str, str]:
        try:
            return json.loads(self.aliases_file.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            return {}

    def resolve_history_identity(self, old_title: str, new_title: str) -> dict[str, Any]:
        """After Save As or a copy, say that new_title continues old_title's history."""
        aliases = self._aliases()
        aliases[old_title] = new_title
        self.aliases_file.parent.mkdir(parents=True, exist_ok=True)
        self.aliases_file.write_text(json.dumps(aliases, indent=2), encoding="utf-8")
        return {"old_title": old_title, "new_title": new_title}

    # ---- code modules (developer mode)
    @property
    def modules_dir(self) -> Path:
        return paths.root() / "code-modules"

    def list_code_modules(self) -> list[dict[str, Any]]:
        if not self.modules_dir.is_dir():
            return []
        return [{"name": p.stem, "bytes": p.stat().st_size} for p in sorted(self.modules_dir.glob("*.cs"))]

    def save_code_module(self, name: str, code: str, overwrite: bool = False) -> dict[str, Any]:
        self._require_developer_mode()
        path = self._module_path(name)
        if path.exists() and not overwrite:
            raise GatewayError(409, f"Module {name} exists; pass overwrite to replace it.")
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(code, encoding="utf-8")
        return {"name": name, "bytes": len(code.encode("utf-8"))}

    def delete_code_module(self, name: str) -> dict[str, Any]:
        self._require_developer_mode()
        path = self._module_path(name)
        if not path.exists():
            raise GatewayError(404, f"No module {name}.")
        path.unlink()
        return {"deleted": name}

    def _module_path(self, name: str) -> Path:
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{0,63}", name):
            raise GatewayError(422, "Module names are letters, digits and _ (start with a letter).")
        return self.modules_dir / f"{name}.cs"

    @staticmethod
    def _require_developer_mode() -> None:
        if not developer_mode():
            raise GatewayError(403, "DEVELOPER_MODE_OFF: a BIM manager turns it on in %LOCALAPPDATA%\\Plinth\\addin-settings.json.")

    # ---- baked-tool suggestions (developer mode to accept)
    @property
    def baked_dir(self) -> Path:
        return paths.root() / "baked-tools"

    @property
    def dismissed_file(self) -> Path:
        return paths.root() / "baked-tools" / ".dismissed.json"

    def bake_suggestions(self) -> list[dict[str, Any]]:
        """Code sent with send_code_to_revit, successfully, at least SUGGEST_AFTER times, not yet baked or dismissed."""
        dismissed = set(json.loads(self.dismissed_file.read_text(encoding="utf-8"))) if self.dismissed_file.exists() else set()
        counts: Counter[str] = Counter()
        sample: dict[str, str] = {}
        for row in _read_jsonl(self.calls_log):
            if row["command"] != "send_code_to_revit" or not row["success"]:
                continue
            try:
                code = json.loads(row["params"]).get("code", "")
            except ValueError:
                continue
            key = hashlib.sha256(code.encode("utf-8")).hexdigest()[:12]
            counts[key] += 1
            sample[key] = code
        return [{"id": k, "uses": n, "code": sample[k]} for k, n in counts.most_common()
                if n >= SUGGEST_AFTER and k not in dismissed and not (self.baked_dir / f"suggested-{k}.json").exists()]

    def accept_bake_suggestion(self, suggestion_id: str, name: str, description: str) -> dict[str, Any]:
        self._require_developer_mode()
        match = next((s for s in self.bake_suggestions() if s["id"] == suggestion_id), None)
        if match is None:
            raise GatewayError(404, f"No open suggestion {suggestion_id}.")
        self.baked_dir.mkdir(parents=True, exist_ok=True)
        tool = {"name": name, "description": description, "parameters_schema": {"type": "object", "properties": {}},
                "code": match["code"], "source": f"suggestion {suggestion_id}", "created_utc": _now()}
        (self.baked_dir / f"{name}.json").write_text(json.dumps(tool, indent=2), encoding="utf-8")
        (self.baked_dir / f"suggested-{suggestion_id}.json").write_text(json.dumps({"baked_as": name}), encoding="utf-8")
        return {"baked": name}

    def dismiss_bake_suggestion(self, suggestion_id: str) -> dict[str, Any]:
        dismissed = set(json.loads(self.dismissed_file.read_text(encoding="utf-8"))) if self.dismissed_file.exists() else set()
        dismissed.add(suggestion_id)
        self.dismissed_file.parent.mkdir(parents=True, exist_ok=True)
        self.dismissed_file.write_text(json.dumps(sorted(dismissed)), encoding="utf-8")
        return {"dismissed": suggestion_id}
