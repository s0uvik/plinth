"""Fit-out layout -> build plan -> its own run (F5/F6): a small built plan (a bedroom and a toilet) is furnished by the
rules, its families matched (project first, then a template; a size copy; a question answered "skip"), built as a
run on top of the building's run, read back and removed: only what the fit-out made goes; the building and the types
the project already had stay."""

import pytest

from plinth.builds import BuildService
from plinth.fitout import furniture, rooms, to_plan
from plinth.plan import BuildPlan
from plinth.revit.types import TypeMap
from plinth.runner import Runner, plan_batches
from plinth.runs import RunStore
from tests.fitout.conftest import box_walls, plan_of
from tests.fitout.fake_revit import FitoutRevit

RES = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Residential-Default_Metric.rte"
PLU = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Plumbing-Default_Metric.rte"
TEMPLATES = {
    RES: {"family_types": [
        {"id": 11, "category": "OST_Furniture", "family": "M_Bed-Standard", "type": "King 1830x2032mm",
         "placement": "OneLevelBased", "dims_mm": {"width": 1830, "length": 2032}},
        {"id": 12, "category": "OST_Furniture", "family": "M_Desk", "type": "1200 x 600mm", "placement": "OneLevelBased",
         "dims_mm": {"width": 1200, "depth": 600}},
        {"id": 13, "category": "OST_Furniture", "family": "M_Chair-Breuer", "type": "Chair-Breuer",
         "placement": "OneLevelBased", "dims_mm": {}}]},
    PLU: {"family_types": [
        {"id": 21, "category": "OST_PlumbingFixtures", "family": "M_Water Closet-Flush Tank", "type": "Standard",
         "placement": "OneLevelBased", "dims_mm": {}},
        {"id": 22, "category": "OST_PlumbingFixtures", "family": "M_Lavatory-Wall Hung", "type": "560x430mm",
         "placement": "WorkPlaneBased", "dims_mm": {}}]},
}


@pytest.fixture
def building():
    return plan_of(*box_walls("b", 0, 0, 3800, 4200),
                   {"op": "door", "key": "DB", "host_key": "bS", "type": "Door 900x2100", "offset_mm": 650,
                    "flip_facing": True},
                   {"op": "room", "key": "BED", "level": "L0", "point": (1900, 2100), "name": "Bedroom"},
                   *box_walls("t", 5000, 0, 6700, 2600),
                   {"op": "door", "key": "DT", "host_key": "tS", "type": "Door 750x2100", "offset_mm": 850,
                    "flip_facing": True},
                   {"op": "room", "key": "TLT", "level": "L0", "point": (5850, 1300), "name": "Toilet"})


def built_run(store: RunStore, building: BuildPlan, revit: FitoutRevit) -> tuple[str, dict[str, int]]:
    """The building's run, as BuildService leaves it: its plan, what it created (in the fake document) and its title."""
    created = {}
    for op in building.ops:
        element_id = 70000 + len(created)
        created[op.key] = element_id
        revit.elements[element_id] = {"category": {"wall": "OST_Walls", "door": "OST_Doors", "room": "OST_Rooms",
                                                   "level": "OST_Levels"}[op.op], "level": getattr(op, "level", None)}
    run = store.create()
    run.save("plan", building.model_dump(mode="json"))
    run.update(status="built", created=created, document_title=revit.title)
    return run.id, created


def test_a_furnished_plan_is_built_read_back_and_removed_as_its_own_run(tmp_path, building, wb_rules):
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "M_Table-Night Stand", "type": "450 x 450mm",
                                "dims": {"width": 450, "depth": 450}}], templates=TEMPLATES)
    store = RunStore(tmp_path)
    building_run, built = built_run(store, building, revit)
    layout = furniture.solve(rooms.build(building, wb_rules), wb_rules)
    assert {p.item for p in layout.placed} == {"bed", "wardrobe", "side_table", "study_table", "wc", "basin"}

    # 1. Planned: the wardrobe has no family anywhere: asked, and held back; everything else is in the plan.
    first = to_plan.plan_fitout(revit, building, built, furniture=layout)
    assert not first.ready
    (question,) = first.questions
    assert question.id == "fitout:family:wardrobe-1200x600" and question.instances == ["FIT-BED-wardrobe-1"]
    assert [(x.key, x.reason) for x in first.left_out] == [
        ("FIT-BED-wardrobe-1", "waiting for a family type (fitout:family:wardrobe-1200x600)")]

    # 2. Answered (leave it out): ready.
    found = to_plan.plan_fitout(revit, building, built, furniture=layout, answers={question.id: "skip"})
    assert found.ready and found.questions == []
    assert [x.reason for x in found.left_out] == ["left out by the user's answer"]
    plan = found.plan
    by_key = plan.by_key()
    assert plan.existing_levels == {"L0": 0.0} and plan.existing == {"tW": built["tW"]}  # the basin's wall only
    # Types: one copy per template (only the types used), then the bed and desk copied to their sizes.
    assert [op.key for op in plan.ops if op.op == "types_from_file"] == [
        "FIT-TYPES-PLUMBING-DEFAULT-METRIC", "FIT-TYPES-RESIDENTIAL-DEFAULT-METRIC"]
    assert [(i.family, i.name) for i in by_key["FIT-TYPES-RESIDENTIAL-DEFAULT-METRIC"].items] == [
        ("M_Bed-Standard", "King 1830x2032mm"), ("M_Desk", "1200 x 600mm")]
    copies = {op.family: (op.base_type, op.name, op.values_mm) for op in plan.ops if op.op == "family_type_copy"}
    assert copies == {"M_Bed-Standard": ("King 1830x2032mm", "1500x2000mm (Plinth)", {"Width": 1500.0}),
                      "M_Desk": ("1200 x 600mm", "1000x600mm (Plinth)", {"Width": 1000.0})}
    bed, table = by_key["FIT-BED-bed-1"], by_key["FIT-BED-side_table-1"]
    assert (bed.type, bed.point, bed.rotation_deg, bed.host_key) == ("1500x2000mm (Plinth)", (3700.0, 2100.0), -90.0, None)
    assert (table.family, table.type) == ("M_Table-Night Stand", "450 x 450mm")  # the project's own
    basin = by_key["FIT-TLT-basin-1"]  # face-based: on the toilet's wall, on the room's side
    assert (basin.host_key, basin.toward, basin.rotation_deg) == ("tW", (5325.0, 925.0), 0)
    assert {a.id for a in found.assumptions} >= {"fitout:family-copy:bed-1500x2000",
                                                 "fitout:family-size:wc-400x700",
                                                 "fitout:family-source:basin-550x450"}
    assert all(op.source for op in plan.ops if op.op == "family")
    assert [len(b) for b in plan_batches(plan)] == [1, 1, 1, 1, 5]

    # 3. Built as its own run, read back.
    before = dict(revit.elements)
    service = BuildService(store, lambda: revit, types=TypeMap())
    record = service.build_now(plan, revit.title)
    assert record["status"] == "built", record
    assert record["readback"]["ok"] and record["readback"]["elements"] == 5
    created = record["created"]
    assert not set(created.values()) & set(built.values())  # nothing of the building's run is the fit-out's
    night_stand = next(i for i, e in before.items() if e.get("type") == "450 x 450mm")
    assert night_stand not in created.values()
    placed = revit.elements[created["FIT-TLT-basin-1"]]
    assert placed["host"] == built["tW"]

    # 4. Removed as a whole: exactly what it made goes; the building and the project's own types stay.
    removed = service.remove(record["run_id"])
    assert removed["status"] == "removed", removed
    assert revit.elements == before
    assert store.get(building_run).load("run")["status"] == "built"


def test_mep_devices_runs_and_circuits_join_the_plan(building, wb_rules):
    templates = {**TEMPLATES, PLU: {**TEMPLATES[PLU], "mep_types": {
        "pipe_type": [{"id": 31, "name": "PVC - DWV"}],
        "pipe_system_type": [{"id": 32, "name": "Sanitary", "classification": "Sanitary"}],
        "conduit_type": [{"id": 33, "name": "RNC Sch 40"}]}}}
    templates[RES] = {"family_types": TEMPLATES[RES]["family_types"] + [
        {"id": 41, "category": "OST_LightingFixtures", "family": "M_Downlight", "type": "100W",
         "placement": "WorkPlaneBased", "dims_mm": {}},
        {"id": 42, "category": "OST_ElectricalEquipment", "family": "M_Distribution Board", "type": "8 way",
         "placement": "OneLevelBased", "dims_mm": {}}]}
    revit = FitoutRevit(templates=templates)
    built = {op.key: 70000 + i for i, op in enumerate(building.ops)}
    mep = {
        "devices": [
            {"key": "MEP-L1", "level": "L0", "item": "light", "label": "Ceiling light", "category": "Lighting Fixtures",
             "keywords": ["downlight", "light"], "point": (1900, 2100), "elevation_mm": 2900, "face_down": True,
             "source": "electrical: points per room"},
            {"key": "MEP-DB", "level": "L0", "item": "db", "label": "Distribution board", "category": "Electrical Equipment",
             "keywords": ["distribution board", "panel"], "point": (300, 300), "elevation_mm": 1500,
             "source": "electrical: one DB per flat"},
            {"key": "MEP-X", "level": "L0", "item": "fan", "label": "Exhaust fan", "category": "Mechanical Equipment",
             "keywords": ["exhaust fan"], "point": (6000, 2000), "elevation_mm": 2200}],
        "runs": [
            {"key": "MEP-SOIL", "kind": "pipe", "system": "Sanitary", "level": "L0", "diameter_mm": 100,
             "points": [(6600, 100, 3000), (6600, 100, -300)], "source": "plumbing: soil stack"},
            {"key": "MEP-CONDUIT", "kind": "conduit", "level": "L0", "diameter_mm": 20, "start_key": "MEP-DB",
             "end_key": "MEP-L1", "points": [(300, 300, 1500), (300, 300, 2900), (1900, 2100, 2900)]},
            {"key": "MEP-FANWIRE", "kind": "conduit", "level": "L0", "diameter_mm": 20, "start_key": "MEP-DB",
             "end_key": "MEP-X", "points": [(300, 300, 1500), (6000, 2000, 2200)]}],
        "circuits": [{"key": "MEP-C1", "device_keys": ["MEP-L1", "MEP-X"], "panel_key": "MEP-DB"}],
    }
    found = to_plan.plan_fitout(revit, building, built, mep=mep,
                                answers={"fitout:family:fan": "skip"})
    by_key = found.plan.by_key()
    soil = by_key["MEP-SOIL"]
    assert (soil.type, soil.system_type, soil.points[1]) == ("PVC - DWV", "Sanitary", (6600, 100, -300))
    assert by_key["MEP-CONDUIT"].start_key == "MEP-DB" and by_key["MEP-L1"].face_down
    assert by_key["MEP-C1"].device_keys == ["MEP-L1"]  # the fan is left out: the circuit goes without it
    assert {x.key for x in found.left_out} == {"MEP-X", "MEP-FANWIRE"}
    built_fitout = Runner(revit).build(found.plan)
    assert built_fitout.status == "built", built_fitout.error
    assert Runner(revit).verify(found.plan, built_fitout.created)["ok"]
