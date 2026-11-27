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


# ------------------------------------------------------------------------------------------------ joins (MEP)

MEP_TEMPLATES = {**TEMPLATES, PLU: {**TEMPLATES[PLU], "mep_types": {
    "pipe_type": [{"id": 31, "name": "PVC - DWV"}],
    "pipe_system_type": [{"id": 32, "name": "Sanitary", "classification": "Sanitary"},
                         {"id": 34, "name": "Domestic Cold Water", "classification": "DomesticColdWater"}]}},
    RES: {"family_types": TEMPLATES[RES]["family_types"] + [
        {"id": 42, "category": "OST_ElectricalEquipment", "family": "M_Distribution Board", "type": "8 way",
         "placement": "OneLevelBased", "dims_mm": {}}]}}


def toilet_mep():
    """A toilet's water and drain, a geyser and the flat's board: the cold water to the WC, the basin's waste teed
    into a stack, the geyser on a circuit of the board (records as mep_plan makes them)."""
    return {
        "devices": [
            {"key": "MEP-DB", "level": "L0", "item": "distribution_board", "label": "Distribution board",
             "category": "Electrical Equipment", "keywords": ["distribution board"], "point": (300, 300),
             "elevation_mm": 1500, "panel": True, "source": "electrical"},
            {"key": "MEP-GEYSER", "level": "L0", "room_key": "TLT", "item": "geyser", "label": "Geyser",
             "category": "Mechanical Equipment", "keywords": ["water heater"], "point": (6000, 2400),
             "elevation_mm": 1800, "width_mm": 400, "depth_mm": 300, "height_mm": 600, "source": "plumbing"}],
        "runs": [
            {"key": "RUN-STACK", "kind": "pipe", "system": "Sanitary", "level": "L0", "diameter_mm": 100,
             "points": [(6600, 300, -300), (6600, 300, 3000)]},
            {"key": "RUN-WASTE", "kind": "pipe", "system": "Sanitary", "level": "L0", "diameter_mm": 40,
             "start_key": "FIT-TLT-basin-1", "end_key": "RUN-STACK",
             "points": [(5300, 300, 400), (6500, 300, 400)]},
            {"key": "RUN-COLD", "kind": "pipe", "system": "DomesticColdWater", "level": "L0", "diameter_mm": 15,
             "end_key": "FIT-TLT-wc-1", "points": [(6600, 500, 2800), (5500, 500, 2800), (5500, 500, 300)]},
            {"key": "RUN-VENT", "kind": "pipe", "system": "Sanitary", "level": "L0", "diameter_mm": 50,
             "start_key": "RUN-STACK", "points": [(6600, 300, 2900), (6600, 300, 3400)]}],
        "circuits": [{"key": "CKT-1", "device_keys": ["MEP-GEYSER"], "panel_key": "MEP-DB"}]}


def plan_toilet(revit, building, built, wb_rules, **kw):
    layout = furniture.solve(rooms.build(building, wb_rules), wb_rules)
    answers = {"fitout:family:wardrobe": "skip", "fitout:family:side_table": "skip", "fitout:family:wc": {"create": "box"},
               "fitout:family:geyser": {"create": "box"}}
    return to_plan.plan_fitout(revit, building, built, furniture=layout, mep=toilet_mep(), answers=answers, **kw)


def test_runs_to_families_join_best_effort_and_box_families_get_their_connectors(tmp_path, building, wb_rules):
    revit = FitoutRevit(templates=MEP_TEMPLATES, unjoinable={"OST_PlumbingFixtures", "OST_MechanicalEquipment"},
                        unpowered={"OST_MechanicalEquipment"})
    store = RunStore(tmp_path)
    _, built = built_run(store, building, revit)
    found = plan_toilet(revit, building, built, wb_rules)
    assert found.ready, found.questions
    by_key = found.plan.by_key()
    # every fit-out run is best effort, even one joining only runs: an elbow or tee a real type's routing preferences
    # lack at a size is an issue of the build, never a stop of thousands of ops
    assert [by_key[k].require_connections for k in ("RUN-STACK", "RUN-WASTE", "RUN-COLD", "RUN-VENT")] == [
        False, False, False, False]
    assert by_key["CKT-1"].best_effort
    boxes = {op.family: op.connectors for op in found.plan.ops if op.op == "family_create"}
    assert {f: [c.model_dump(exclude_none=True) for c in cs] for f, cs in boxes.items()} == {
        "Plinth Geyser": [{"domain": "electrical", "system_type": "PowerCircuit", "face": "back"}],
        "Plinth Water closet": [{"domain": "pipe", "classification": "DomesticColdWater", "diameter_mm": 15.0, "face": "back"}]}
    record = BuildService(store, lambda: revit, types=TypeMap()).build_now(found.plan, revit.title)
    # the WC and geyser boxes joined by their own connectors; the template basin has none: its waste stands, unjoined
    assert record["status"] == "built_with_issues", record
    assert [(i["key"], i["what"], i["at"]) for i in record["issues"]] == [("RUN-WASTE", "connection", "start")]
    assert record["readback"]["ok"]
    assert BuildService(store, lambda: revit).remove(record["run_id"])["status"] == "removed"


def test_box_power_connectors_are_made_at_the_supply_voltage(building, wb_rules):
    from plinth.fitout.to_plan import supply_voltage
    assert supply_voltage(wb_rules) == 230.0  # india-nbc2016's supply, inherited by west-bengal
    revit = FitoutRevit(templates=MEP_TEMPLATES)
    built = {op.key: 70000 + i for i, op in enumerate(building.ops)}
    found = plan_toilet(revit, building, built, wb_rules, voltage_v=supply_voltage(wb_rules))
    geyser = next(op for op in found.plan.ops if op.op == "family_create" and op.family == "Plinth Geyser")
    assert [c.model_dump(exclude_none=True) for c in geyser.connectors] == [
        {"domain": "electrical", "system_type": "PowerCircuit", "voltage_v": 230.0, "face": "back"}]


def test_a_mep_layout_given_as_it_is_is_made_into_records(building, wb_rules):
    from plinth.fitout.mepcore import MepLayout, Run
    from plinth.fitout import mep_plan
    revit = FitoutRevit(templates=MEP_TEMPLATES)
    built = {op.key: 70000 + i for i, op in enumerate(building.ops)}
    layout = MepLayout(rule_set="x", runs=[Run(key="RUN-STACK", kind="pipe", system="soil", level="L0", size_mm=100,
                                              points=[(6600, 300, -300), (6600, 300, 3000)], source="test")])
    as_layout = to_plan.plan_fitout(revit, building, built, mep=layout).plan.by_key()["RUN-STACK"]
    as_records = to_plan.plan_fitout(revit, building, built, mep=mep_plan.records(layout, to_plan.levels_of(building)))
    assert as_layout == as_records.plan.by_key()["RUN-STACK"]
    assert as_layout.points == [(6600, 300, -300), (6600, 300, 3000)] and as_layout.diameter_mm == 100


def test_level_based_wall_and_ceiling_devices_are_placed_by_their_box_not_their_origin():
    # 2026-11-27 review: a wall device's elevation is its centre and a ceiling device's the ceiling; a level-based family
    # stands on its origin, so taken as the offset a 320 mm AC indoor unit stood 160 mm too high and a fan above the
    # ceiling
    from plinth.fitout.families import Choice, Measurement
    plain = Choice(need="ac", category="OST_MechanicalEquipment", family="Unit", type="T", source="project",
                   placement="OneLevelBased")
    wall = {"key": "AC", "level": "L0", "point": (1000, 0), "elevation_mm": 2260, "host_kind": "wall",
            "center_mm": 2260, "wall_key": "bS", "toward": (1000, 200), "height_mm": 320}
    op, note = to_plan._family_op(wall, plain, set(), {})
    assert op["offset_mm"] == 2100 and "origin taken as its base" in note
    # measured: its box (300 high, its bottom 10 mm below its origin) centred on 2260
    measured = plain.model_copy(update={"measured": Measurement(type="T", min=(-400, -200, -10), max=(400, 0, 290))})
    op, note = to_plan._family_op(wall, measured, set(), {})
    assert op["offset_mm"] == 2120 and note is None
    # an outdoor unit: the layout's elevation is its bottom, its centre what the adapter gives
    outdoor = {**wall, "host_kind": "outside_wall", "elevation_mm": 1500, "center_mm": 1800, "height_mm": 600}
    assert to_plan._family_op(outdoor, plain, set(), {})[0]["offset_mm"] == 1500
    unknown = {k: v for k, v in wall.items() if k != "height_mm"}
    op, note = to_plan._family_op(unknown, plain, set(), {})
    assert op["offset_mm"] == 2260 and "height not known" in note
    # a level-based fan or light hangs with its top at the ceiling
    fan = {"key": "FAN", "level": "L0", "point": (1000, 1000), "elevation_mm": 2550, "host_kind": "ceiling",
           "face_down": True, "height_mm": 300}
    op, note = to_plan._family_op(fan, plain, set(), {})
    assert op["offset_mm"] == 2250 and not op.get("face_down") and "not facing down" in note
    op, note = to_plan._family_op(fan, plain.model_copy(update={"measured": Measurement(
        type="T", min=(-600, -600, -250), max=(600, 600, 0))}), set(), {})
    assert op["offset_mm"] == 2550 and "not facing down" in note  # drawn hanging below its origin
    # furniture records are not MEP devices: as before
    sofa = {"key": "S", "level": "L0", "point": (0, 0), "elevation_mm": 0}
    assert "offset_mm" not in to_plan._family_op(sofa, plain, set(), {})[0]


def test_prune_drops_what_joins_a_record_that_is_gone_and_the_types_nobody_uses(building, wb_rules):
    revit = FitoutRevit(templates=MEP_TEMPLATES)
    built = {op.key: 70000 + i for i, op in enumerate(building.ops)}
    plan = plan_toilet(revit, building, built, wb_rules).plan
    out = to_plan.prune(plan, drop={"FIT-TLT-wc-1", "RUN-STACK", "MEP-GEYSER"}, why="not in the pilot")
    assert out.dropped == {"FIT-TLT-wc-1": "not in the pilot", "MEP-GEYSER": "not in the pilot",
                           "RUN-STACK": "not in the pilot",
                           "RUN-WASTE": "it joins RUN-STACK, which is left out",
                           "RUN-COLD": "it joins FIT-TLT-wc-1, which is left out",
                           "RUN-VENT": "it joins RUN-STACK, which is left out",
                           "CKT-1": "every device of it is left out"}
    kinds = {op.op for op in out.plan.ops}
    assert "mep_run" not in kinds and "circuit" not in kinds
    assert not [op for op in out.plan.ops if op.op == "family_create"]  # both boxes' instances are gone
    items = [(i.kind, i.name) for op in out.plan.ops if op.op == "types_from_file" for i in op.items]
    assert not [i for i in items if i[0].startswith("pipe")]  # no run uses a pipe type or system any more
    assert set(out.plan.existing) == {"tW"}  # the basin's wall; nothing else is referred to
    # a circuit only loses a device that is gone
    trimmed = to_plan.prune(BuildPlan.model_validate({**plan.model_dump(mode="json"), "ops": [
        *plan.model_dump(mode="json")["ops"],
        {"op": "circuit", "key": "CKT-2", "device_keys": ["MEP-GEYSER", "FIT-TLT-basin-1"], "panel_key": "MEP-DB"}]}),
        drop={"MEP-GEYSER"})
    assert trimmed.trimmed == {"CKT-2": ["MEP-GEYSER"]} and trimmed.plan.by_key()["CKT-2"].device_keys == [
        "FIT-TLT-basin-1"]
    assert to_plan.prune(plan, drop={op.key for op in plan.ops}).plan is None


def test_on_an_earlier_fitout_run_its_records_are_joined_not_placed_again(building, wb_rules):
    revit = FitoutRevit(templates=MEP_TEMPLATES)
    built = {op.key: 70000 + i for i, op in enumerate(building.ops)}
    full = plan_toilet(revit, building, built, wb_rules).plan
    furniture_keys = [op.key for op in full.ops if op.op == "family" and op.key.startswith("FIT-")]
    earlier = {k: 80000 + i for i, k in enumerate(furniture_keys)}
    found = plan_toilet(revit, building, built, wb_rules, built_fitout=earlier)
    plan = found.plan
    assert not set(earlier) & set(plan.by_key())
    assert plan.existing == {"FIT-TLT-basin-1": earlier["FIT-TLT-basin-1"], "FIT-TLT-wc-1": earlier["FIT-TLT-wc-1"]}
    assert plan.by_key()["RUN-COLD"].end_key == "FIT-TLT-wc-1"
    # no type op for what the earlier run placed: the WC box family is that run's, the bed and desk copies too
    assert {op.family for op in plan.ops if op.op == "family_create"} == {"Plinth Geyser"}
    assert not [op for op in plan.ops if op.op == "family_type_copy"]
    assert set(found.already_built) == set(earlier)
    assert found.already_built["FIT-TLT-wc-1"] == full.by_key()["FIT-TLT-wc-1"]
    record = Runner(FitoutRevit(templates=MEP_TEMPLATES, elements={
        **{i: {"category": "OST_Walls", "level": "L0"} for i in built.values()},
        **{i: {"category": "OST_PlumbingFixtures", "level": "L0"} for i in earlier.values()}})).build(plan)
    assert record.status == "built", record.error
