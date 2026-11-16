"""Self-check (PRD Step 9, T9): the read-back model against the plan and the drawings, on a small synthetic building
(registered with a rotation and a translation) whose plan comes from the real plan conversion."""

import copy
import json

import pytest

from b2m.cad.building import (Column, Evidence, Finding, FoundationRecognition, Grid, Level, Opening, PlanRecognition,
                              RecognisedBuilding, Registration, Room, SectionRecognition, Stair, Wall)
from b2m.cad.building import Column as Section
from b2m.imported import selfcheck, to_plan
from b2m.imported.selfcheck import Explanation, Tolerances, compare
from tests.selfcheck_readback import perfect_readback

P, S = "P.dxf", "S.dxf"
ANSWERS = {"door_height:D": 2100}


def ev(*handles, sheet=P):
    return Evidence(sheet=sheet, handles=list(handles))


def make_building(**changes) -> RecognisedBuilding:
    """An 8 x 6 m single-storey box (250 exterior, 125 interior walls), a column, a door, a window, two rooms, a straight
    stair, two grids; drawn in its own coordinates and registered with a 90 degree turn and a shift."""
    walls = [Wall(id="wall-1", start=(0, 0), end=(8000, 0), thickness_mm=250, kind="exterior", evidence=ev("W1")),
             Wall(id="wall-2", start=(8000, 0), end=(8000, 6000), thickness_mm=250, kind="exterior", evidence=ev("W2")),
             Wall(id="wall-3", start=(8000, 6000), end=(0, 6000), thickness_mm=250, kind="exterior", evidence=ev("W3")),
             Wall(id="wall-4", start=(0, 6000), end=(0, 0), thickness_mm=250, kind="exterior", evidence=ev("W4")),
             Wall(id="wall-5", start=(4000, 0), end=(4000, 6000), thickness_mm=125, kind="interior", evidence=ev("W5"))]
    openings = [Opening(id="opening-1", kind="door", wall_id="wall-1", center=(2000, 0), width_mm=900, mark="D",
                        mark_normalised="D", swing="in", hinge=(1550, 0), evidence=ev("D1")),
                Opening(id="opening-2", kind="window", wall_id="wall-2", center=(8000, 3000), width_mm=1200, mark="w",
                        mark_normalised="W", height_mm=1200, sill_mm=900, evidence=ev("WI1"))]
    rooms = [Room(id="room-1", name="BED ROOM", point=(2000, 3000), labelled_size_mm=(3812.5, 5750),
                  boundary=[(125, 125), (3937.5, 125), (3937.5, 5875), (125, 5875)], evidence=ev("R1")),
             Room(id="room-2", name="HALL", point=(6000, 3000),
                  boundary=[(4062.5, 125), (7875, 125), (7875, 5875), (4062.5, 5875)], evidence=ev("R2"))]
    plan = PlanRecognition(
        sheet=P, role="floor_plan", storeys=[0],
        grids=[Grid(id="grid-1", start=(0, -1000), end=(0, 7000), evidence=ev("G1")),
               Grid(id="grid-2", start=(-1000, 0), end=(9000, 0), name="A", evidence=ev("G2"))],
        walls=walls, openings=openings, rooms=rooms,
        columns=[Column(id="column-1", center=(0, 0), width_mm=250, depth_mm=250, evidence=ev("C1"))],
        stairs=[Stair(id="stair-1", kind="straight", outline=[(5000, 500), (6000, 500), (6000, 5500), (5000, 5500)],
                      width_mm=1000, tread_mm=250, risers_per_flight=[18], up_arrow=(5500, 5000), evidence=ev("ST1"))],
        outline=[(-125, -125), (8125, -125), (8125, 6125), (-125, 6125)])
    section = SectionRecognition(
        sheet=S, name="AA", datum="PL", slab_thickness_mm=150, door_heights_mm=[2100],
        levels=[Level(name="PL", elevation_mm=0, kind="plinth", storey=0, evidence=ev("L1", sheet=S)),
                Level(name="roof", elevation_mm=3000, kind="roof", evidence=ev("L2", sheet=S))])
    building = RecognisedBuilding(
        plans=[plan], sections=[section],
        registrations=[Registration(sheet=P, rotation_deg=90, translation=(1000, 2000), method="test", residual_mm=0,
                                    evidence=ev()),
                       Registration(sheet=S, frame="vertical", method="test", residual_mm=0, evidence=ev(sheet=S))])
    for path, value in changes.items():
        _set(building, path, value)
    return building


def _set(obj, path: str, value) -> None:
    *head, last = path.split(".")
    for part in head:
        obj = obj[int(part)] if part.isdigit() else getattr(obj, part)
    if last.isdigit():
        obj[int(last)] = value
    else:
        setattr(obj, last, value)


@pytest.fixture(scope="module")
def built():
    building = make_building()
    result = to_plan(building, ANSWERS)
    assert result.missing == [], [m.id for m in result.missing]
    created, readback = perfect_readback(result, building)
    return building, result, created, readback


def check(built, readback=None, created=None, **kw):
    building, result, c, rb = built
    return compare(result, building, c if created is None else created, rb if readback is None else readback,
                   answers=kw.pop("answers", ANSWERS), **kw)


def unexplained(report):
    return sorted((d.kind, d.stage, d.op_key, d.field) for d in report.differences if not d.explained)


def key_of(result, op, element):
    return next(k for k, s in result.sources.items() if s.element == element and result.plan.by_key()[k].op == op)


def test_a_perfect_build_has_no_differences(built):
    building, result, created, readback = built
    report = check(built)
    assert report.ok and report.unexplained == 0 and report.explained == 0, unexplained(report)
    assert report.accounting == {"recognised": 16, "planned": 16, "not_modelled": 0, "explained": 0, "unaccounted": 0}
    kinds = report.kinds
    assert kinds["wall"].ops == 5 and kinds["opening"].ops == 2 and kinds["room"].ops == 2 and kinds["stair"].ops == 1
    assert kinds["wall"].fields > 5 * 9  # every wall compared on every field
    assert report.elements_read == len(readback)


def test_the_registration_is_applied(built):
    building, result, created, readback = built
    wall = result.plan.by_key()[key_of(result, "wall", "wall-1")]
    assert wall.start == (1000, 2000) and wall.end == (1000, 10000)  # (x, y) -> (-y, x) + (1000, 2000)
    shifted = make_building(**{"registrations.0.translation": (1000, 2005)})
    report = compare(result, shifted, created, readback, answers=ANSWERS)
    moved = {(d.kind, d.field) for d in report.differences if d.stage == "drawing_to_plan"}
    assert moved >= {("wall", "start"), ("wall", "end"), ("column", "centre"), ("room", "placement point"),
                     ("slab", "outline corners"), ("grid", "drawn start on the grid line"), ("shaft", "outline corners")}
    assert all(d.stage == "drawing_to_plan" and d.delta == pytest.approx(5, abs=1e-6) for d in report.differences
               if d.field in ("start", "end", "centre", "placement point"))
    # nothing changed between the plan and the model
    assert not [d for d in report.differences if d.stage == "plan_to_model"]


@pytest.mark.parametrize("move, expect", [(1.0, False), (1.01, True)])
def test_position_tolerance_edge(built, move, expect):
    building, result, created, readback = built
    key = key_of(result, "wall", "wall-1")
    rb = copy.deepcopy(readback)
    rb[created[key]]["location"]["end"]["x"] += move
    report = check(built, rb)
    assert (unexplained(report) == [("wall", "plan_to_model", key, "end")]) is expect
    assert report.ok is not expect


def test_tolerances_are_configurable(built):
    building, result, created, readback = built
    key = key_of(result, "wall", "wall-1")
    rb = copy.deepcopy(readback)
    rb[created[key]]["location"]["end"]["x"] += 3
    assert not check(built, rb).ok
    assert check(built, rb, tolerances={"position_mm": 5}).ok
    assert check(built, rb, tolerances=Tolerances(position_mm=3.0)).ok


@pytest.mark.parametrize("extra, expect", [(0.109, False), (0.11, True)])  # 21.92 m2 room: 0.5 % = 0.1096 m2
def test_area_tolerance_edge(built, extra, expect):
    building, result, created, readback = built
    key = key_of(result, "room", "room-1")
    rb = copy.deepcopy(readback)
    area = rb[created[key]]["room"]["area_m2"]
    assert area == pytest.approx(3.8125 * 5.75, abs=1e-4)
    rb[created[key]]["room"]["area_m2"] = area + extra
    fields = {d.field for d in check(built, rb).differences}
    assert ("area vs drawn boundary" in fields) is expect
    assert ("area vs written size" in fields) is expect


def _perturb(readback, created, key, path, value):
    rb = copy.deepcopy(readback)
    target = rb[created[key]]
    *head, last = path.split(".")
    for part in head:
        target = target[part]
    target[last] = value(target[last]) if callable(value) else value
    return rb


CASES = [
    # (op, element, read-back path, new value, kind, field)
    ("wall", "wall-1", "wall.width_mm", 200, "wall", "thickness"),
    ("wall", "wall-1", "wall.base_level", "Roof", "wall", "base level"),
    ("wall", "wall-1", "wall.top_level", None, "wall", "top level"),
    ("wall", "wall-1", "wall.height_mm", 2990, "wall", "height"),
    ("column", "column-1", "location.rotation_deg", 45, "column", "rotation (mod 90 deg)"),
    ("column", "column-1", "frame.h_mm", 450, "column", "section h"),
    ("column", "column-1", "frame.top_level", "Ground Floor", "column", "top level"),
    ("door", "opening-1", "opening.host_id", 1, "opening", "host wall"),
    ("door", "opening-1", "opening.host_offset_mm", lambda v: v + 10, "opening", "centre along wall"),
    ("door", "opening-1", "opening.hand_flipped", lambda v: not v, "opening", "hand flipped"),
    ("door", "opening-1", "opening.width_mm", 1000, "opening", "width"),
    ("door", "opening-1", "type_mark", "D9", "opening", "mark"),
    ("window", "opening-2", "opening.sill_mm", 750, "opening", "sill"),
    ("room", "room-2", "room.name", "LIVING", "room", "name"),
    ("room", "room-2", "room.number", "9-9", "room", "number"),
    ("room", "room-2", "room.area_m2", 0, "room", "area vs drawn boundary"),
    ("floor", "outline", "floor.thickness_mm", 200, "slab", "thickness"),
    ("floor", "outline", "floor.outline", [[{"x": 0, "y": 0}, {"x": 1, "y": 0}, {"x": 1, "y": 1}]], "slab", "outline corners"),
    ("floor", "outline", "level", "Roof", "slab", "level"),
    ("stair", "stair-1", "stair.risers", 17, "stair", "risers"),
    ("stair", "stair-1", "stair.tread_mm", 280, "stair", "tread"),
    ("stair", "stair-1", "stair.run_widths_mm", [900], "stair", "run 1 width"),
    ("railing", "stair-1", "railing.host_id", 7, "railing", "host stair"),
    ("shaft", "stair-1.well", "shaft.top_offset_mm", 0, "shaft", "top offset"),
    ("grid", "grid-2", "grid.name", "B", "grid", "name"),
    ("level", "roof", "level_element.elevation_mm", 3002, "level", "elevation"),
]


@pytest.mark.parametrize("op, element, path, value, kind, field", CASES)
def test_each_kind_of_difference(built, op, element, path, value, kind, field):
    building, result, created, readback = built
    key = key_of(result, op, element)
    report = check(built, _perturb(readback, created, key, path, value))
    stage = "drawing_to_model" if field.startswith("area vs") else "plan_to_model"
    expected = [(kind, stage, field)] + ([(kind, stage, "outline area")] if field == "outline corners" else [])
    assert [(d.kind, d.stage, d.field) for d in report.differences if not d.explained] == expected
    d = next(d for d in report.differences if not d.explained)
    assert d.op_key == key and d.element_id == created[key]
    if element != "roof":  # evidence: the sheet, the recognised element and (when it has them) the drawing's handles
        assert d.sheet == P and d.element == element and (d.handles or element == "outline")


def test_missing_and_extra_elements(built):
    building, result, created, readback = built
    wall = key_of(result, "wall", "wall-5")
    window = key_of(result, "window", "opening-2")
    c = dict(created)
    rb = copy.deepcopy(readback)
    del rb[c.pop(wall)]  # never built
    rb[created[window]] = {"id": created[window], "exists": False}  # deleted since
    c["STRAY-1"] = 99  # an element the run made for no op
    rb[99] = {"id": 99, "exists": True, "category": "OST_Walls"}
    rb[98] = {"id": 98, "exists": True, "category": "OST_Walls"}  # read back but made by nobody
    report = check(built, rb, c)
    got = {(d.kind, d.field, d.op_key, d.element_id) for d in report.differences if not d.explained}
    assert got == {("wall", "presence", wall, None), ("opening", "presence", window, created[window]),
                   ("extra", "presence", "STRAY-1", 99), ("extra", "presence", None, 98)}


def test_the_stairs_own_railings_stand_for_the_railing_op(built):
    building, result, created, readback = built
    stair, railing = key_of(result, "stair", "stair-1"), key_of(result, "railing", "stair-1")
    c, rb = dict(created), copy.deepcopy(readback)
    rid = c.pop(railing)
    c[f"{stair}#2"] = rid  # Revit gave the stair its railing; the runner made none
    assert check(built, rb, c).ok
    rb[rid]["category"] = "OST_Walls"
    assert unexplained(check(built, rb, c)) == [("railing", "plan_to_model", f"{stair}#2", "category")]


def test_answers_explain_where_the_plan_departs_from_the_drawing():
    # The window mark W is drawn at two widths; the answer picks 1200: the 1000 one is an explained difference.
    building = make_building()
    plan = building.plans[0]
    plan.openings.append(Opening(id="opening-3", kind="window", wall_id="wall-4", center=(0, 3000), width_mm=1000,
                                 mark="W", mark_normalised="W", height_mm=1200, sill_mm=900, evidence=ev("WI2")))
    answers = {**ANSWERS, "window_width:W": 1200}
    result = to_plan(building, answers)
    created, readback = perfect_readback(result, building)
    report = compare(result, building, created, readback, answers=answers)
    assert report.ok and report.unexplained == 0
    [d] = report.differences
    assert (d.kind, d.stage, d.field, d.expected, d.actual) == ("opening", "drawing_to_plan", "width", 1000, 1200)
    assert d.explained and d.reason == "answered question window_width:W = 1200" and d.handles == ["WI2"]
    # without the answer on record the same difference is unexplained
    assert not compare(result, building, created, readback, answers=ANSWERS).ok


def test_documented_explanations(built):
    building, result, created, readback = built
    key = key_of(result, "door", "opening-1")
    rb = _perturb(readback, created, key, "opening.hand_flipped", lambda v: not v)
    reason = "the user flipped this door by hand after the build (issue 12)"
    report = check(built, rb, explanations=[{"reason": reason, "kind": "opening", "field": "hand flipped",
                                             "op_key": "P.dxf-opening-1-*"}])
    assert report.ok and report.explained == 1 and report.differences[0].reason == reason
    assert not check(built, rb, explanations=[Explanation(reason=reason, field="facing flipped")]).ok


def test_not_modelled_and_unaccounted_recognised_elements(built):
    building = make_building()
    building.plans[0].openings.append(Opening(id="opening-9", kind="opening", wall_id="wall-5", center=(4000, 1000),
                                              width_mm=800, evidence=ev("O9")))
    building.foundations.append(FoundationRecognition(sheet="F.dxf", column_sections=[
        Section(id="CS1", center=(0, 0), width_mm=250, depth_mm=250, evidence=ev("CS1", sheet="F.dxf")),
        Section(id="CS2", center=(0, 0), width_mm=300, depth_mm=600, evidence=ev("CS2", sheet="F.dxf"))]))
    building.registrations.append(Registration(sheet="F.dxf", method="test", residual_mm=0, evidence=ev(sheet="F.dxf")))
    result = to_plan(building, ANSWERS)
    assert ["P.dxf#opening-9"] in [n.elements for n in result.not_modelled]
    created, readback = perfect_readback(result, building)
    report = compare(result, building, created, readback, answers=ANSWERS)
    assert report.accounting == {"recognised": 19, "planned": 16, "not_modelled": 1, "explained": 1, "unaccounted": 1}
    [cs1] = [d for d in report.differences if d.explained]
    assert cs1.element == "CS1" and cs1.reason == "a typical column section, built as column type RCC Column 250x250"
    [cs2] = [d for d in report.differences if not d.explained]
    assert (cs2.kind, cs2.stage, cs2.sheet, cs2.element, cs2.handles) == (
        "recognised column_sections", "accounting", "F.dxf", "CS2", ["CS2"])


def test_section_levels_are_accounted_for(built):
    building = make_building()
    extra = [Level(name="window sill", elevation_mm=900, kind="sill", storey=0, evidence=ev("L3", sheet=S)),
             Level(name="window head", elevation_mm=2100, kind="lintel", storey=0, evidence=ev("L4", sheet=S)),
             Level(name="odd", elevation_mm=1234, kind="lintel", storey=0, evidence=ev("L5", sheet=S))]
    building.sections[0].levels += extra
    result = to_plan(building, ANSWERS)
    created, readback = perfect_readback(result, building)
    report = compare(result, building, created, readback, answers=ANSWERS)
    reasons = {d.element: d.reason for d in report.differences}
    assert reasons == {"window sill": "the sill of window type W (900 mm above storey 0's floor)",
                       "window head": "the head of window type W (2100 mm above storey 0's floor)", "odd": None}


def test_read_back_pages_and_chunks():
    class FakeRevit:
        def __init__(self):
            self.calls = []

        def call(self, command, params=None, **_):
            self.calls.append((command, len(params["element_ids"]), params["start"]))
            ids, start = params["element_ids"], params["start"]
            stop = min(start + 3000, len(ids))
            return {"elements": [{"id": i, "exists": True} for i in ids[start:stop]],
                    "next_start": stop if stop < len(ids) else None}

    revit = FakeRevit()
    out = selfcheck.read_back(revit, range(1, 6001))
    assert len(out) == 6000 and set(out) == set(range(1, 6001))
    assert revit.calls == [("read_back_elements", 5000, 0), ("read_back_elements", 5000, 3000),
                           ("read_back_elements", 1000, 0)]


def test_after_build_hook_writes_the_report(built, tmp_path):
    building, result, created, readback = built

    class FakeRevit:
        def call(self, command, params=None, **_):
            assert command == "read_back_elements"
            return {"elements": [readback[i] for i in params["element_ids"] if i in readback], "next_start": None}

    class FakeRun:
        dir = tmp_path

    hook = selfcheck.after_build_selfcheck(result, building, ANSWERS, review_url="/import#imp-1")
    out = hook(FakeRevit(), FakeRun(), created)
    assert out["selfcheck"]["ok"] is True and out["files"] == ["selfcheck.json", "selfcheck.html"]
    data = json.loads((tmp_path / "selfcheck.json").read_text(encoding="utf-8"))
    assert data["ok"] is True and data["unexplained"] == 0 and data["kinds"]["wall"]["ops"] == 5
    page = (tmp_path / "selfcheck.html").read_text(encoding="utf-8")
    assert "No unexplained differences." in page and "<td>wall</td>" in page


def test_html_groups_by_kind_with_evidence(built):
    building, result, created, readback = built
    key = key_of(result, "wall", "wall-1")
    rb = _perturb(readback, created, key, "wall.width_mm", 200)
    report = check(built, rb, review_url="/import#imp-1")
    page = selfcheck.to_html(report)
    assert "1 unexplained difference." in page
    assert "<h2>wall <small>1 unexplained, 0 explained</small></h2>" in page
    assert '<a href="/import#imp-1">P.dxf · wall-1 · storey 0</a>' in page and "handles W1" in page


def test_room_separation_lines_segment_by_segment():
    building = make_building()
    plan = building.plans[0]
    plan.findings.append(Finding(id="share-1", topic="rooms_share_space", text="one space", evidence=ev("R1", "R2")))
    answers = {**ANSWERS, f"rooms:{P}:share-1": {"mode": "separate", "lines": [[[4000, 0], [4000, 3000], [4000, 6000]]]}}
    result = to_plan(building, answers)
    key = f"{P}-share-1.1-0"
    assert result.plan.by_key()[key].op == "room_separator"
    created, readback = perfect_readback(result, building)
    assert f"{key}#2" in created
    assert compare(result, building, created, readback, answers=answers).ok
    rb = copy.deepcopy(readback)
    rb[created[f"{key}#2"]]["curve"]["end"]["y"] += 2  # building y is drawing x after the turn: still 2 mm
    c = dict(created)
    report = compare(result, building, c, rb, answers=answers)
    assert unexplained(report) == [("room_separator", "plan_to_model", f"{key}#2", "segment 2 end")]
    del rb[c.pop(f"{key}#2")]
    report = compare(result, building, c, rb, answers=answers)
    assert unexplained(report) == [("room_separator", "plan_to_model", f"{key}#2", "segment 2")]


def test_a_non_square_column_on_a_turned_sheet():
    building = make_building(**{"plans.0.columns.0.width_mm": 500})
    result = to_plan(building, ANSWERS)
    created, readback = perfect_readback(result, building)
    report = compare(result, building, created, readback, answers=ANSWERS)
    assert report.ok, unexplained(report)


def test_no_plan_no_check():
    building = make_building()
    result = to_plan(building, {})
    assert result.plan is None
    with pytest.raises(ValueError, match="nothing was built"):
        compare(result, building, {}, {})
