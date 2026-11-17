"""Recognised building -> build plan (Step 9 T6) on a small synthetic building: exact ops, the missing-input list,
the project standards, and the runner's type pre-steps."""

from collections import Counter

import pytest

from plinth.cad.building import (Column, Evidence, Finding, Level, Opening, PlanRecognition, Projection,
                              RecognisedBuilding, Registration, Room, SectionRecognition, Shaft, Stair, Wall)
from plinth.imported import ProjectStandards, to_plan
from plinth.plan import BuildPlan
from plinth.revit import commands
from plinth.revit.client import RevitError
from plinth.runner import Runner, plan_batches

PLAN, ROOF, SEC = "plan.dxf", "roof.dxf", "section.dxf"


def ev(sheet, *handles):
    return Evidence(sheet=sheet, handles=list(handles) or ["H"])


def building(move=(0.0, 0.0)) -> RecognisedBuilding:
    """Two storeys (0, 1) of one 10 x 8 m box drawn once, a roof plan with parapets, one section.
    The plan sheet is drawn ``move`` away from the building frame (the roof plan is the frame)."""
    e = lambda *h: ev(PLAN, *h)
    walls = [
        Wall(id="w1", start=(0, 0), end=(10000, 0), thickness_mm=250, kind="exterior", evidence=e("a")),
        Wall(id="w2", start=(10000, 0), end=(10000, 8000), thickness_mm=250, kind="exterior", evidence=e("b")),
        Wall(id="w3", start=(10000, 8000), end=(0, 8000), thickness_mm=250, kind="exterior", evidence=e("c")),
        Wall(id="w4", start=(0, 8000), end=(0, 0), thickness_mm=250, kind="exterior", evidence=e("d")),
        Wall(id="w5", start=(5000, 0), end=(5000, 8000), thickness_mm=125, kind="interior", evidence=e("e")),
    ]
    openings = [
        # D1 in the interior wall, hinge towards the wall's start, opening to the wall's left (+x seen from start)
        Opening(id="o1", kind="door", wall_id="w5", center=(5000, 2000), width_mm=800, mark="D1", mark_normalised="D1",
                swing="left", hinge=(5000, 1600), leaves=1, evidence=e("f")),
        Opening(id="o2", kind="window", wall_id="w1", center=(2500, 0), width_mm=1200, mark="w1", mark_normalised="W1",
                evidence=e("g")),
    ]
    rooms = [
        Room(id="r1", name="BED ROOM", point=(2500, 4000), boundary=[(0, 0), (5000, 0), (5000, 8000), (0, 8000)],
             unit="U1", evidence=e("t1")),
        Room(id="r2", name="LIVING", point=(7500, 6000), unit="U1", evidence=e("t2")),
        Room(id="r3", name="DINING", point=(7500, 2000), unit="U1", evidence=e("t3")),
    ]
    findings = [Finding(id="share-1", topic="rooms_share_space", text="share", evidence=e("t2", "t3"))]
    plan = PlanRecognition(
        sheet=PLAN, role="floor_plan", storeys=[0, 1], walls=walls, openings=openings, rooms=rooms, findings=findings,
        columns=[Column(id="c1", center=(125, 125), width_mm=500, depth_mm=250, evidence=e("h"))],
        outline=[(-125, -125), (10125, -125), (10125, 8125), (-125, 8125)],
        shafts=[Shaft(id="s1", kind="duct", outline=[(6000, 6000), (6500, 6000), (6500, 6500), (6000, 6500)], evidence=e("i"))],
        projections=[Projection(id="p1", kind="chajja", outline=[(1800, -125), (3200, -125), (3200, -600), (1800, -600)],
                                evidence=e("j"))])
    roof = PlanRecognition(sheet=ROOF, role="roof_plan", walls=[
        Wall(id="pw1", start=(0, 0), end=(10000, 0), thickness_mm=250, kind="parapet", evidence=ev(ROOF))],
        outline=[(-125, -125), (10125, -125), (10125, 8125), (-125, 8125)])
    s = ev(SEC)
    section = SectionRecognition(sheet=SEC, name="AA", datum="PL", levels=[
        Level(name="PL", elevation_mm=0, kind="plinth", storey=0, evidence=s),
        Level(name="first", elevation_mm=3000, kind="floor", storey=1, evidence=s),
        Level(name="roof", elevation_mm=6000, kind="roof", evidence=s),
        Level(name="parapet", elevation_mm=7000, kind="parapet_top", evidence=s)],
        storey_heights_mm=[3000, 3000], slab_thickness_mm=150, parapet_height_mm=1000)
    regs = [Registration(sheet=ROOF, method="frame", residual_mm=0, evidence=ev(ROOF)),
            Registration(sheet=PLAN, translation=move, method="columns", residual_mm=0, evidence=ev(PLAN)),
            Registration(sheet=SEC, frame="vertical", method="datum", residual_mm=0, evidence=s)]
    return RecognisedBuilding(plans=[plan, roof], sections=[section], registrations=regs)


ANSWERS = {
    "door_height:D1": 2100, "window_height:W1": 1200, "window_sill:W1": 900,
    "projection_thickness:chajja": 100,
    "rooms:plan.dxf:share-1": {"mode": "separate", "lines": [[[5000, 4000], [10000, 4000]]]},
}


def test_missing_inputs_list_every_value_the_drawings_lack():
    result = to_plan(building())
    assert result.plan is None
    assert [m.id for m in result.missing] == [
        "door_height:D1", "projection_thickness:chajja", "rooms:plan.dxf:share-1", "window_height:W1", "window_sill:W1"]
    door = result.missing[0]
    assert door.elements == ["plan.dxf#o1"] and door.unit == "mm"
    rooms = next(m for m in result.missing if m.topic == "rooms_share_space")
    assert rooms.elements == ["plan.dxf#r2", "plan.dxf#r3"]
    # the catalogue is known before the answers: widths and marks from the plan
    assert [(o.name, o.width_mm, o.height_mm) for o in result.standards.windows] == [("W1", 1200, None)]


def test_plan_from_answers_exact_ops():
    result = to_plan(building(move=(1000, 0)), ANSWERS)
    assert result.missing == []
    plan = result.plan
    ops = plan.by_key()
    assert [op.name for op in plan.ops if op.op == "level"] == ["Ground Floor", "Floor 1", "Roof", "Parapet Top"]
    assert Counter(op.op for op in plan.ops) == {
        "wall_type": 2, "floor_type": 2, "opening_type": 2, "level": 4, "wall": 11, "column": 2, "door": 2,
        "window": 2, "room": 6, "room_separator": 2, "floor": 5, "shaft": 1}
    # walls: storey 0 base Ground Floor, top Floor 1; moved into the building frame by the plan's registration
    w = ops["plan.dxf-w1-0"]
    assert (w.level, w.top_level, w.start, w.end, w.type) == ("Ground Floor", "Floor 1", (1000, 0), (11000, 0),
                                                               "Plinth 250mm Exterior")
    assert ops["plan.dxf-w5-1"].type == "Plinth 125mm Interior" and ops["plan.dxf-w5-1"].top_level == "Roof"
    parapet = ops["roof.dxf-pw1-roof"]
    assert (parapet.level, parapet.height_mm, parapet.top_level) == ("Roof", 1000, None)
    # door: offset from the wall start to its centre, type by mark, flips from swing and hinge
    d = ops["plan.dxf-o1-1"]
    assert (d.host_key, d.type, d.offset_mm, d.flip_facing, d.flip_hand) == ("plan.dxf-w5-1", "D1", 2000, True, True)
    win = ops["plan.dxf-o2-0"]
    assert (win.host_key, win.offset_mm, win.sill_mm) == ("plan.dxf-w1-0", 2500, 900)
    # column 500 x 250 drawn: type 250 x 500 turned 90 degrees
    c = ops["plan.dxf-c1-0"]
    assert (c.type, c.rotation_deg, c.family, c.point) == ("RCC Column 250x500", 90, "M_Concrete-Rectangular-Column",
                                                           (1125, 125))
    # slabs per floor and the roof; the chajja at the window head (900 + 1200) plus its thickness
    assert {op.key: op.level for op in plan.ops if op.op == "floor" and "outline" in op.key} == {
        "plan.dxf-outline-0": "Ground Floor", "plan.dxf-outline-1": "Floor 1", "roof.dxf-outline-roof": "Roof"}
    chajja = ops["plan.dxf-p1-1"]
    assert (chajja.offset_mm, chajja.type) == (2200, "Plinth 100mm Slab")
    # the duct cuts the slab of the storey it is drawn on (not the lowest)
    shaft = ops["plan.dxf-s1-1"]
    assert (shaft.base_level, shaft.top_level, shaft.base_offset_mm, shaft.top_offset_mm) == ("Floor 1", "Floor 1", -200, 50)
    # rooms: number <storey>-<unit>-<n>, department = unit; separate rooms with their line
    rooms = {op.key: (op.name, op.number, op.flat) for op in plan.ops if op.op == "room" and op.level == "Floor 1"}
    assert rooms == {"plan.dxf-r1-1": ("BED ROOM", "1-U1-1", "U1"), "plan.dxf-r2-1": ("LIVING", "1-U1-2", "U1"),
                     "plan.dxf-r3-1": ("DINING", "1-U1-3", "U1")}
    assert ops["plan.dxf-share-1.1-1"].points == [(6000, 4000), (11000, 4000)]
    # every op points back at what it came from
    assert result.sources["plan.dxf-w5-1"].model_dump() == {"sheet": PLAN, "element": "w5", "storey": "1", "note": None}
    assert result.counts["wall@Floor 1"] == 5 and result.counts["door@Floor 1"] == 1


def test_one_room_and_deterministic():
    answers = dict(ANSWERS, **{"rooms:plan.dxf:share-1": {"mode": "one_room"}})
    a, b = to_plan(building(), answers), to_plan(building(), answers)
    assert a.plan == b.plan
    rooms = [op for op in a.plan.ops if op.op == "room" and op.level == "Floor 1"]
    assert [r.name for r in rooms] == ["BED ROOM", "LIVING / DINING"]
    assert not any(op.op == "room_separator" for op in a.plan.ops)


def test_door_in_no_wall_and_beam_depth_are_asked():
    b = building()
    b.plans[0].openings.append(Opening(id="o3", kind="door", wall_id=None, center=(7000, 8000), width_mm=1000,
                                       hinge=(6500, 8000), swing=None, evidence=ev(PLAN, "k")))
    result = to_plan(b, ANSWERS)
    assert {m.id for m in result.missing} == {"door_height:door-1000", "opening_host:plan.dxf:o3"}
    answers = dict(ANSWERS, **{"door_height:door-1000": 2100,
                               "opening_host:plan.dxf:o3": {"mode": "new_wall", "thickness_mm": 250, "function": "interior"}})
    plan = to_plan(b, answers).plan
    wall, door = plan.by_key()["plan.dxf-o3.wall-1"], plan.by_key()["plan.dxf-o3-1"]
    assert (wall.start, wall.end, wall.type) == ((6500, 8000), (7500, 8000), "Plinth 250mm Interior")
    assert (door.host_key, door.offset_mm, door.type) == ("plan.dxf-o3.wall-1", 500, "door-1000")


def test_stair_from_plan_risers_within_the_rule_and_landing_asked():
    b = building()
    b.plans[0].stairs.append(Stair(id="st1", kind="dog_leg", width_mm=1000, tread_mm=250, risers_per_flight=[9, 9],
                                   outline=[(6000, 1000), (8200, 1000), (8200, 4500), (6000, 4500)],
                                   up_arrow=(6400, 1800), evidence=ev(PLAN, "s")))
    result = to_plan(b, ANSWERS)
    assert {m.id for m in result.missing} == {"stair_landing:plan.dxf:st1"}
    plan = to_plan(b, dict(ANSWERS, **{"stair_landing:plan.dxf:st1": "north"})).plan
    stair = plan.by_key()["plan.dxf-st1-0"]
    assert (stair.base_level, stair.top_level, stair.width_mm, stair.tread_mm) == ("Ground Floor", "Floor 1", 1000, 250)
    # the arrow head on the west flight near the entry (south) end: the west flight climbs from the landing, so the
    # first flight is the east one
    assert [(r.start, r.end, r.risers) for r in stair.runs] == [
        ((7700, 1000), (7700, 3000), 9), ((6500, 3000), (6500, 1000), 9)]
    assert plan.by_key()["plan.dxf-st1.railing-0"].stair_key == "plan.dxf-st1-0"
    # the stair cuts the slab it climbs to
    assert plan.by_key()["plan.dxf-st1.well-0"].base_level == "Floor 1"


def test_standards_persist(tmp_path):
    standards = to_plan(building(), ANSWERS).standards
    assert [(w.name, w.function, w.thickness_mm) for w in standards.walls] == [
        ("Plinth 250mm Exterior", "exterior", 250), ("Plinth 125mm Interior", "interior", 125)]
    assert [(d.name, d.width_mm, d.height_mm) for d in standards.doors] == [("D1", 800, 2100)]
    assert [(c.name, c.b_mm, c.h_mm) for c in standards.columns] == [("RCC Column 250x500", 250, 500)]
    standards.save(tmp_path)
    assert ProjectStandards.load(tmp_path) == standards


# ------------------------------------------------------------------------------------------- runner and commands

class TypeRevit:
    """Answers the type pre-steps (with a project name of its own for one wall type) and batches."""

    def __init__(self, fail_code=None):
        self.calls, self.next_id, self.fail_code = [], 100, fail_code

    def call(self, command, params=None, *, timeout_seconds=None):
        self.calls.append((command, params))
        self.next_id += 1
        if command == "ensure_family_type" and params["category"] not in ("door", "window"):
            return {"action": "existing", "type_name": params["type_name"], "family_name": params["family_name"],
                    "type_id": self.next_id}
        if command in ("ensure_wall_type", "ensure_floor_type", "ensure_family_type"):
            if self.fail_code:
                raise RevitError({"success": False, "error": f"{self.fail_code}: ask"})
            name = "Generic - 250mm" if params.get("name") == "Plinth 250mm Exterior" else params.get("name") or "900 x 2100mm D1"
            action = "existing" if name == "Generic - 250mm" else "duplicated"
            data = {"type_name": name, "type_id": self.next_id, "action": action}
            if command == "ensure_family_type":
                data["family_name"] = "M_Single-Flush" if params["category"] == "door" else "M_Fixed"
                data["type_name"] = f"{params['dimensions']['width_mm']:g} x {params['dimensions']['height_mm']:g}mm"
            return data
        if command == "batch_execute":
            results = []
            for i, sub in enumerate(params["commands"]):
                field = {"create_structural_column": "column_id", "create_structural_beam": "element_id",
                         "create_foundation_slab": "element_id"}.get(sub["command"], "elementId")
                if sub["command"] == "create_room_separator":
                    results.append({"index": i, "ok": True, "data": {"separator_ids": [self.next_id, self.next_id + 1]}})
                    self.next_id += 2
                    continue
                results.append({"index": i, "ok": True, "data": {field: self.next_id}})
                self.next_id += 1
            return {"results": results}
        if command == "analyze_model_statistics":
            return {"categories": [], "missingIds": []}
        if command == "get_model_warnings_summary":
            return {"total_warnings": 0, "warnings": []}
        raise AssertionError(command)


def test_runner_makes_types_first_and_uses_the_returned_names():
    plan = to_plan(building(), ANSWERS).plan
    batches = plan_batches(plan)
    assert all(len(b) == 1 for b in batches[:6]) and {b[0].op for b in batches[:6]} == {"wall_type", "floor_type",
                                                                                       "opening_type"}
    revit = TypeRevit()
    result = Runner(revit).build(plan)
    assert result.status == "built"
    sub = [s for c, p in revit.calls if c == "batch_execute" for s in p["commands"]]
    walls = [s["params"] for s in sub if s["command"] == "create_line_based_element"]
    assert {w["typeName"] for w in walls} == {"Generic - 250mm", "Plinth 125mm Interior"}
    doors = [s["params"] for s in sub if s["params"].get("builtInCategory") == "OST_Doors"]
    assert doors[0]["familyName"] == "M_Single-Flush" and doors[0]["typeName"] == "800 x 2100mm"
    ensure = [p for c, p in revit.calls if c == "ensure_family_type" and p["category"] == "door"]
    assert ensure[0] == {"category": "door", "dimensions": {"width_mm": 800, "height_mm": 2100}, "type_mark": "D1"}
    # the wall type that already existed is not the run's to remove; the duplicated ones are
    assert "TYPE-wall-Plinth 250mm Exterior" not in result.created and "TYPE-wall-Plinth 125mm Interior" in result.created
    assert "plan.dxf-share-1.1-1#2" in result.created  # a separator op's second line
    report = Runner(revit).verify(plan, result.created)
    stats = [p for c, p in revit.calls if c == "analyze_model_statistics"][-1]
    assert result.created["TYPE-wall-Plinth 125mm Interior"] not in stats["elementIds"]
    assert {m["category"] for m in report["mismatches"]} >= {"OST_RoomSeparationLines", "OST_Walls"}


@pytest.mark.parametrize("code", ["BASE_TYPE_NOT_FOUND", "TYPE_CHOICE_NEEDED", "FAMILY_CHOICE_NEEDED"])
def test_type_questions_stop_the_build_as_needs_input(code):
    result = Runner(TypeRevit(fail_code=code)).build(to_plan(building(), ANSWERS).plan)
    assert result.status == "needs_input" and result.failed_key.startswith("TYPE-") and result.created == {}


def test_new_op_commands():
    plan = BuildPlan.model_validate({"ops": [
        {"op": "level", "key": "L0", "name": "F", "elevation_mm": -1300},
        {"op": "floor_type", "key": "T", "name": "Plinth 500mm Foundation Slab", "thickness_mm": 500, "structural": True,
         "foundation": True},
        {"op": "foundation_slab", "key": "R", "level": "F", "type": "Plinth 500mm Foundation Slab",
         "points": [(0, 0), (10, 0), (10, 10)], "offset_mm": 50},
        {"op": "room_separator", "key": "S", "level": "F", "points": [(0, 0), (5, 0)]},
        {"op": "wall_type", "key": "W", "name": "x", "thickness_mm": 230, "function": "exterior",
         "layers": [{"function": "finish1", "thickness_mm": 15}, {"function": "structure", "thickness_mm": 200},
                    {"function": "finish2", "thickness_mm": 15}]},
    ]})
    by = plan.by_key()
    assert commands.to_command(by["R"], by, {}) == {"command": "create_foundation_slab", "params": {
        "level": "F", "points": [[0, 0], [10, 0], [10, 10]], "type_name": "Plinth 500mm Foundation Slab",
        "height_offset_mm": 50}}
    assert commands.to_command(by["T"], by, {})["params"] == {
        "thickness_mm": 500, "name": "Plinth 500mm Foundation Slab", "structural": True, "foundation": True}
    assert commands.to_command(by["S"], by, {}) == {"command": "create_room_separator", "params": {
        "points": [{"x": 0, "y": 0}, {"x": 5, "y": 0}], "level_name": "F", "close_loop": False}}
    assert commands.to_command(by["W"], by, {})["params"]["layers"][1] == {"function": "structure", "thickness_mm": 200}
    with pytest.raises(ValueError, match="add up"):
        BuildPlan.model_validate({"ops": [{"op": "wall_type", "key": "W", "name": "x", "thickness_mm": 250,
                                           "function": "interior", "layers": [{"function": "structure", "thickness_mm": 200}]}]})
    with pytest.raises(ValueError, match="not a foundation slab type"):
        BuildPlan.model_validate({"ops": [
            {"op": "level", "key": "L0", "name": "F", "elevation_mm": 0},
            {"op": "floor_type", "key": "T", "name": "slab", "thickness_mm": 150},
            {"op": "foundation_slab", "key": "R", "level": "F", "type": "slab", "points": [(0, 0), (1, 0), (1, 1)]}]})
