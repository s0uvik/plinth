"""The self-check (T9) on the G+4 sample: the plan made with answers-test.json, a read-back of a model built exactly
as planned, then a few values changed in that read-back.

A perfect build leaves no difference between plan and model, nor between drawing and plan beyond what the answers
explain, and nothing is unaccounted: the 14 roof-plan centre lines that lie on none of the floor plans' grid lines
(100, 87.5, 125 and 900 mm off them) are listed in not_modelled with their reason.
"""

import copy
import json
from collections import Counter

import pytest

from b2m.cad import structure
from b2m.cad.recognise import recognise_plan
from b2m.imported import selfcheck, to_plan
from tests.cad.conftest import SAMPLE
from tests.selfcheck_readback import perfect_readback

TYPICAL, ROOF = "2.PLAN 1st to 4th.dxf", "3.ROOF PLAN.dxf"
ROOF_LINES_OFF_GRID = [f"grid-{n}" for n in (1, 3, 6, 10, 13, 15, 16, 18, 19, 21, 23, 24, 27, 29)]


@pytest.fixture(scope="module")
def sample(sample_sheets):
    building = structure.recognise_set(list(sample_sheets.values()), recognise_plan)
    answers = {k: v for k, v in json.loads((SAMPLE / "answers-test.json").read_text(encoding="utf-8")).items()
               if not k.startswith("_")}
    result = to_plan(building, answers)
    assert result.plan is not None
    created, readback = perfect_readback(result, building)
    return building, answers, result, created, readback


def unexplained(report):
    return [d for d in report.differences if not d.explained]


def test_a_perfect_build_of_the_sample(sample):
    building, answers, result, created, readback = sample
    report = selfcheck.compare(result, building, created, readback, answers=answers)
    # every op but the 20 type pre-steps (1332 with 16 kitchen rooms and their 16 separation lines)
    assert report.elements_read == len(readback) == 1364 - 20
    # plan -> model and drawing -> model: nothing; drawing -> plan: only what the answers changed
    assert [d for d in report.differences if d.stage in ("plan_to_model", "drawing_to_model")] == []
    explained = Counter((d.kind, d.field, d.reason.split(" = ")[0]) for d in report.differences
                        if d.stage == "drawing_to_plan")
    assert all(d.explained for d in report.differences if d.stage == "drawing_to_plan")
    assert explained == {
        ("opening", "width", "answered question door_width:D1"): 16,  # D1 drawn 900 wide 4x per storey, answered 800
        ("room", "name", f"answered question rooms:{TYPICAL}:rooms-share-space-1"): 4,
        ("room", "name", f"answered question rooms:{TYPICAL}:rooms-share-space-2"): 4,
        ("room", "name", f"answered question rooms:{TYPICAL}:rooms-share-space-3"): 4,
        ("room", "name", f"answered question rooms:{TYPICAL}:rooms-share-space-4"): 4,
        ("room", "name", f"answered question rooms:{TYPICAL}:rooms-share-space-5"): 4,
    }
    # accounting: every recognised element is planned, listed as not modelled, or accounted for by a rule
    # the 4 kitchen labels are built as rooms of their own: planned, no longer accounted for as merged
    assert report.accounting == {"recognised": 641, "planned": 553, "not_modelled": 27, "explained": 61,
                                 "unaccounted": 0}
    rules = Counter(d.kind for d in report.differences if d.stage == "accounting" and d.explained)
    assert rules == {"recognised grids": 32, "recognised levels": 16, "recognised rooms": 5,
                     "recognised section_marks": 6, "recognised column_sections": 2}
    merged = {d.element: d.reason for d in report.differences
              if d.stage == "accounting" and d.kind == "recognised rooms"}
    # each flat's living room label is part of its living/dining room, never of the kitchen
    assert merged["room-10"].startswith("one room with room-7, built from room-7")
    assert len(merged) == 5 and not {"room-1", "room-6", "room-33", "room-34"} & set(merged)  # kitchens are built
    assert unexplained(report) == []
    off_grid = next(n for n in result.not_modelled if n.what == "roof-plan centre lines off the building grid")
    assert sorted(off_grid.elements) == sorted(f"{ROOF}#{g}" for g in ROOF_LINES_OFF_GRID)


def test_changed_values_are_exactly_the_differences(sample):
    building, answers, result, created, readback = sample
    by_key = result.plan.by_key()
    wall = f"{TYPICAL}-wall-12-3"
    door = next(k for k, op in by_key.items() if op.op == "door" and k.endswith("-2"))
    room = next(k for k, op in by_key.items() if op.op == "room" and result.sources[k].element == "room-2"
                and k.endswith("-1"))
    rb = copy.deepcopy(readback)
    rb[created[wall]]["location"]["end"]["x"] += 5
    rb[created[door]]["opening"]["hand_flipped"] = not rb[created[door]]["opening"]["hand_flipped"]
    rb[created[room]]["room"]["area_m2"] += 0.5
    rb[created["LEVEL-Floor 2"]]["level_element"]["elevation_mm"] += 2
    report = selfcheck.compare(result, building, created, rb, answers=answers)
    new = [d for d in unexplained(report) if d.stage != "accounting"]
    assert {(d.op_key, d.field): d.delta for d in new} == {
        (wall, "end"): 5.0, (door, "hand flipped"): None, (room, "area vs drawn boundary"): 0.5,
        (room, "area vs written size"): 0.493,  # drawn 13.993 m2 inside, written 3500 X 4000
        ("LEVEL-Floor 2", "elevation"): 2.0}
    assert len(new) == 5
    assert {d.field: d.severity for d in new if d.op_key == room} == {"area vs drawn boundary": "error",
                                                                       "area vs written size": "warning"}
    w = next(d for d in new if d.op_key == wall)
    assert (w.sheet, w.element, w.storey, w.element_id) == (TYPICAL, "wall-12", "3", created[wall]) and w.handles


def test_a_shaft_outline_sent_as_one_flat_loop_reads_the_same(sample):
    """Add-ins before 2026-11-16 sent a shaft outline as a flat list of points, not a list of loops."""
    building, answers, result, created, readback = sample
    rb = copy.deepcopy(readback)
    shafts = [k for k, op in result.plan.by_key().items() if op.op == "shaft"]
    for key in shafts:
        rb[created[key]]["shaft"]["outline"] = rb[created[key]]["shaft"]["outline"][0]
    report = selfcheck.compare(result, building, created, rb, answers=answers)
    assert shafts and unexplained(report) == []


def test_a_separation_line_lengthened_into_its_wall_is_explained_but_not_one_moved_sideways(sample):
    """Live 2026-11-17: Revit lengthened kitchen separation lines 38.7 mm along themselves into the wall they meet."""
    building, answers, result, created, readback = sample
    sep = next(op for op in result.plan.ops if op.op == "room_separator")
    (ax, ay), (bx, by) = sep.points[0], sep.points[1]
    length = ((bx - ax) ** 2 + (by - ay) ** 2) ** 0.5
    ux, uy = (ax - bx) / length, (ay - by) / length  # outward at the start
    for shift, explained in (((ux * 38.7, uy * 38.7), True), ((-uy * 38.7, ux * 38.7), False)):
        rb = copy.deepcopy(readback)
        curve = rb[created[sep.key]].get("curve") or rb[created[sep.key]]["location"]
        curve["start"]["x"] += shift[0]
        curve["start"]["y"] += shift[1]
        report = selfcheck.compare(result, building, created, rb, answers=answers)
        d = next(d for d in report.differences if d.op_key == sep.key and d.field == "segment 1 start")
        assert d.explained is explained
