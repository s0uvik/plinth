"""Drawing import review API (PRD Step 9 T8): the flow with the engine seams faked (b2m.imports_api), and the overlay
of a real sample plan sheet recognised by the real plan recogniser."""

import pytest
from fastapi.testclient import TestClient

from b2m import imports_api
from b2m.api import create_app
from b2m.cad import classify, reader, sheet
from b2m.cad.building import Evidence as BEvidence, Finding, PlanRecognition, RecognisedBuilding, Registration, Wall
from b2m.cad.project import Evidence, ImportStore
from b2m.cad.recognise import recognise_plan
from b2m.examples import two_storey_house
from tests.cad.conftest import SAMPLE_DXF
from tests.cad.test_project import _sheet
from tests.test_service import FakeClient, make_service

SEVERITY = {"storey_height": "blocking"}


class FakeEngine:
    """Stands in for T5 (pipeline, questions, answers, assumptions, readiness) and T6 (to_plan)."""

    def __init__(self):
        self.plan_calls = 0

    def run_import(self, project):
        file = project.sheets[0].file
        project.add_question("storey_height", "Floor to floor height is not dimensioned. What is it?",
                             [Evidence(sheet=file, handles=[project.sheets[0].title_handle])], options=["2700", "3000"])
        project.add_question("opening_without_mark", "An opening has no mark. Door or window?",
                             [Evidence(sheet=file, handles=[])], options=["door", "window"])
        ev = BEvidence(sheet=file, handles=["AA"])
        return RecognisedBuilding(
            plans=[PlanRecognition(sheet=file, role="floor_plan", storeys=[0],
                                   walls=[Wall(id="wall-1", start=(0, 0), end=(4000, 0), thickness_mm=250,
                                               kind="exterior", evidence=ev)],
                                   findings=[Finding(id="f-1", topic="unclaimed", text="Lines nobody used",
                                                     evidence=ev)])],
            registrations=[Registration(sheet=file, method="identity", residual_mm=0, evidence=ev)])

    def question_groups(self, project):
        groups = {}
        for q in project.questions:
            groups.setdefault(SEVERITY.get(q.topic, "check"), []).append(q)
        return groups

    def apply_answer(self, project, question_id, answer):
        return project.answer(question_id, answer)

    def assume(self, project, ids):
        city = project.inputs.location.city if project.inputs.location else None
        made = []
        for q in project.open_questions():
            if ids is None or q.id in ids:
                project.answer(q.id, q.options[0], by="assumed", basis=f"{city} building rules, typical practice")
                made.append(project.assumptions[-1])
        return made

    def ready_to_build(self, project, building):
        blocking = [q.text for q in project.open_questions() if SEVERITY.get(q.topic) == "blocking"]
        return {"ready": not blocking, "blocking": blocking}

    def to_plan(self, project, building):
        self.plan_calls += 1
        if self.plan_calls == 1:
            return [{"text": "Door heights are not given anywhere."}]
        return two_storey_house("Door 900", "Window 1200")


@pytest.fixture
def engine(monkeypatch):
    fake = FakeEngine()
    for name in ("run_import", "question_groups", "apply_answer", "assume", "ready_to_build", "to_plan"):
        monkeypatch.setattr(imports_api, name, getattr(fake, name))
    return fake


def _app(tmp_path):
    service = make_service(tmp_path / "runs", FakeClient())
    app = create_app(service, import_store=ImportStore(tmp_path / "imports"))
    return service, app, TestClient(app)


def _wait(app):
    with app.state.imports.lock:
        pass


def _question(review, topic):
    return next(q for g in review["groups"] for q in g["questions"] if q["topic"] == topic)


def test_import_review_flow(tmp_path, engine):
    drawings = tmp_path / "drawings"
    drawings.mkdir()
    _sheet(drawings / "A-1.dxf", "%%UGROUND FLOOR PLAN")
    service, app, api = _app(tmp_path)

    assert api.post("/imports", json={}).status_code == 422
    assert api.post("/imports", json={"folder": str(tmp_path / "nope")}).status_code == 404
    started = api.post("/imports", json={"folder": str(drawings)})
    assert started.status_code == 202
    import_id = started.json()["import_id"]
    _wait(app)

    status = api.get(f"/imports/{import_id}").json()
    assert status["status"] == "ready", status
    (s,) = status["sheets"]
    assert (s["n"], s["file"], s["role"], s["storeys"], s["scale"]) == (1, "A-1.dxf", "floor_plan", [0], 50)
    assert s["recognised"]["walls"] == 1 and s["recognised"]["findings"] == 1
    assert status["registrations"][0]["sheet"] == "A-1.dxf"
    assert status["readiness"]["ready"] is False and status["approved"] is False
    assert api.get("/imports").json()[0]["import_id"] == import_id
    assert api.get("/imports/nope").status_code == 404

    review = api.get(f"/imports/{import_id}/questions").json()
    assert [g["severity"] for g in review["groups"]] == ["blocking", "check"]  # blocking first
    height = _question(review, "storey_height")
    assert height["evidence"][0]["sheet"] == "A-1.dxf" and height["evidence"][0]["handles"]
    assert height["severity"] == "blocking" and height["answered_by"] is None
    mark = _question(review, "opening_without_mark")

    answered = api.post(f"/imports/{import_id}/answers", json={"question_id": mark["id"], "answer": "window"}).json()
    assert _question(answered, "opening_without_mark")["answered_by"] == "user"
    assert api.post(f"/imports/{import_id}/answers", json={"question_id": "Q99", "answer": "x"}).status_code == 404

    # "assume" needs the location first, and nothing is assumed without it
    refused = api.post(f"/imports/{import_id}/assume", json={"question_ids": "all"})
    assert refused.status_code == 409 and "location" in refused.json()["detail"]
    assert api.get(f"/imports/{import_id}/questions").json()["assumptions"] == []

    # approval is refused while a blocking question is open
    blocked = api.post(f"/imports/{import_id}/approve", json={})
    assert blocked.status_code == 409
    assert any("Floor to floor" in e for e in blocked.json()["detail"]["errors"])
    # and nothing builds before approval
    assert api.post(f"/imports/{import_id}/build", json={"document_title": "Scratch"}).status_code == 409

    assert api.post(f"/imports/{import_id}/location", json={"city": "Kolkata", "state": "West Bengal",
                                                           "country": ""}).status_code == 422
    located = api.post(f"/imports/{import_id}/location", json={"city": "Kolkata", "municipality": "KMC",
                                                               "state": "West Bengal", "country": "India"}).json()
    assert located["location"] == {"city": "Kolkata", "municipality": "KMC", "state": "West Bengal", "country": "India"}
    assumed = api.post(f"/imports/{import_id}/assume", json={"question_ids": "all"}).json()
    assert len(assumed["assumed"]) == 1 and assumed["assumptions"][0]["basis"].startswith("Kolkata")
    assert _question(assumed, "storey_height")["answered_by"] == "assumed"
    assert _question(assumed, "opening_without_mark")["answered_by"] == "user"  # the user's answer stays
    assert assumed["readiness"]["ready"] is True

    approved = api.post(f"/imports/{import_id}/approve", json={"note": "checked the overlay"}).json()
    assert approved["approved"] is True

    missing = api.post(f"/imports/{import_id}/build", json={"document_title": "Scratch"})
    assert missing.status_code == 409
    assert missing.json()["detail"]["missing"] == [{"text": "Door heights are not given anywhere."}]

    plan = two_storey_house("Door 900", "Window 1200")
    FakeClient._created = {op.key: 0 for op in plan.ops}
    built = api.post(f"/imports/{import_id}/build", json={"document_title": "Scratch"})
    assert built.status_code == 202
    run_id = built.json()["run_id"]
    with service._lock:
        pass
    run = service.store.get(run_id)
    assert run.load("run")["status"] == "built"
    assert run.load("import")["id"] == import_id
    assert run.load("recognised_building")["plans"][0]["walls"][0]["id"] == "wall-1"
    assert {a["answered_by"] for a in run.load("answers")} == {"user", "assumed"}
    assert run.load("assumptions")[0]["question"] == height["id"]
    assert run.load("approval")["note"] == "checked the overlay"
    assert api.get(f"/imports/{import_id}").json()["runs"] == [run_id]

    # changing an answer after approval withdraws it: approve again before the next build
    api.post(f"/imports/{import_id}/answers", json={"question_id": mark["id"], "answer": "door"})
    assert api.get(f"/imports/{import_id}").json()["approved"] is False
    assert api.post(f"/imports/{import_id}/build", json={"document_title": "Scratch"}).status_code == 409


def test_unwired_engine_never_assumes_or_approves(tmp_path, monkeypatch):
    def not_wired(*a):
        raise imports_api.NotWired("The drawing import step 'pipeline' is not installed in this B2M build yet.")
    for name in ("run_import", "question_groups", "apply_answer", "assume", "ready_to_build", "to_plan"):
        monkeypatch.setattr(imports_api, name, not_wired)
    drawings = tmp_path / "drawings"
    drawings.mkdir()
    _sheet(drawings / "A-101.dxf", "%%UDOOR SCHEDULE")
    _, app, api = _app(tmp_path)
    import_id = api.post("/imports", json={"files": [str(drawings / "A-101.dxf")]}).json()["import_id"]
    _wait(app)
    status = api.get(f"/imports/{import_id}").json()
    assert status["status"] == "failed" and "not installed" in status["error"]
    assert status["sheets"][0]["role"] == "unknown" and status["readiness"]["ready"] is False
    review = api.get(f"/imports/{import_id}/questions").json()
    (group,) = review["groups"]
    assert group["severity"] == "blocking" and group["questions"][0]["topic"] == "sheet_role"
    api.post(f"/imports/{import_id}/location", json={"city": "Kolkata", "state": "West Bengal", "country": "India"})
    assert api.post(f"/imports/{import_id}/assume", json={"question_ids": "all"}).status_code == 501
    assert api.post(f"/imports/{import_id}/approve", json={}).status_code == 409


@pytest.fixture
def real_plan(monkeypatch):
    def run_import(project):  # the real recogniser on every plan sheet
        plans = []
        for source, record in zip(project.sources, project.sheets):
            sh = sheet.detect(reader.read_dxf(source.dxf, source.path))
            plans.append(recognise_plan(sh, classify.classify(sh.title, record.file)))
            for f in plans[-1].findings[:2]:
                project.add_question(f.topic, f.text, [Evidence(sheet=record.file, handles=f.evidence.handles)])
        return RecognisedBuilding(plans=plans)
    monkeypatch.setattr(imports_api, "run_import", run_import)


def test_overlay_of_a_real_sample_plan_sheet(tmp_path, real_plan):
    _, app, api = _app(tmp_path)
    import_id = api.post("/imports", json={"files": [str(SAMPLE_DXF / "1.GROUND FLOOR PLAN.dxf")]}).json()["import_id"]
    _wait(app)
    status = api.get(f"/imports/{import_id}").json()
    assert status["status"] == "ready", status
    assert status["sheets"][0]["recognised"]["walls"] > 0

    response = api.get(f"/imports/{import_id}/sheets/1/overlay.svg")
    assert response.status_code == 200 and response.headers["content-type"].startswith("image/svg+xml")
    svg = response.text
    assert svg.startswith("<svg") and svg.rstrip().endswith("</svg>")
    assert 'class="recognised"' in svg and "data-id=" in svg and "data-handles=" in svg
    assert 'class="evidence"' in svg and 'data-question="Q1"' in svg  # question evidence, hidden until shown

    elements = api.get(f"/imports/{import_id}/sheets/1/elements").json()["elements"]
    walls = [e for e in elements.values() if e["kind"].endswith("wall")]
    assert walls and all(e["handles"] and "mm thick" in e["size"] for e in walls)
    assert any(e["kind"] == "finding" for e in elements.values())
    assert api.get(f"/imports/{import_id}/sheets/2/overlay.svg").status_code == 404
