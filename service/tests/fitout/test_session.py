"""The fit-out flow on a built model (plinth.fitout.session), on the G+4 sample with the user's MEP answers of 2026-11-17:
furniture and MEP planned into one plan that builds on the fake Revit, reads back and is removed whole; the MEP alone
as its own run on top of an earlier furniture run (its furniture joined, never placed again; a moved item stops the
plan); the systems asked for and a one-flat pilot leave out the rest and whatever joins it; joins and circuits Revit
refuses are the run's issues."""

import json
import math
from collections import Counter

import pytest

from plinth.fitout import families, session
from plinth.plan import BuildPlan
from plinth.revit.types import TypeMap
from plinth.runs import RunStore
from tests.cad.conftest import SAMPLE
from tests.fitout.fake_revit import FitoutRevit

TEMPLATE = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Plinth-Test_Metric.rte"
TITLE = "G4 sample"
MEP = ["plumbing", "electrical", "hvac", "fire"]
# Placements as such families usually are: wall points and lights face-based, the rest level-based.
FACE_BASED = {"OST_LightingFixtures", "OST_ElectricalFixtures", "OST_LightingDevices", "OST_CommunicationDevices",
              "OST_DataDevices"}
SYSTEM_TYPES = [("Sanitary", "Sanitary"), ("Domestic Cold Water", "DomesticColdWater"),
                ("Domestic Hot Water", "DomesticHotWater"), ("Vent", "Vent"), ("Other", "OtherPipe"),
                ("Fire Protection Wet", "FireProtectWet")]


def template(rules) -> dict:
    """One family type per furniture item and MEP device of the rules (named by its first keyword), and the pipe and
    conduit types and piping systems of a metric template."""
    specs = list(rules.furniture.items.values())
    specs += [d for cat in (rules.electrical.devices, rules.plumbing.devices, rules.hvac.devices, rules.fire.devices)
              for d in cat.values()]
    rows, seen = [], set()
    for spec in specs:
        category = families.builtin_category(spec.category)
        family = f"Test {spec.keywords[0]}"
        if (category, family) in seen:
            continue
        seen.add((category, family))
        rows.append({"id": len(rows) + 1, "category": category, "family": family, "type": "Standard",
                     "placement": "WorkPlaneBased" if category in FACE_BASED else "OneLevelBased", "dims_mm": {}})
    return {TEMPLATE: {"family_types": rows, "mep_types": {
        "pipe_type": [{"id": 901, "name": "PVC - DWV"}],
        "conduit_type": [{"id": 902, "name": "Rigid Nonmetallic Conduit (RNC Sch 40)"}],
        "pipe_system_type": [{"id": 910 + i, "name": n, "classification": c} for i, (n, c) in enumerate(SYSTEM_TYPES)]}}}


@pytest.fixture(scope="module")
def fitout_answers():
    return {k: v for k, v in json.loads((SAMPLE / "fitout-answers.json").read_text(encoding="utf-8")).items()
            if not k.startswith("_")}


@pytest.fixture
def world(sample_import, wb_rules, tmp_path):
    """The sample's building as built (its run: plan, created ids, import plan's sources) and the fake document."""
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
    run.update(status="built", created=created, document_title=TITLE)
    revit = FitoutRevit(templates=template(wb_rules), elements=elements, title=TITLE)
    return store, run.id, revit


def plan(world, answers, **options):
    store, run_id, revit = world
    return session.plan_fitout(revit, store, run_id, TITLE, "west-bengal", answers=answers, **options)


def build(world, planned):
    store, _, revit = world
    return session.build(planned, store, TITLE, connect=lambda: revit, types=TypeMap())


def test_furniture_and_mep_are_one_plan_built_read_back_and_removed(world, fitout_answers):
    store, _, revit = world
    planned = plan(world, fitout_answers)
    r = planned.report
    assert planned.ready, r["questions"]
    assert r["notice"].startswith("Indicative coordination model") and r["systems"] == list(session.SYSTEMS)
    assert r["mep"]["problems"] == [] and r["mep"]["unrouted"] == []
    assert set(r["mep"]["counts"]) == set(MEP) and r["mep"]["water"]["underground_l"] > 0
    assert any(a["id"] == "fitout:rainwater_system" for a in r["assumptions"])
    ops = Counter(op.op for op in planned.plan.ops)
    assert ops["family"] > 1500 and ops["mep_run"] > 1500 and ops["circuit"] > 200
    by_key = planned.plan.by_key()
    # the runs end at the furniture fixtures of the same plan; every run is best effort, those joining runs too (an
    # elbow a real type cannot make at a size is an issue, never a stop)
    wc_runs = [op for op in planned.plan.ops if op.op == "mep_run" and "-wc-" in (op.start_key or "")]
    assert wc_runs and all(by_key[op.start_key].op == "family" for op in wc_runs)
    stacks = [op for op in planned.plan.ops if op.op == "mep_run" and op.key.split("-")[-2].endswith("_stack")]
    assert stacks and not [op for op in planned.plan.ops if op.op == "mep_run" and op.require_connections]
    # no leg is too short for an elbow
    short = [op.key for op in planned.plan.ops if op.op == "mep_run" and len(op.points) > 2
             and min(math.dist(a, b) for a, b in zip(op.points, op.points[1:])) < 50]
    assert not short, short[:5]
    assert all(op.best_effort for op in planned.plan.ops if op.op == "circuit")
    # ceiling lights face down at the ceiling's height (no ceilings in the model); wall points on their wall's side
    lights = [op for op in planned.plan.ops if op.op == "family" and "-light-" in op.key]
    assert lights and all(op.face_down and op.offset_mm > 0 for op in lights)
    sockets = [op for op in planned.plan.ops if op.op == "family" and "socket" in op.key]
    assert sockets and all(op.host_key and op.toward for op in sockets)
    outdoor = [op for op in planned.plan.ops if op.op == "family" and "ac_outdoor" in op.key]
    assert len(outdoor) == r["mep"]["counts"]["hvac"]["devices"]["ac_outdoor"]  # level-based: outside, on the level

    before = dict(revit.elements)
    record = build(world, planned)
    assert record["status"] == "built", {k: record.get(k) for k in ("error", "failed_key", "issues", "readback")}
    assert record["readback"]["ok"] and record["issues"] == []
    saved = store.get(record["run_id"]).load("fitout_report")
    assert saved["building_run"] == world[1] and saved["systems"] == list(session.SYSTEMS) and saved["answers"]
    assert saved["notice"] == r["notice"]
    from plinth.builds import BuildService
    assert BuildService(store, lambda: revit).remove(record["run_id"])["status"] == "removed"
    assert revit.elements == before


def test_mep_on_top_of_a_furniture_run_joins_its_furniture_and_stops_when_it_moved(world, fitout_answers):
    store, run_id, revit = world
    furniture = plan(world, {}, systems=["furniture"])
    assert furniture.ready and "notice" not in furniture.report
    assert {op.op for op in furniture.plan.ops} <= {"types_from_file", "family"}
    frun = build(world, furniture)
    assert frun["status"] == "built"
    furniture_keys = {op.key for op in furniture.plan.ops if op.op == "family"}

    planned = plan(world, fitout_answers, systems=MEP, furniture_run=frun["run_id"])
    assert planned.ready, planned.report["questions"]
    keys = set(planned.plan.by_key())
    assert not keys & furniture_keys  # never placed again
    joined = {k for k in planned.plan.existing if k.startswith("FIT-")}
    assert joined and joined <= furniture_keys
    assert all(planned.plan.existing[k] == frun["created"][k] for k in joined)
    assert planned.report["furniture_check"]["compared"] == len(furniture_keys)
    assert planned.report["items"]["wc"]["built_before"] == planned.report["items"]["wc"]["laid_out"]
    mep_run = build(world, planned)
    assert mep_run["status"] == "built", mep_run.get("error")
    assert store.get(mep_run["run_id"]).load("fitout_report")["furniture_run"] == frun["run_id"]
    # the furniture run's answers are the defaults: its run gives them back
    assert planned.answers == {**store.get(frun["run_id"]).load("fitout_report")["answers"], **fitout_answers}

    # the furniture run's plan says one item stood elsewhere: the layout now is not what was built
    saved = store.get(frun["run_id"]).load("plan")
    moved = next(op for op in saved["ops"] if op["op"] == "family" and "-wc-" in op["key"])
    moved["point"] = [moved["point"][0] + 50, moved["point"][1]]
    store.get(frun["run_id"]).save("plan", saved)
    with pytest.raises(session.Drift) as stopped:
        plan(world, fitout_answers, systems=MEP, furniture_run=frun["run_id"])
    assert len(stopped.value.mismatches) == 1 and stopped.value.mismatches[0].startswith(moved["key"] + ": point")


def test_the_systems_asked_for_leave_out_the_rest_and_what_joins_it(world, fitout_answers):
    planned = plan(world, fitout_answers, systems=["plumbing"])
    ops = planned.plan.ops
    assert planned.ready
    assert not [op for op in ops if op.op in ("circuit",) or getattr(op, "kind", None) == "conduit"]
    assert not [op for op in ops if op.op == "family" and op.key.startswith("FIT-")]  # no furniture
    # without the furniture, the runs to its fixtures are left out with them; the rest of the plumbing stands
    assert planned.report["dropped"]
    assert planned.report["dropped"]["it joins what is left out"] > 0
    assert planned.report["dropped"]["not in the systems built (plumbing)"] > 0
    assert [op for op in ops if op.op == "mep_run"] and set(planned.report["mep"]["counts"]) == {"plumbing"}
    assert BuildPlan.model_validate(planned.plan.model_dump(mode="json")) == planned.plan
    # an HVAC-only question is not asked of a plumbing build
    hvac_only = plan(world, {k: v for k, v in fitout_answers.items() if not k.startswith("fitout:ac:")}, systems=["plumbing"])
    assert hvac_only.ready and [q["id"] for q in hvac_only.report["questions_not_needed_now"]] == [
        "fitout:ac:living dining:39", "fitout:ac:living dining:40"]


def test_a_flat_pilot_keeps_the_flat_and_what_it_joins(world, fitout_answers):
    _, _, revit = world
    planned = plan(world, fitout_answers, flat="U1", level="Floor 1")
    assert planned.ready
    model_rooms = {r.key: r for r in session.prepare(world[0], world[1], "west-bengal", answers=fitout_answers).model.rooms}
    furniture = [op for op in planned.plan.ops if op.op == "family" and op.key.startswith("FIT-")]
    assert furniture and all(model_rooms[op.room_key].flat == "U1" and model_rooms[op.room_key].level == "Floor 1"
                             for op in furniture)
    runs = [op for op in planned.plan.ops if op.op == "mep_run"]
    kinds = Counter(op.key.split("-")[-2] for op in runs)
    # the flat's branches reach their stacks: the stacks they tee into are in the pilot, other flats' branches not
    assert kinds["soil_stack"] >= 1 and kinds["waste_branch"] >= 1 and kinds["cold_branch"] >= 1
    assert all(op.level == "Floor 1" or op.key.split("-")[-2].endswith(("_stack", "downtake", "riser"))
               for op in runs), sorted({(op.level, op.key.split("-")[-2]) for op in runs})
    assert planned.report["flat"] == "U1" and planned.report["dropped"]
    record = build(world, planned)
    assert record["status"] == "built", record.get("error")
    # the rest built later, without what the pilot built
    rest = plan(world, fitout_answers, exclude_runs=[record["run_id"]])
    elements = {op.key for op in rest.plan.ops if op.op in ("family", "mep_run", "circuit")}
    assert elements and not elements & {k for k in record["created"] if "#" not in k}
    assert {k for k in rest.plan.existing if k in record["created"]}  # what the pilot built is joined
    assert build(world, rest)["status"] == "built"
    calls = len(revit.calls)
    with pytest.raises(session.SessionError, match="no rooms of flat 'U9'"):
        plan(world, fitout_answers, flat="U9", level="Floor 1")
    assert len(revit.calls) == calls  # stopped before Revit was asked anything


def test_a_pilot_is_held_back_only_by_questions_about_what_it_builds():
    # 2026-11-27: both large-living-room AC questions blocked every one-flat pilot, though each was about other flats'
    # rooms (their instances are room keys, not records; their form must not matter)
    from plinth.fitout.rooms import Question
    inputs = session.Inputs.model_construct(systems=["furniture", "hvac", "plumbing"], flat="U1", level="Floor 1")

    def asks(instances, topic="fitout_ac", held=frozenset({"ROOM-U1-LIVING", "FIT-U1-sofa-1"}), built=("hvac",)):
        q = Question(id="q", topic=topic, text="?", options=[], instances=list(instances))
        return session._asks_now(q, inputs, gone={"FIT-U2-sofa-1"}, held=set(held), built_systems=set(built))

    assert asks(["ROOM-U1-LIVING", "ROOM-U2-LIVING"])  # one of its rooms is the pilot's
    assert not asks(["ROOM-U2-LIVING", "ROOM-U3-LIVING"])  # another flat's rooms only
    assert not asks(["FIT-U2-sofa-1"], topic="families")  # only what is not built now
    assert asks(["FIT-U1-sofa-1"], topic="families")
    assert asks([], topic="fitout_meter_room") is False  # no instances: its system has nothing in the pilot
    assert asks([], topic="fitout_tank", built=("plumbing",))
    assert asks([], topic="fitout_stack_kind")  # shapes every system
    assert not asks(["ROOM-U1-LIVING"], topic="fitout_fire")  # fire is not built now
    whole = session.Inputs.model_construct(systems=["hvac"], flat=None, level=None)
    q = Question(id="q", topic="fitout_ac", text="?", options=[], instances=["ROOM-U2-LIVING"])
    assert session._asks_now(q, whole, gone=set(), held=None, built_systems={"hvac"})  # not a pilot: as before


def test_joins_and_circuits_revit_refuses_are_the_runs_issues(world, fitout_answers):
    store, _, revit = world
    revit.unjoinable = {"OST_PlumbingFixtures", "OST_ElectricalFixtures", "OST_LightingFixtures",
                        "OST_LightingDevices", "OST_MechanicalEquipment", "OST_ElectricalEquipment"}
    revit.unpowered = {"OST_ElectricalFixtures"}
    planned = plan(world, fitout_answers, flat="U1", level="Floor 1")
    record = build(world, planned)
    assert record["status"] == "built_with_issues"
    issues = Counter(i["what"] for i in record["issues"])
    assert issues["connection"] > 10 and issues["circuit_device"] > 10
    assert record["readback"]["ok"], record["readback"]  # circuits not made are not missed
    made = {k for k in record["created"] if "#" not in k}
    not_made = {i["key"] for i in record["issues"] if i["what"] == "circuit"}
    assert not_made and not not_made & made
    assert all(i["reason"] for i in record["issues"])


def test_wrong_inputs_stop_before_revit(world):
    store, run_id, revit = world
    for options, said in (({"systems": ["plumbing", "gas"]}, "systems are"),
                          ({"measured_level": "Floor 9"}, "measured level"),
                          ({"level": "Floor 1"}, "give the flat too"),
                          ({"furniture_run": "nope"}, "no run nope")):
        with pytest.raises(session.SessionError, match=said):
            session.prepare(store, run_id, "west-bengal", **options)
    with pytest.raises(session.SessionError, match="no run nope"):
        session.prepare(store, "nope", "west-bengal")
    assert not revit.calls


def test_the_command_line_plans_and_builds_a_pilot_with_its_mep(world, fitout_answers, tmp_path, capsys):
    from scripts import fitout_build
    store, run_id, revit = world
    argv = ["--run", run_id, "--title", TITLE, "--rules", "west-bengal", "--answers", json.dumps(fitout_answers),
            "--systems", "furniture,plumbing,electrical", "--flat", "U2", "--level", "Floor 2",
            "--out", str(tmp_path / "out")]
    assert fitout_build.main(argv, connect=lambda: revit, store=store) == 0
    printed = capsys.readouterr().out
    assert "NOTICE: Indicative coordination model" in printed and "pilot: flat U2 on Floor 2" in printed
    assert "  plumbing: " in printed and "  electrical: " in printed and "  hvac: " not in printed
    assert "READY" in printed
    report = json.loads((tmp_path / "out" / "report.json").read_text(encoding="utf-8"))
    assert report["systems"] == ["furniture", "plumbing", "electrical"] and report["mep"]["problems"] == []
    assert fitout_build.main(argv + ["--build"], connect=lambda: revit, store=store, types=TypeMap()) == 0
    assert "build " in capsys.readouterr().out
    built = [r for r in store.list() if r["run_id"] != run_id]
    assert len(built) == 1 and built[0]["status"] == "built"
    assert fitout_build.main(argv[:-2] + ["--systems", "gas"], connect=lambda: revit, store=store) == 2
    capsys.readouterr()
    # an answer planning cannot take is said, not a traceback
    bad = json.dumps({**fitout_answers, "fitout:family:bed": {"create": "cylinder"}})
    assert fitout_build.main(argv[:-2] + ["--answers", bad], connect=lambda: revit, store=store) == 2
    assert "the fit-out cannot be planned: fitout:family:bed: create 'cylinder' is not 'box'" in capsys.readouterr().out


def test_drift_compares_height_host_side_facing_down_and_category():
    from plinth.plan import FamilyOp
    built = FamilyOp(op="family", key="K", category="OST_ElectricalFixtures", family="F", type="T", level="L1",
                     point=(0, 0), offset_mm=300, host_key="W1", toward=(0, 200))
    for change, said in (({"offset_mm": 900}, "offset 300 -> 900"), ({"host_key": "W2"}, "host W1 -> W2"),
                         ({"toward": (0, -200)}, "side (0.0, 200.0) -> (0, -200)"),
                         ({"toward": None}, "side (0.0, 200.0) -> None"),
                         ({"face_down": True}, "face down False -> True"),
                         ({"category": "OST_LightingDevices"}, "category OST_ElectricalFixtures -> OST_LightingDevices")):
        drift = session.check_drift({"K": built}, {"K": built.model_copy(update=change)})
        assert drift["mismatches"] == [f"K: {said}"], change
    same = session.check_drift({"K": built}, {"K": built.model_copy(update={"offset_mm": 300.4})})
    assert same == {"compared": 1, "mismatches": [], "built_not_planned_now": []}
