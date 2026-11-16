"""The drawing import end to end on the G+4 sample (PRD Step 9, T12a), through the API with a fake Revit:

import -> every blocking question answered (answers-test.json, the location, T5's own choices) -> approve -> build
(the fake runner "builds" every op and hands back ids; the fake add-in reads back a model built exactly as planned)
-> self-check with nothing unexplained -> drawing set (its question answered first) -> estimate from the model,
compared with the office's own workbook: the differences are exactly the workbook's own inconsistencies.

Test-only answers: answers-test.json for T6's inputs, and for T5's own questions the choices below. Window sizes are
the office estimate's (W2/W3 640 high, W4 1600 high), where answers-test.json is illustrative (every window 1200).
"""

from __future__ import annotations

import json
from collections import Counter
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from b2m import builds
from b2m.api import create_app
from b2m.builds import BuildService
from b2m.cad import plan_bridge
from b2m.cad.building import RecognisedBuilding
from b2m.cad.project import ImportStore
from b2m.imported.to_plan import ImportPlan
from b2m.revit.types import TypeMap
from b2m.runs import RunStore
from tests.cad.conftest import SAMPLE, SAMPLE_DXF
from tests.cad.test_import_drawings import FakeRevit as DrawingRevit
from tests.selfcheck_readback import perfect_readback

T6 = {k: v for k, v in json.loads((SAMPLE / "answers-test.json").read_text(encoding="utf-8")).items()
      if not k.startswith("_")}
OWN = {  # T5's own questions (the office's names and sizes where the office estimate gives them)
    "mark-width-d1": "800 mm",
    "opening-mark-door-1000": "Entrance",  # the office estimate's name for the four unmarked flat doors
    "opening-mark-window-640": "W2 (640 mm)",
    "door-not-in-wall-1000": {"mode": "new_wall", "thickness_mm": 250, "function": "interior"},
    "rooms-share-space-dining-kitchen-open-living-room": {"one_room": True, "name": "LIVING / DINING / KITCHEN"},
    "rooms-share-space-entrance-entrance": {"one_room": True, "name": "ENTRANCE LOBBY"},
    "tie-beam-depth": 450,
    "window-size-w": {"sill_mm": 900, "head_mm": 2100},
    "window-size-w1": {"sill_mm": 900, "head_mm": 2100},
    "window-size-w2": {"sill_mm": 1460, "head_mm": 2100},
    "window-size-w3": {"sill_mm": 1460, "head_mm": 2100},
    "window-size-w4": {"sill_mm": 500, "head_mm": 2100},
}
ESTIMATE = {  # what the estimate asks (materials and railings are on no drawing), answered as the office's workbook
    "estimate:material:D": "wood",
    "estimate:material:Entrance": "wood",
    "estimate:material:D1": {"ground": "pvc", "typical": {"wood": 12, "pvc": 8}},
    "estimate:material:door-1025-sliding": "exclude",  # lift landing doors: not in the door/window estimate
    "estimate:railings:stair": {"length_mm": 2800, "count": 10},
    "estimate:railings:balcony": [{"kind": "verandah", "subkind": "main", "length_mm": 8125, "count": 16},
                                  {"kind": "verandah", "subkind": "attached", "length_mm": 8500, "count": 16}],
}
REFERENCE = SAMPLE / "door-window-estimate.xlsx"


def value_for(q: dict) -> object:
    if q["id"] in OWN:
        return OWN[q["id"]]
    if q["id"] in T6:
        return T6[q["id"]]
    key = q.get("key") or {}
    if key.get("members"):  # T6's questions asked once for several places: the fixture's value per place
        return {m: T6[m] for m in key["members"]}
    ids = key.get("plan_ids") or []
    if q["answer_type"] == "door_height":
        heights = {T6[i] for i in ids if i in T6} or {2100}  # a mark given by answer: the office's 2100
        assert len(heights) == 1, q
        return heights.pop()
    raise AssertionError(f"no test answer for {q['id']}: {q['text']}")


class FakeAddin(DrawingRevit):
    """The add-in as the build service sees it: the document, the drawing-set commands (the T10 fake) and
    read_back_elements answering from a model built exactly as planned."""

    def __init__(self, state):
        super().__init__()
        self.state = state
        self.title, self.health, self.observer = "Scratch", {"addin_version": "0.1.0", "revit_year": 2026}, None

    def confirm_document(self, expected_title=None):
        assert expected_title in (None, self.title)
        return self.title

    def call(self, command, params=None, **kw):
        if command == "read_back_elements":
            ids = params["element_ids"][params.get("start", 0):]
            return {"elements": [self.state["readback"][i] for i in ids], "next_start": None}
        return super().call(command, params, **kw)

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def fake_runner(state):
    class Runner:
        def __init__(self, revit, progress=None, **kw):
            self.revit = revit

        def build(self, plan):
            created = state["created"]
            assert set(op.key for op in plan.ops) <= {k.split("#")[0] for k in created}
            return SimpleNamespace(status="built", batches_total=1, batches_succeeded=1, created=dict(created),
                                   error=None, error_code=None, failed_key=None, error_data=None, failures=[])

        def verify(self, plan, created):
            return {"ok": True}

    return Runner


@pytest.fixture(scope="module")
def flow(tmp_path_factory):
    tmp = tmp_path_factory.mktemp("e2e")
    state: dict = {}
    revit = FakeAddin(state)
    mp = pytest.MonkeyPatch()
    mp.setattr(builds, "Runner", fake_runner(state))
    service = BuildService(RunStore(tmp / "runs"), lambda: revit, types=TypeMap())
    app = create_app(service, import_store=ImportStore(tmp / "imports"))
    api = TestClient(app)
    yield SimpleNamespace(api=api, app=app, service=service, revit=revit, state=state, imports=tmp / "imports")
    mp.undo()


def test_sample_drawings_to_model_drawings_and_estimate(flow):
    api, state = flow.api, flow.state
    started = api.post("/imports", json={"folder": str(SAMPLE_DXF)})
    import_id = started.json()["import_id"]
    with flow.app.state.imports.lock:
        pass
    assert api.get(f"/imports/{import_id}").json()["status"] == "ready"

    # ---- review: one question per fact
    review = api.get(f"/imports/{import_id}/questions").json()
    counts = {g["severity"]: len(g["questions"]) for g in review["groups"]}
    assert counts == {"blocking": 23, "check": 9, "info": 14}  # 27 blocking before grouping
    texts = [o for g in review["groups"] for q in g["questions"] for o in q["options"]]
    assert not any(o.startswith("{") for o in texts)  # options in words, the machine values behind them
    blocking = review["groups"][0]["questions"]
    assert sum(q["topic"] == "room_not_enclosed" for q in blocking) == 1
    assert sum(q["topic"] == "stair_axis" for q in blocking) == 1

    # ---- answer every blocking question (an answer may raise another: a mark given needs its door height)
    assert api.post(f"/imports/{import_id}/location",
                    json={"city": "Bardhaman", "state": "West Bengal", "country": "India"}).status_code == 200
    answered = []
    review = api.get(f"/imports/{import_id}/questions").json()
    while True:  # every answer returns the questions re-derived; take the next open blocking one
        todo = [q for g in review["groups"] if g["severity"] == "blocking" for q in g["questions"]
                if not q["answered_by"]]
        if not todo:
            break
        q = todo[0]
        assert q["id"] not in answered, f"{q['id']} asked again"
        r = api.post(f"/imports/{import_id}/answers", json={"question_id": q["id"], "answer": value_for(q)})
        assert r.status_code == 200, (q["id"], r.json())
        answered.append(q["id"])
        review = r.json()
    assert "door-height-entrance" in answered  # raised by the mark given to the four flat doors
    assert review["readiness"]["ready"] is True, review["readiness"]
    assert api.post(f"/imports/{import_id}/approve", json={"note": "e2e"}).status_code == 200

    # ---- build: the fake runner hands back an id per op; the read-back is the model built exactly as planned
    store = flow.app.state.imports.store
    project = store.get(import_id)
    raw = RecognisedBuilding.model_validate_json((flow.imports / import_id / "recognised_building.json").read_text(
        encoding="utf-8"))
    result = plan_bridge.to_plan(project, raw)
    assert result.plan is not None, [m.id for m in result.missing]
    state["created"], state["readback"] = perfect_readback(result, plan_bridge.building_for_plan(project, raw))
    built = api.post(f"/imports/{import_id}/build", json={"document_title": "Scratch"})
    assert built.status_code == 202, built.json()
    run_id = built.json()["run_id"]
    with flow.service._lock:
        pass
    record = api.get(f"/runs/{run_id}").json()
    assert record["status"] == "built", record
    run = flow.service.store.get(run_id)
    kept = ImportPlan.model_validate(run.load("import_plan"))  # the full ImportPlan, not just its ops
    assert kept.sources and kept.standards and kept.not_modelled and kept.counts
    assert run.load("plan_answers")["door_width:D1"] == 800
    sc = record["selfcheck"]
    assert (sc["ok"], sc["unexplained"]) == (True, 0), sc
    assert sc["explained"] > 0 and sc["accounting"]["unaccounted"] == 0
    assert record["checks"]["files"] == ["selfcheck.json", "selfcheck.html"]
    page = api.get(f"/runs/{run_id}/selfcheck.html")
    assert page.status_code == 200 and "<html" in page.text.lower()

    # ---- drawing set: the elevation's direction is asked first (an import question), then the set is made
    asked = api.post(f"/imports/{import_id}/drawings", json={"run_id": run_id})
    assert asked.status_code == 409
    (q,) = asked.json()["detail"]["questions"]
    assert q["id"] == "drawing:elevation:5.FRONT ELEVATION.dxf" and q["options"] == ["north", "south", "east", "west"]
    assert api.post(f"/imports/{import_id}/answers", json={"question_id": q["id"], "answer": "north"}).status_code == 200
    assert store.get(import_id).inputs.values[q["id"]] == "north"
    assert api.get(f"/imports/{import_id}").json()["approved"] is True  # a drawing answer leaves the approval
    made = api.post(f"/runs/{run_id}/drawings")  # the run's own endpoint dispatches to the import's drawing set
    assert made.status_code == 200, made.json()
    drawings = made.json()
    assert drawings["status"] == "done" and drawings["sheets"] == [str(n) for n in range(28, 37)]
    assert drawings["pdf_url"] == f"/runs/{run_id}/drawings.pdf"
    assert api.get(drawings["comparison_url"]).status_code == 200
    assert all(k.startswith(("DWG-", )) or k in state["created"] for k in api.get(f"/runs/{run_id}").json()["created"])

    # ---- estimate from the model, compared with the office's workbook
    first = api.post(f"/imports/{import_id}/estimate", json={"run_id": run_id, "reference_xlsx": str(REFERENCE)})
    assert first.status_code == 200, first.json()
    est = first.json()
    assert est["source"] == "model" and est["source_detail"]["elements_read"] == 155 + 153 + 5
    asked = {q["id"] for q in est["questions"]}
    assert set(ESTIMATE) <= asked, asked - set(ESTIMATE)
    for qid in ESTIMATE:
        assert api.post(f"/imports/{import_id}/answers",
                        json={"question_id": qid, "answer": ESTIMATE[qid]}).status_code == 200
    final = api.post(f"/imports/{import_id}/estimate", json={"run_id": run_id, "reference_xlsx": str(REFERENCE)})
    est = final.json()
    assert api.get(est["urls"]["estimate.xlsx"]).status_code == 200
    assert set(est["urls"]) == {"estimate.xlsx", "estimate-crosscheck.html", "estimate-comparison.html"}
    diffs = {(d["cell"], d["kind"]) for d in est["comparison"]["differences"]}
    # exactly the workbook's own inconsistencies (tests/estimate/test_estimate_sample.py): row 22 (160 typed for
    # 33 x 5.0), H22 'Sqm.', row 28's lone =24*4 and 'Total: -24', 'ii)w1' spelt lower case, and W1's width: the
    # model has it as drawn (1050), so the office's 1.0 m shutter and steel paint rows (47, 65) and their totals
    # (51/52, 69/70) differ, where its 1.05 m frame row (32) agrees
    assert diffs == {("G22", "formula_vs_constant"), ("G22", "value"), ("H22", "text"), ("C28", "blank"),
                     ("I28", "text"), ("B47", "text"), ("B65", "text"),
                     ("D47", "number"), ("G47", "value"), ("G51", "value"), ("G52", "value"),
                     ("D65", "number"), ("G65", "value"), ("G69", "value"), ("G70", "value")}
    assert est["comparison"]["rows_compared"] == 69  # every row aligned: none missing, none extra
    open_estimate = est["questions"]
    assert not any(q["id"].startswith(("estimate:material:", "estimate:railings:")) for q in open_estimate)
    reference_rows = sorted(q["id"] for q in open_estimate if q["topic"] == "estimate_reference")
    assert reference_rows == [f"estimate:reference:row-{r}" for r in (22, 28, 47, 51, 52, 65, 69, 70)]
    # the rest: the rules' duplicate SL. NO 3, openings no rule measures (ground / roof windows, the ventilators),
    # and where the drawings disagree with the counts (the drawn door widths, the marks given by answer)
    assert Counter(q["topic"] for q in open_estimate) == EXPECTED_ESTIMATE_TOPICS, \
        [(q["topic"], q["text"][:90]) for q in open_estimate]
    # the same questions are import questions, with stable ids: a rerun asks the same ones
    review = api.get(f"/imports/{import_id}/questions").json()
    staged = {q["id"] for g in review["groups"] for q in g["questions"] if (q.get("key") or {}).get("stage")}
    assert {q["id"] for q in open_estimate} | set(ESTIMATE) <= staged
    again = api.post(f"/imports/{import_id}/estimate", json={"run_id": run_id, "reference_xlsx": str(REFERENCE)})
    assert {q["id"] for q in again.json()["questions"]} == {q["id"] for q in open_estimate}

    # without a run the estimate counts the recognised building with the answers, and says so
    drawn = api.post(f"/imports/{import_id}/estimate", json={}).json()
    assert drawn["source"] == "drawings" and drawn["urls"]["estimate.xlsx"].startswith(f"/imports/{import_id}/")
    assert drawn["comparison"] is None


EXPECTED_ESTIMATE_TOPICS = Counter({
    "estimate_rules": 1,  # the rules file gives SL. NO 3 to two sections
    "estimate_coverage": 1,  # the ground-floor W2 (a mark given by answer): no rule measures ground-floor windows
    "estimate_crosscheck": 7,  # lift landing doors drawn (1025) but left out (3 sheets); drawn 800/900 door counts
    # against D/D1 as answered (the four 900 D1s answered 800); 'Entrance' is a name given by answer; D1's material
    "estimate_reference": 8,  # rows 22, 28, 47, 51, 52, 65, 69, 70
})
