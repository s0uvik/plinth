"""The import review API (T8) with the real T5 engine behind its seams, on the G+4 sample."""

from fastapi.testclient import TestClient

from b2m.api import create_app
from b2m.cad.project import ImportStore
from tests.cad.conftest import SAMPLE_DXF
from tests.test_service import FakeClient, make_service


def test_review_api_on_the_sample(tmp_path):
    service = make_service(tmp_path / "runs", FakeClient())
    app = create_app(service, import_store=ImportStore(tmp_path / "imports"))
    api = TestClient(app)
    started = api.post("/imports", json={"folder": str(SAMPLE_DXF)})
    assert started.status_code == 202
    import_id = started.json()["import_id"]
    with app.state.imports.lock:  # the import thread holds it until done
        pass
    status = api.get(f"/imports/{import_id}").json()
    assert status["status"] == "ready", status
    folder = tmp_path / "imports" / import_id
    assert (folder / "recognised_building.json").is_file() and (folder / "project-standards.json").is_file()

    review = api.get(f"/imports/{import_id}/questions").json()
    assert [g["severity"] for g in review["groups"]] == ["blocking", "check", "info"]
    blocking = {q["id"] for q in review["groups"][0]["questions"]}
    assert {"location", "mark-width-d1", "door-height-d1"} <= blocking
    stairs = next(q for q in review["groups"][0]["questions"] if q["topic"] == "stair_axis")  # both stairs, once
    assert stairs["key"]["members"] == ["stair_axis:1.GROUND FLOOR PLAN.dxf:stair-1", "stair_axis:2.PLAN 1st to 4th.dxf:stair-1"]
    assert stairs["options"] == ["x (east-west)", "y (north-south)"]
    assert review["readiness"]["ready"] is False

    refused = api.post(f"/imports/{import_id}/assume", json={"question_ids": "all"})
    assert refused.status_code == 409

    located = api.post(f"/imports/{import_id}/location",
                       json={"city": "Bardhaman", "state": "West Bengal", "country": "India"}).json()
    location = next(q for g in located["groups"] for q in g["questions"] if q["id"] == "location")
    assert location["answered_by"] == "user"
    rule_set = next(q for g in located["groups"] for q in g["questions"] if q["id"] == "rule-set")
    assert "West Bengal Municipal (Building) Rules, 2007" in rule_set["text"]

    assumed = api.post(f"/imports/{import_id}/assume", json={"question_ids": "all"}).json()
    assert assumed["assumptions"] and all(a["basis"] for a in assumed["assumptions"])
    assert any("office practice, confirm" in a["text"] for a in assumed["assumptions"])

    answered = api.post(f"/imports/{import_id}/answers", json={"question_id": "mark-width-d1", "answer": "800 mm"})
    assert answered.status_code == 200
    bad = api.post(f"/imports/{import_id}/answers", json={"question_id": "tie-beam-depth", "answer": "deep"})
    assert bad.status_code == 422
