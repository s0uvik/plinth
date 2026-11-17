import json
import threading

import pytest

from plinth.gateway import Gateway, GatewayError
from plinth.revit import RevitError


class FakeClient:
    def __init__(self, responses):
        self.responses = responses
        self.document_id = None

    def __enter__(self):
        return self

    def __exit__(self, *a):
        pass

    def confirm_document(self, expected_title=None):
        self.document_id = "doc-1"
        return "Scratch"

    def request(self, command, params=None, timeout_seconds=None):
        response = self.responses.pop(0)
        if not response.get("success"):
            raise RevitError(response)
        return response


@pytest.fixture
def gateway(tmp_path, monkeypatch):
    monkeypatch.setenv("PLINTH_HOME", str(tmp_path))
    responses = []
    g = Gateway(threading.Lock(), connect=lambda target=None: FakeClient(responses))
    g._target = lambda: "fake-target"
    g.responses = responses
    return g


def ok(data=None, **extra):
    return {"success": True, "data": data, "_target": {"document_title": "Scratch", "pid": 7}, **extra}


def test_calls_are_logged_with_ids_and_errors_carry_hints(gateway):
    gateway.responses += [ok({"x": 1}), {"success": False, "error": "DOCUMENT_UNCONFIRMED: confirm first"}]
    first = gateway.call("list_levels", {})
    second = gateway.call("create_level", {"elevation": 0})
    assert first["success"] and first["call_id"]
    assert not second["success"] and "Confirm" in second["hint"]
    assert gateway.operation_status(first["call_id"])["state"] == "succeeded"
    assert gateway.operation_status(second["call_id"])["state"] == "failed"
    usage = gateway.usage_patterns()
    assert usage["calls"] == 2 and usage["error_codes"] == [{"code": "DOCUMENT_UNCONFIRMED", "count": 1}]
    assert [r["command"] for r in gateway.failure_records()] == ["create_level"]
    with pytest.raises(GatewayError):
        gateway.operation_status("nope")


def test_calls_wait_for_nobody_while_a_build_holds_the_lock(gateway):
    gateway._lock.acquire()
    with pytest.raises(GatewayError) as e:
        gateway.call("list_levels", {})
    assert e.value.status == 409


def test_change_history_follows_save_as(gateway):
    gateway.responses += [ok(), ok()]
    a = gateway.call("create_level", {"elevation": 0})["call_id"]
    b = gateway.call("create_level", {"elevation": 3000})["call_id"]
    assert gateway.record_change([a, b], "Client asked for an extra floor") == {"recorded": 2}
    with pytest.raises(GatewayError):
        gateway.record_change([a], "again")
    with pytest.raises(GatewayError):
        gateway.record_change(["unknown"], "x")
    gateway.resolve_history_identity("Scratch", "Scratch v2")
    assert len(gateway.change_records("Scratch v2")) == 2


def test_code_modules_need_developer_mode(gateway, tmp_path):
    with pytest.raises(GatewayError) as e:
        gateway.save_code_module("Helpers", "class X {}")
    assert e.value.status == 403
    (tmp_path / "addin-settings.json").write_text('{"developer_mode": true}', encoding="utf-8")
    gateway.save_code_module("Helpers", "class X {}")
    assert gateway.list_code_modules() == [{"name": "Helpers", "bytes": 10}]
    with pytest.raises(GatewayError):
        gateway.save_code_module("../evil", "x")
    gateway.delete_code_module("Helpers")
    assert gateway.list_code_modules() == []


def test_repeated_send_code_is_suggested_for_baking(gateway, tmp_path):
    code = {"code": "doc.Title"}
    gateway.responses += [ok(), ok(), ok()]
    for _ in range(3):
        gateway.call("send_code_to_revit", code)
    suggestions = gateway.bake_suggestions()
    assert len(suggestions) == 1 and suggestions[0]["uses"] == 3
    (tmp_path / "addin-settings.json").write_text('{"developer_mode": true}', encoding="utf-8")
    gateway.accept_bake_suggestion(suggestions[0]["id"], "doc_title", "Return the document title")
    baked = json.loads((tmp_path / "baked-tools" / "doc_title.json").read_text(encoding="utf-8"))
    assert baked["code"] == "doc.Title" and gateway.bake_suggestions() == []


def test_switch_target_confirms_the_document(gateway, monkeypatch):
    from plinth.revit.discovery import Target
    from pathlib import Path
    t = Target(Path("x"), 42, 2026, "0.1.0", "pipe", "Plinth.Revit.42", None, "tok")
    monkeypatch.setattr("plinth.gateway.find_targets", lambda: [t])
    result = gateway.switch_target("2026")
    assert result["confirmed_document"] == "Scratch" and gateway.document_id == "doc-1" and gateway.pinned_pid == 42
    with pytest.raises(GatewayError):
        gateway.switch_target("pid:1")


def test_lookup_explains_known_codes(gateway):
    assert "Revit was busy" in gateway.lookup_message("REVIT_REQUEST_NOT_STARTED: x")["explanation"]
