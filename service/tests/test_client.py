import json
from pathlib import Path

import pytest

from b2m.revit.client import RevitClient, RevitError, RevitTimeout
from b2m.revit.discovery import Target

TARGET = Target(Path("x.json"), 42, 2026, "0.1.0", "pipe", "B2M.Revit.42", None, "tok")

HEALTH = {"protocol_version": 1, "addin_version": "0.1.0", "commands": [{"name": "create_level", "read_only": False}]}


class FakeConnection:
    """Answers each request with the next scripted response; '{id}' is replaced with the request id."""

    def __init__(self, *responses):
        self.sent: list[dict] = []
        self._responses = list(responses)
        self._out: list[str] = []

    def send_line(self, line):
        request = json.loads(line)
        self.sent.append(request)
        for r in self._responses.pop(0):
            self._out.append(json.dumps(r).replace('"{id}"', json.dumps(request["id"])))

    def read_line(self):
        return self._out.pop(0) if self._out else None

    def close(self):
        pass


def ok(data=None, **extra):
    return {"id": "{id}", "success": True, "data": data, **extra}


def test_check_accepts_matching_add_in():
    client = RevitClient(TARGET, FakeConnection([ok(HEALTH)]))
    assert client.check(["create_level"])["addin_version"] == "0.1.0"


def test_check_rejects_other_protocol_and_missing_commands():
    with pytest.raises(ConnectionError, match="protocol 2"):
        RevitClient(TARGET, FakeConnection([ok({**HEALTH, "protocol_version": 2})])).check([])
    with pytest.raises(ConnectionError, match="lacks commands: create_room"):
        RevitClient(TARGET, FakeConnection([ok(HEALTH)])).check(["create_level", "create_room"])


def test_writes_carry_the_confirmed_document():
    conn = FakeConnection(
        [ok({"viewName": "L1"}, _target={"document_id": "doc-1", "document_title": "Scratch"})],
        [ok({"elementId": 7})],
    )
    client = RevitClient(TARGET, conn)
    assert client.confirm_document("Scratch") == "Scratch"
    assert client.call("create_level", {"elevation": 3000}) == {"elementId": 7}
    assert conn.sent[1]["expected_document_id"] == "doc-1"
    assert conn.sent[1]["token"] == "tok"
    assert conn.sent[1]["params"] == {"elevation": 3000}


def test_confirm_rejects_unexpected_document():
    conn = FakeConnection([ok({}, _target={"document_id": "doc-1", "document_title": "Live Project"})])
    with pytest.raises(ConnectionError, match="Live Project"):
        RevitClient(TARGET, conn).confirm_document("Scratch")


def test_errors_carry_code_and_data():
    conn = FakeConnection([{"id": "{id}", "success": False, "error": "LEVEL_NOT_FOUND: no level", "data": {"valid_levels": ["L1"]}}])
    with pytest.raises(RevitError) as info:
        RevitClient(TARGET, conn).call("create_line_based_element", {})
    assert info.value.code == "LEVEL_NOT_FOUND"
    assert info.value.data == {"valid_levels": ["L1"]}


def test_late_results_of_other_requests_are_skipped_and_pending_raises_timeout():
    conn = FakeConnection([{"id": "old-1", "success": True}, {"id": "{id}", "success": False, "pending": True, "error": "REVIT_OPERATION_TIMED_OUT: still running"}])
    with pytest.raises(RevitTimeout):
        RevitClient(TARGET, conn).call("batch_execute", {"commands": []})


def test_connection_level_error_without_id_raises():
    conn = FakeConnection([{"id": None, "success": False, "error": "UNAUTHORIZED: wrong token"}])
    with pytest.raises(RevitError, match="UNAUTHORIZED"):
        RevitClient(TARGET, conn).call("health")


def test_closed_connection_raises():
    conn = FakeConnection([])
    with pytest.raises(ConnectionError):
        RevitClient(TARGET, conn).call("health")
