import json
from dataclasses import replace
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from b2m.compliance import RoomFact, evaluate, facts_after_build, facts_before_build, hard_failures, kind_of
from b2m.compliance.pdf import write_pdf
from b2m.engine import generate
from b2m.spec import BuildingSpec

SPECS = sorted((Path(__file__).parent / "golden" / "specs").glob("*.json"))


def load(path):
    return BuildingSpec.model_validate(json.loads(path.read_text(encoding="utf-8")))


@pytest.mark.parametrize("path", SPECS, ids=[p.stem for p in SPECS])
def test_engine_layouts_pass_every_nbc_check_they_design_to(path):
    spec = load(path)
    report = evaluate(facts_before_build(spec, generate(spec).report))
    nbc_fails = [f for f in report["findings"] if f["rule_set"] == "NBC 2016" and f["status"] == "fail"
                 and f["rule_id"] != "NBC-HEIGHT-CLASS"]
    assert nbc_fails == []
    assert report["summary"]["not_checked"] == 0
    assert report["summary"]["provisional"] == len(report["findings"])  # nothing is verified yet


def test_failures_name_the_room_value_and_limit():
    spec = load(SPECS[0])
    facts = facts_before_build(spec, generate(spec).report)
    facts.rooms.append(RoomFact("Flat Z Bedroom 9", "bedroom", 2000, 6.0, 0.4, 6.0))
    facts = replace(facts, stair={**facts.stair, "riser_mm": 200}, setbacks_mm={**facts.setbacks_mm, "front": 500})
    report = evaluate(facts)
    fails = hard_failures(report)
    assert any("Flat Z Bedroom 9: 2.00 m wide, 6.00 m²" in f for f in fails)
    assert any(f.startswith("NBC-VENT-HABITABLE: Flat Z Bedroom 9") for f in fails)
    assert any(f.startswith("NBC-STAIR-RISER") for f in fails)
    assert any("Open space, front: 0.50 m" in f for f in fails)
    assert report["findings"][0]["status"] == "fail"  # failures sort first


def test_room_kinds_from_engine_names():
    assert [kind_of(n) for n in ("Living/Dining", "Bedroom 1", "Bedroom 3", "Kitchen", "Toilet 2", "Stair & Lobby")] == \
        ["living", "bedroom_master", "bedroom", "kitchen", "toilet", None]


class FakeRevitRooms:
    def call(self, command, params=None, **_):
        if command == "list_rooms":
            return {"rooms": [
                {"element_id": 1, "name": "Bedroom 2", "department": "F1-A", "area_m2": 10.2, "level": {"name": "Floor 1"}},
                {"element_id": 2, "name": "Toilet 1", "department": "F1-A", "area_m2": 2.0, "level": {"name": "Floor 1"}},
                {"element_id": 3, "name": "Someone else's room", "area_m2": 1.0, "level": {"name": "Level 1"}}]}
        if command == "get_room_boundaries":
            w, h = {1: (2500, 4100), 2: (1000, 2000)}[params["room_id"]]
            pts = [(0, 0), (w, 0), (w, h), (0, h)]
            return {"loops": [{"segments": [{"start": {"x_mm": x, "y_mm": y}} for x, y in pts]}]}
        raise AssertionError(command)


def test_after_build_checks_read_rooms_back_from_revit_and_skip_windows():
    spec = load(SPECS[0])
    facts = facts_after_build(spec, generate(spec).report, FakeRevitRooms(), [1, 2])
    assert [r.kind for r in facts.rooms] == ["bedroom", "toilet"]
    report = evaluate(facts)
    toilet = [f for f in report["findings"] if f["subject"].startswith("F1-A Toilet 1")]
    assert toilet[0]["status"] == "fail" and "1.00 m wide, 2.00 m²" in toilet[0]["measured"]
    assert any(f["status"] == "not_checked" and f["rule_id"] == "NBC-VENT-HABITABLE" for f in report["findings"])


def test_pdf_is_written(tmp_path):
    spec = load(SPECS[0])
    path = write_pdf(evaluate(facts_before_build(spec, generate(spec).report)), tmp_path / "r.pdf")
    assert path.read_bytes()[:5] == b"%PDF-" and path.stat().st_size > 2000


def test_api_blocks_hard_failures_unless_overridden(tmp_path, monkeypatch):
    from b2m.api import create_app
    from tests.test_service import FakeClient, make_service
    monkeypatch.setenv("B2M_HOME", str(tmp_path))
    spec = json.loads((SPECS[13]).read_text(encoding="utf-8"))  # 14: fails placeholder coverage and FAR
    service = make_service(tmp_path, FakeClient())
    api = TestClient(create_app(service, brief_client=lambda: None))
    checked = api.post("/check", json={"spec": spec}).json()
    assert checked["summary"]["hard_fail"] >= 1
    blocked = api.post("/builds/from-spec", json={"spec": spec, "document_title": "Scratch"})
    assert blocked.status_code == 409 and any("KMC-" in e for e in blocked.json()["detail"]["errors"])
    started = api.post("/builds/from-spec", json={"spec": spec, "document_title": "Scratch", "override": True})
    assert started.status_code == 202
    run_id = started.json()["run_id"]
    with service._lock:
        pass
    run = service.store.get(run_id)
    assert run.load("override")["overridden"] and run.load("check_before")["summary"]["hard_fail"] >= 1
    assert api.get(f"/runs/{run_id}/report_before.pdf").content[:5] == b"%PDF-"
    assert api.get(f"/runs/{run_id}/nope.pdf").status_code == 404
