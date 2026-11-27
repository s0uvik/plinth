"""The fit-out through the HTTP API (and so the review page and the MCP tools), on the G+4 sample's building as built
from an import, with the fake Revit: plan (questions become import questions of stage "fitout"; answers typed as JSON
are read as JSON), build (refused without a reviewed plan, with open questions, in another document; else its own run
in the background, recorded with the import), remove (only what it built)."""

import json

import pytest
from fastapi.testclient import TestClient

from plinth.api import create_app
from plinth.builds import BuildService
from plinth.cad.project import ImportProject, ImportStore
from plinth.fitout import session
from plinth.revit.types import TypeMap
from plinth.runs import RunStore
from tests.fitout.fake_revit import FitoutRevit
from tests.fitout.test_session import MEP, TITLE, template

IMPORT_ID = "2026-11-20T10-00-00Z-ab12"
AC = ("fitout:ac:living",)

# The sample's plan has no slab over the stair/lift rooms the overhead tanks are answered on: the layout asks
# fitout:overhead_tank_slab rather than leave them floating; answered here as added (as tests/fitout/test_mep.py does).
SLAB = {"fitout:overhead_tank_slab": "slab"}


class Addin(FitoutRevit):
    """The fake document as the service connects to it: health says which commands the add-in has."""

    def __init__(self, *a, **kw):
        super().__init__(*a, **kw)
        self.checked: list[list[str]] = []
        self.lacks: set[str] = set()

    def check(self, required_commands):
        self.checked.append(list(required_commands))
        missing = sorted(set(required_commands) & self.lacks)
        if missing:
            raise ConnectionError(f"Add-in 0.9.0 lacks commands: {', '.join(missing)}.")
        return self.health


@pytest.fixture(scope="module")
def fitout_answers():
    from tests.cad.conftest import SAMPLE
    return {k: v for k, v in json.loads((SAMPLE / "fitout-answers.json").read_text(encoding="utf-8")).items()
            if not k.startswith("_")} | SLAB


@pytest.fixture
def world(sample_import, wb_rules, tmp_path):
    """An import (located in Kolkata) whose build is done: its run holds the sample's plan, ids and import plan."""
    _, result = sample_import
    store = RunStore(tmp_path / "runs")
    category = {"wall": "OST_Walls", "floor": "OST_Floors", "level": "OST_Levels"}
    elements, created = {}, {}
    for i, op in enumerate(result.plan.ops):
        created[op.key] = 100000 + i
        elements[100000 + i] = {"category": category.get(op.op, "OST_GenericModel"), "level": getattr(op, "level", None)}
    run = store.create()
    run.save("plan", result.plan.model_dump(mode="json"))
    run.save("import_plan", {"sources": {k: v.model_dump(mode="json") for k, v in result.sources.items()}})
    run.save("import", {"id": IMPORT_ID})
    run.update(status="built", created=created, document_title=TITLE)

    imports = ImportStore(tmp_path / "imports")
    project = ImportProject(id=IMPORT_ID, created="2026-11-20T10:00:00Z")
    project.set_location(city="Kolkata", state="West Bengal")
    project.set_value("country", "India")
    imports.save(project)
    (imports.root / IMPORT_ID / "import.json").write_text(json.dumps(
        {"import_id": IMPORT_ID, "status": "ready", "runs": [run.id]}), encoding="utf-8")

    revit = Addin(templates=template(wb_rules), elements=elements, title=TITLE)
    service = BuildService(store, lambda: revit, types=TypeMap())
    app = create_app(service, import_store=imports)
    return TestClient(app), service, run.id, revit


def plan(api, run_id, **body):
    return api.post(f"/imports/{IMPORT_ID}/fitout/plan", json={"run_id": run_id, "document_title": TITLE, **body})


def build(api, service, run_id, **body):
    response = api.post(f"/imports/{IMPORT_ID}/fitout/build", json={"run_id": run_id, "document_title": TITLE, **body})
    if response.status_code == 202:
        with service._lock:  # the background build is done
            pass
    return response


def test_plan_asks_its_questions_as_import_questions_and_reads_json_answers(world, fitout_answers):
    api, service, run_id, revit = world
    given = {k: v for k, v in fitout_answers.items() if k not in AC}
    r = plan(api, run_id, systems=["furniture", "hvac"], answers=given)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["rules"] == "west-bengal" and body["rules_from"].startswith("the project location (Kolkata")
    assert not body["ready"] and body["buildable"] and body["report"]["notice"].startswith("Indicative coordination")
    assert set(body["report"]["mep"]["counts"]) == {"hvac"}
    assert {q["id"] for q in body["questions"]} >= set(AC)
    assert all(q["key"]["stage"] == "fitout" and q["answer_type"] == "stage_value" for q in body["questions"])
    assert revit.checked[0] == session.PLAN_COMMANDS
    # the import's questions now hold them, shown with the others
    asked = [q for g in api.get(f"/imports/{IMPORT_ID}/questions").json()["groups"] for q in g["questions"]]
    assert {q["id"] for q in asked} >= set(AC)

    # the build waits for them
    refused = build(api, service, run_id)
    assert refused.status_code == 409 and AC[0] in refused.text

    # answered as typed on the page: JSON text is read as JSON
    for qid in AC:
        a = api.post(f"/imports/{IMPORT_ID}/answers", json={"question_id": qid, "answer": "[1.5, 1.5]"})
        assert a.status_code == 200, a.text
    project = ImportStore(service.store.root.parent / "imports").get(IMPORT_ID)
    assert project.question(AC[0]).answer == [1.5, 1.5] and project.inputs.values[AC[-1]] == [1.5, 1.5]
    state = api.get(f"/imports/{IMPORT_ID}/fitout").json()
    assert state["plan"]["stale"] == list(AC)  # answered since: plan again to use them

    again = plan(api, run_id, systems=["furniture", "hvac"], answers=given)
    assert again.status_code == 200 and again.json()["ready"], again.json()["report"]["questions"]
    assert again.json()["stale"] == [] and again.json()["questions"] == []
    saved = service.store.get(run_id).load("fitout_plan")
    assert saved["planned"]["answers"][AC[0]] == [1.5, 1.5] and saved["document_title"] == TITLE


def test_text_answers_stay_text_and_numbers_to_options_stay_option_text():
    from plinth.api import fitout_answer
    from plinth.cad.project import Question
    free = Question(id="fitout:building_height_m", topic="fitout_height", text="?")
    choice = Question(id="fitout:family:sofa", topic="fitout_families", text="?", options=['{"family": "A"}', "skip"])
    assert fitout_answer(free, "15") == 15 and fitout_answer(free, "stair") == "stair"
    assert fitout_answer(choice, '{"family": "A"}') == {"family": "A"} and fitout_answer(choice, "skip") == "skip"
    assert fitout_answer(choice, "1") == "1" and fitout_answer(choice, {"create": "box"}) == {"create": "box"}


def test_build_needs_a_reviewed_plan_in_the_same_document(world, fitout_answers):
    api, service, run_id, revit = world
    r = build(api, service, run_id)
    assert r.status_code == 409 and "Plan the fit-out on this run first" in r.text
    other = plan(api, run_id, document_title="Another project", systems=["furniture"])
    assert other.status_code == 409 and "was built in 'G4 sample'" in other.text
    assert plan(api, run_id, systems=["furniture"], answers=fitout_answers).status_code == 200
    r = api.post(f"/imports/{IMPORT_ID}/fitout/build", json={"run_id": run_id, "document_title": "Another project"})
    assert r.status_code == 409 and "planned in 'G4 sample'" in r.text
    # wrong inputs stop before Revit, and leave no plan to build
    calls = len(revit.calls)
    stopped = plan(api, run_id, systems=["gas"])
    assert stopped.status_code == 409 and "systems are" in stopped.text and len(revit.calls) == calls
    assert plan(api, run_id, flat="U9", level="Floor 1").status_code == 409
    # an add-in without what planning reads stops it, and the plan before it is gone
    revit.lacks = {"list_types_in_file"}
    failed = plan(api, run_id, systems=["furniture"], answers=fitout_answers)
    assert failed.status_code == 502 and "lacks commands: list_types_in_file" in failed.text
    assert build(api, service, run_id).status_code == 409


def test_build_runs_in_the_background_is_recorded_and_removed_alone(world, fitout_answers):
    api, service, run_id, revit = world
    before = dict(revit.elements)
    assert plan(api, run_id, systems=["furniture", "electrical"], flat="U1", level="Floor 1",
                answers=fitout_answers).json()["ready"]
    r = build(api, service, run_id)
    assert r.status_code == 202, r.text
    fit_id = r.json()["run_id"]
    record = api.get(f"/runs/{fit_id}").json()
    assert record["status"] in ("built", "built_with_issues"), record.get("error")
    assert record["document_title"] == TITLE and record["created"]
    built_plan = service.store.get(fit_id).load("plan")  # the sockets are hosted on the building's own walls
    assert built_plan.get("existing") and set(built_plan["existing"]) <= set(service.store.get(run_id).load("run")["created"])
    report = service.store.get(fit_id).load("fitout_report")
    assert report["building_run"] == run_id and report["import_id"] == IMPORT_ID and report["answers"]
    assert report["systems"] == ["furniture", "electrical"] and report["flat"] == "U1"
    assert any(c == session.required_commands(session.Planned.model_validate(
        service.store.get(run_id).load("fitout_plan")["planned"]).plan) for c in revit.checked)

    state = api.get(f"/imports/{IMPORT_ID}/fitout").json()
    assert [b["run_id"] for b in state["building_runs"]] == [run_id]
    assert state["plan"]["built_as"] == fit_id and state["notice"].startswith("Indicative coordination")
    assert [(x["run_id"], x["building_run"], x["flat"]) for x in state["runs"]] == [(fit_id, run_id, "U1")]
    assert state["runs"][0]["elements"] == len(record["created"])

    # the same plan is not built twice
    assert build(api, service, run_id).status_code == 409

    removed = api.post(f"/runs/{fit_id}/remove").json()
    assert removed["status"] == "removed"
    assert revit.elements == before  # only what the fit-out built went
    assert api.post(f"/runs/{run_id}/remove").json()["status"] == "removed"


def test_partial_build_and_mep_on_a_furniture_run(world, fitout_answers):
    api, service, run_id, revit = world
    without_ac = {k: v for k, v in fitout_answers.items() if k not in AC}  # the furniture run's answers are defaults
    furniture = plan(api, run_id, systems=["furniture"], flat="U1", level="Floor 1", answers=without_ac)
    assert furniture.json()["ready"]  # an HVAC question is not the furniture's
    frun = build(api, service, run_id).json()["run_id"]
    assert service.store.get(frun).load("fitout_layout")  # kept, so the MEP pass on it lays nothing out again
    mep = plan(api, run_id, systems=["hvac"], furniture_run=frun, answers=without_ac)
    assert mep.status_code == 200, mep.text
    assert not mep.json()["ready"] and build(api, service, run_id).status_code == 409  # the AC questions are open
    r = build(api, service, run_id, partial=True)  # what is ready is built; the large rooms' AC waits
    assert r.status_code == 202, r.text
    mrun = r.json()["run_id"]
    assert service.store.get(mrun).load("fitout_report")["furniture_run"] == frun
    # each element the MEP run builds on is recorded with the run that built it (the page passes the plan's owners)
    existing = service.store.get(mrun).load("plan").get("existing") or {}
    owners = service.store.get(mrun).load("run").get("existing_runs") or {}
    assert set(owners) == set(existing) and set(owners.values()) <= {frun, run_id}
    if any(owner == frun for owner in owners.values()):
        assert api.post(f"/runs/{frun}/remove").status_code == 409  # the MEP run on it goes first
    assert [x["run_id"] for x in api.get(f"/imports/{IMPORT_ID}/fitout").json()["runs"]] == [frun, mrun]
    assert api.post(f"/runs/{mrun}/remove").json()["status"] == "removed"  # the latest first
    assert api.post(f"/runs/{frun}/remove").json()["status"] == "removed"


def test_a_moved_furniture_item_stops_the_plan_with_what_to_do(world, fitout_answers):
    api, service, run_id, revit = world
    assert plan(api, run_id, systems=["furniture"], flat="U1", level="Floor 1", answers=fitout_answers).json()["ready"]
    frun = build(api, service, run_id).json()["run_id"]
    saved = service.store.get(frun).load("plan")
    moved = next(op for op in saved["ops"] if op["op"] == "family")
    moved["point"] = [moved["point"][0] + 50, moved["point"][1]]
    service.store.get(frun).save("plan", saved)
    r = plan(api, run_id, systems=["plumbing"], furniture_run=frun, flat="U1", level="Floor 1", answers=fitout_answers)
    assert r.status_code == 409
    drift = r.json()["detail"]["drift"]
    assert drift["mismatches"][0].startswith(moved["key"] + ": point") and "Plan with the answers" in drift["what_to_do"]
    assert service.store.get(run_id).load("fitout_plan") is None  # nothing left to build


def test_location_or_rules_are_asked_for(world):
    api, service, run_id, _ = world
    imports = ImportStore(service.store.root.parent / "imports")
    project = imports.get(IMPORT_ID)
    project.inputs.location = None
    imports.save(project)
    r = plan(api, run_id, systems=["furniture"])
    assert r.status_code == 409 and "location" in r.text and "Or give the fit-out rule set" in r.text
    assert plan(api, run_id, systems=["furniture"], rules="west-bengal").status_code == 200


def test_the_review_page_has_the_fitout_section(world):
    api = world[0]
    page = api.get("/import").text
    for element_id in ("fitout", "fitout-run", "fitout-title", "fitout-systems", "fitout-furniture-run", "fitout-flat",
                       "fitout-level", "fitout-exclude", "fitout-plan", "fitout-build", "fitout-partial",
                       "fitout-report", "fitout-questions", "fitout-runs"):
        assert f'id="{element_id}"' in page, element_id
    assert "/fitout/plan" in page and "/fitout/build" in page and "an MEP engineer must design and check" in page
    assert 'id="fitout-build" disabled' in page  # nothing to build until a plan is reviewed
