import json
import os

from plinth.revit.discovery import find_targets, parse

VALID = {
    "schema_version": 1,
    "product": "plinth-revit",
    "addin_version": "0.1.0",
    "revit_year": 2026,
    "pid": 1234,
    "transport": "pipe",
    "pipe_name": "Plinth.Revit.1234",
    "port": None,
    "auth_token": "secret",
}


def test_parse_valid_pipe_file(tmp_path):
    target = parse(tmp_path / "f.json", json.dumps(VALID))
    assert target.pipe_name == "Plinth.Revit.1234"
    assert target.token == "secret"
    assert target.target_id == "revit-2026-1234"


def test_parse_rejects_unusable_files(tmp_path):
    p = tmp_path / "f.json"
    assert parse(p, "not json") is None
    assert parse(p, json.dumps({**VALID, "schema_version": 2})) is None
    assert parse(p, json.dumps({**VALID, "product": "rvt-mcp"})) is None
    assert parse(p, json.dumps({**VALID, "auth_token": ""})) is None
    assert parse(p, json.dumps({**VALID, "pipe_name": None})) is None
    assert parse(p, json.dumps({**VALID, "transport": "tcp"})) is None  # tcp needs a port
    assert parse(p, json.dumps({**VALID, "transport": "tcp", "port": 50000})).port == 50000


def test_find_targets_keeps_live_and_deletes_dead(tmp_path):
    live = tmp_path / f"revit-2026-{os.getpid()}.json"
    live.write_text(json.dumps({**VALID, "pid": os.getpid()}), encoding="utf-8")
    dead = tmp_path / "revit-2025-999999.json"
    dead.write_text(json.dumps({**VALID, "pid": 999999, "revit_year": 2025}), encoding="utf-8")

    targets = find_targets(tmp_path)

    assert [t.pid for t in targets] == [os.getpid()]
    assert not dead.exists()


def test_find_targets_on_missing_directory(tmp_path):
    assert find_targets(tmp_path / "nope") == []
