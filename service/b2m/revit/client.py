"""Client for the B2M.Revit add-in (PRD F6.1 and F6.3).

At startup it checks the add-in's protocol version and command list. Before any write it confirms
the target document: a read command returns ``_target.document_id`` and every later write carries it,
so the add-in refuses to write if the user switched documents or Revit restarted.
"""

from __future__ import annotations

import itertools
import json
from typing import Any, Callable

from b2m.revit.connection import Connection, open_connection
from b2m.revit.discovery import Target, find_targets

PROTOCOL_VERSION = 1
CONFIRM_COMMAND = "get_current_view_info"


class RevitError(Exception):
    """The add-in answered with success=false. ``code`` is the part of the error before the colon."""

    def __init__(self, response: dict[str, Any]):
        self.response = response
        self.error = str(response.get("error") or "UNKNOWN: no error text")
        self.code = self.error.split(":", 1)[0]
        self.data = response.get("data")
        super().__init__(self.error)


class RevitTimeout(RevitError):
    """The request is still running in Revit. Do not retry it; its result arrives later."""


class RevitClient:
    def __init__(self, target: Target, connection: Connection | None = None):
        self.target = target
        # Called with (request without token, final response) for every call, e.g. to keep tool results.
        self.observer: Callable[[dict[str, Any], dict[str, Any]], None] | None = None
        self._connection = connection or open_connection(target)
        self._ids = itertools.count(1)
        self.health: dict[str, Any] = {}
        self.document_id: str | None = None
        self.document_title: str | None = None

    @classmethod
    def connect(cls, target: Target | None = None, required_commands: list[str] | None = None) -> "RevitClient":
        """Connect to a target (the only running Revit if not given) and check the add-in."""
        if target is None:
            targets = find_targets()
            if not targets:
                raise ConnectionError("No Revit with the B2M add-in is running.")
            if len(targets) > 1:
                ids = ", ".join(t.target_id for t in targets)
                raise ConnectionError(f"More than one Revit is running ({ids}); close all but one during a build.")
            target = targets[0]
        client = cls(target)
        try:
            client.check(required_commands or [])
        except Exception:
            client.close()
            raise
        return client

    def close(self) -> None:
        self._connection.close()

    def __enter__(self) -> "RevitClient":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def check(self, required_commands: list[str]) -> dict[str, Any]:
        """Read health and fail if the add-in speaks another protocol or lacks a command we need."""
        self.health = self.call("health")
        version = self.health.get("protocol_version")
        if version != PROTOCOL_VERSION:
            raise ConnectionError(f"Add-in protocol {version} is not supported; this service speaks {PROTOCOL_VERSION}.")
        available = {c["name"] for c in self.health.get("commands", [])}
        missing = sorted(set(required_commands) - available)
        if missing:
            raise ConnectionError(f"Add-in {self.health.get('addin_version')} lacks commands: {', '.join(missing)}.")
        return self.health

    def confirm_document(self, expected_title: str | None = None) -> str:
        """Pin the active document for writes. Returns its title."""
        response = self.request(CONFIRM_COMMAND)
        target = response.get("_target") or {}
        document_id = target.get("document_id")
        if not document_id:
            raise ConnectionError("Revit has no open document to confirm.")
        title = target.get("document_title")
        if expected_title is not None and title != expected_title:
            raise ConnectionError(f"Active document is '{title}', expected '{expected_title}'.")
        self.document_id, self.document_title = document_id, title
        return title

    def call(self, command: str, params: dict[str, Any] | None = None, *, timeout_seconds: int | None = None) -> Any:
        """Run a command and return its data, raising RevitError on failure."""
        return self.request(command, params, timeout_seconds=timeout_seconds).get("data")

    def request(self, command: str, params: dict[str, Any] | None = None, *, timeout_seconds: int | None = None) -> dict[str, Any]:
        """Run a command and return the whole response, raising RevitError on failure."""
        request_id = f"{self.target.pid}-{next(self._ids)}"
        message: dict[str, Any] = {"id": request_id, "token": self.target.token, "command": command}
        if params:
            message["params"] = params
        if self.document_id:
            message["expected_document_id"] = self.document_id
        if timeout_seconds:
            message["timeout_seconds"] = timeout_seconds
        self._connection.send_line(json.dumps(message, separators=(",", ":")))

        while True:
            line = self._connection.read_line()
            if line is None:
                raise ConnectionError("The add-in closed the connection.")
            response = json.loads(line)
            if response.get("id") is None and not response.get("success"):
                self._observe(message, response)
                raise RevitError(response)  # Connection-level error, e.g. UNAUTHORIZED.
            if response.get("id") != request_id:
                continue  # A late result of an earlier timed-out request.
            self._observe(message, response)
            if response.get("pending"):
                raise RevitTimeout(response)
            if not response.get("success"):
                raise RevitError(response)
            return response

    def _observe(self, message: dict[str, Any], response: dict[str, Any]) -> None:
        if self.observer is not None:
            self.observer({k: v for k, v in message.items() if k != "token"}, response)
