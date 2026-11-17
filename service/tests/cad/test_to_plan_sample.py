"""The G+4 sample through plan recognition (T3), sections/foundation/registration (T4) and the plan conversion (T6).

Without answers the conversion refuses with the list of values the drawings lack; with the test-only answers in
answers-test.json it gives a plan whose op counts per kind and level are pinned here. Expected counts follow the
recognition tests' pinned figures (54 walls, 56 columns, 37 windows, 34 room labels per typical storey).
"""

import copy
import json

import pytest
from shapely.geometry import LineString, Point, Polygon

from plinth.cad import structure
from plinth.cad.recognise import recognise_plan
from plinth.imported import split_space, to_plan
from tests.cad.conftest import SAMPLE

TYPICAL, GROUND, TIE = "2.PLAN 1st to 4th.dxf", "1.GROUND FLOOR PLAN.dxf", "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf"


@pytest.fixture(scope="module")
def building(sample_sheets):
    return structure.recognise_set(list(sample_sheets.values()), recognise_plan)


@pytest.fixture(scope="module")
def answers():
    data = json.loads((SAMPLE / "answers-test.json").read_text(encoding="utf-8"))
    return {k: v for k, v in data.items() if not k.startswith("_")}


def test_without_answers_the_missing_inputs(building):
    result = to_plan(building)
    assert result.plan is None
    assert [m.id for m in result.missing] == [
        f"beam_depth:{TIE}", "boundary_wall_height",
        "door_height:D", "door_height:D1", "door_height:door-1000", "door_height:door-1025-sliding",
        "door_width:D1", "level:roof_structure",
        *(f"opening_host:{TYPICAL}:opening-{n}" for n in (29, 30, 47, 48)),
        f"projection:{TYPICAL}:projection-22", "projection_thickness:chajja",
        *(f"room:{GROUND}:room-{n}" for n in (2, 3, 4, 5)),
        *(f"rooms:{TYPICAL}:rooms-share-space-{n}" for n in range(1, 6)),
        "slab_outline:storey-0", f"stair_axis:{GROUND}:stair-1", f"stair_axis:{TYPICAL}:stair-1",
        *(f"window_height:{k}" for k in ("W", "W1", "W2", "W3", "W4", "window-1025", "window-640")),
        *(f"window_sill:{k}" for k in ("W", "W1", "W2", "W3", "W4", "window-1025", "window-640")),
    ]
    by_id = {m.id: m for m in result.missing}
    assert by_id["door_width:D1"].options == [800, 900] and len(by_id["door_width:D1"].elements) == 21
    assert by_id["window_sill:W"].options == [900]  # section BB's window, offered, not used
    assert by_id[f"beam_depth:{TIE}"].options == [450] and len(by_id[f"beam_depth:{TIE}"].elements) == 23
    assert by_id["level:roof_structure"].options == [2100, 2250, 2400]
    # the catalogue is there before the answers
    assert {(d.name, d.width_mm) for d in result.standards.windows} >= {("W", 1500), ("W1", 1050), ("W4", 1000)}


def test_with_answers_the_plan(building, answers):
    result = to_plan(building, answers)
    assert result.missing == []
    plan, counts = result.plan, result.counts
    assert [(op.name, op.elevation_mm) for op in plan.ops if op.op == "level"] == [
        ("Ground Floor", 0), ("Floor 1", 2700), ("Floor 2", 5400), ("Floor 3", 8100), ("Floor 4", 10800),
        ("Roof", 13500), ("Parapet Top", 14400), ("GL", -600), ("Foundation", -1300), ("Upper Roof", 15900)]
    assert counts["grid"] == 33 and counts["wall_type"] == 4 and counts["floor_type"] == 5 and counts["opening_type"] == 11
    for n in range(1, 5):
        storey = f"Floor {n}"
        assert counts[f"wall@{storey}"] == 54 + 4  # the typical plan's walls + walls across the 4 entrance doors
        assert counts[f"column@{storey}"] == 56
        assert counts[f"window@{storey}"] == 37
        assert counts[f"door@{storey}"] == 38  # D x12, D1 x20, 4 entrance doors, 2 lift doors
        # 34 labels; the entrance labels one room; each flat's living/dining/kitchen two rooms (kitchen apart)
        assert counts[f"room@{storey}"] == 29
        assert counts[f"room_separator@{storey}"] == 4  # one per kitchen, where its alcove opens
        assert counts[f"floor@{storey}"] == 1 + 8 + 20 + 1  # slab, balconies, chajjas, the ledge
        assert counts[f"stair@{storey}"] == 1 and counts[f"railing@{storey}"] == 1
        assert counts[f"shaft@{storey}"] == 5  # 2 lifts, 2 ducts, the stair well
    assert counts["wall@Ground Floor"] == 17 and counts["column@Ground Floor"] == 56
    assert counts["door@Ground Floor"] == 1 and counts["window@Ground Floor"] == 3 and counts["room@Ground Floor"] == 1
    assert counts["stair@Ground Floor"] == 1 and counts["floor@Ground Floor"] == 1
    assert counts["column@Foundation"] == 56 and counts["foundation_slab@Foundation"] == 1
    assert counts["beam@Ground Floor"] == 23  # tie beams at the plinth (PL = ground floor here)
    assert counts["wall@Roof"] == 82 + 6 and counts["column@Roof"] == 6 and counts["room@Roof"] == 4
    assert counts["door@Roof"] == 2 and counts["window@Roof"] == 2 and counts["floor@Roof"] == 1
    assert counts["shaft@Roof"] == 1

    ops = plan.by_key()
    marks = {op.type for op in plan.ops if op.op in ("door", "window")}
    assert marks == {"D", "D1", "door-1000", "door-1025-sliding", "W", "W1", "W2", "W3", "W4", "window-1025", "window-640"}
    d1 = next(o for o in result.standards.doors if o.name == "D1")
    assert (d1.width_mm, d1.height_mm, d1.widths_drawn) == (800, 2100, {"800": 17, "900": 4})
    ground = ops[f"{GROUND}-outline-0"]
    assert ground.type == "Plinth 225mm PCC 150 + BFS 75"  # the floor build-up from the sections, as layers
    raft = ops["8.Foundation Details.dxf-F1-foundation"]
    assert (raft.level, raft.type) == ("Foundation", "Plinth 500mm Foundation Slab")
    assert ops[f"{TIE}-B1-0"].type == "RCC Beam 250x450"
    # every op of a recognised element points back at it
    assert all(src.sheet and src.element for key, src in result.sources.items()
               if not key.startswith(("TYPE-", "LEVEL-")))
    assert {n.what for n in result.not_modelled} >= {"reinforcement", "roof falls (slope arrows)",
                                                     "beams at Floor 1, Floor 2, Floor 3, Floor 4, Roof"}



def test_a_wall_across_a_door_gap_ends_on_the_centrelines_it_abuts(building, answers):
    """Revit moves a wall end on another wall's face to that wall's centreline (T join): the plan ends it there, as
    the live build read back (2026-11-16), and the door's offset is measured from that start."""
    ops = to_plan(building, answers).plan.by_key()
    ends = {n: (ops[f"{TYPICAL}-opening-{n}.wall-1"].start[0], ops[f"{TYPICAL}-opening-{n}.wall-1"].end[0],
                ops[f"{TYPICAL}-opening-{n}-1"].offset_mm) for n in (29, 30, 47, 48)}
    assert ends == {29: (19175.03, 18050.03, 625.0), 30: (22425.03, 23550.03, 625.0),
                    47: (19175.03, 17925.03, 625.0), 48: (22425.03, 23675.03, 625.0)}


def _without_corners(building, fid_rooms):
    """The building with the shared space of the given rooms replaced by its bounding box (no corner, no mouth)."""
    b = copy.deepcopy(building)
    plan = next(p for p in b.plans if p.sheet == TYPICAL)
    for r in plan.rooms:
        if r.id in fid_rooms:
            x0, y0, x1, y1 = Polygon(r.shared_space).bounds
            r.shared_space = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    return b


def test_no_clear_mouth_is_asked_never_guessed(building, answers):
    qid = f"rooms:{TYPICAL}:rooms-share-space-1"
    result = to_plan(_without_corners(building, {"room-1", "room-7", "room-10"}), answers)
    assert result.plan is None and [m.id for m in result.missing] == [qid]
    m = result.missing[0]
    assert m.topic == "rooms_share_space" and m.options == [] and "OPEN KITCHEN" in m.text
    assert "No wall end or column face gives a line" in m.text
    # the user gives the line (one per room to separate): it is drawn as given
    line = [[29000.0, 21000.0], [32000.0, 21000.0]]
    result = to_plan(_without_corners(building, {"room-1", "room-7", "room-10"}),
                     {**answers, qid: {**answers[qid], "lines": [line]}})
    assert result.missing == []
    ops = result.plan.by_key()
    assert ops[f"{TYPICAL}-rooms-share-space-1.1-1"].points == [tuple(q) for q in line]
    assert (ops[f"{TYPICAL}-room-1-1"].name, ops[f"{TYPICAL}-room-7-1"].name) == ("OPEN KITCHEN", "LIVING / DINING")


def test_several_lines_alike_are_asked_with_each_as_an_answer(building, answers, monkeypatch):
    qid = f"rooms:{TYPICAL}:rooms-share-space-1"
    found = [((29238.03, 22589.5), (31924.53, 22000.0)), ((29238.03, 22589.5), (31424.53, 19490.18))]
    monkeypatch.setattr(split_space, "find_mouth", lambda *a, **k: split_space.Mouth(
        candidates=found, reason="2 lines part this label from the others, none clearly the shortest"))
    result = to_plan(building, answers)
    m = next(m for m in result.missing if m.id == qid)
    assert result.plan is None and "none clearly the shortest" in m.text and len(m.options) == 2
    assert all(o["mode"] == "separate" and o["rooms"] == ["KITCHEN"] and len(o["lines"]) == 1 for o in m.options)
    monkeypatch.undo()
    result = to_plan(building, {**answers, qid: m.options[1]})  # choosing one builds it
    assert qid not in {x.id for x in result.missing}
    assert result.plan.by_key()[f"{TYPICAL}-rooms-share-space-1.1-1"].points == [tuple(q) for q in m.options[1]["lines"][0]]


def test_a_name_that_matches_no_label_is_asked(building, answers):
    qid = f"rooms:{TYPICAL}:rooms-share-space-1"
    result = to_plan(building, {**answers, qid: {"mode": "separate", "rooms": ["PANTRY"]}})
    assert [m.id for m in result.missing] == [qid] and '"PANTRY" names none of the labels' in result.missing[0].text


def test_each_flats_kitchen_is_its_own_room_closed_where_its_alcove_opens(building, answers):
    """The user's decision (2026-11-17): a kitchen in the open living/dining space is its own room. Answered
    {"mode": "separate", "rooms": ["KITCHEN"], "name": "LIVING / DINING"}, each flat's shared space gives a kitchen
    room and a living/dining room, parted by a room separation line across the kitchen alcove's mouth: from the end
    of the 900 mm wall stub off the outer wall to the corner of the column at the bedroom door, drawn on into both."""
    result = to_plan(building, answers)
    ops = result.plan.by_key()
    plan = next(p for p in building.plans if p.sheet == TYPICAL)
    walls = [LineString([w.start, w.end]).buffer(w.thickness_mm / 2, cap_style="flat") for w in plan.walls]
    flats = {}
    for n, kitchen, rest in ((1, "room-1", "room-7"), (2, "room-6", "room-8"), (4, "room-34", "room-25"),
                             (5, "room-33", "room-26")):
        space = Polygon(next(r.shared_space for r in plan.rooms if r.id == kitchen))
        for storey in range(1, 5):
            sep = ops[f"{TYPICAL}-rooms-share-space-{n}.1-{storey}"]
            k, ld = ops[f"{TYPICAL}-{kitchen}-{storey}"], ops[f"{TYPICAL}-{rest}-{storey}"]
            assert (sep.level, k.level, ld.level) == (f"Floor {storey}",) * 3
            assert "KITCHEN" in k.name and ld.name == "LIVING / DINING" and k.flat == ld.flat
            line = LineString(sep.points)
            assert 3990 <= line.length <= 4010  # the 3792 mm mouth, drawn on into the stub and the wall past the column
            a, b = sep.points
            side = lambda p: (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])  # noqa: E731
            assert side(k.point) * side(ld.point) < 0  # the kitchen label on one side, the living/dining on the other
            assert all(any(w.contains(Point(q)) for w in walls) for q in sep.points)  # both ends in a wall: no slit
            assert space.buffer(1).contains(line.interpolate(0.5, normalized=True))
            # the other labels of the space (the living room's) are in the living/dining room, not counted twice
            others = [r for r in plan.rooms if r.shared_space and Polygon(r.shared_space).equals(space)
                      and r.id not in (kitchen, rest)]
            assert others and all(f"{TYPICAL}-{r.id}-{storey}" not in ops for r in others)
        flats[k.flat] = (k.name, [tuple(q) for q in sep.points])
    assert flats == {
        "U2": ("OPEN KITCHEN", [(31527.72, 19343.91), (29223.04, 22610.75)]),
        "U1": ("OPEN KITCHEN", [(12377.02, 22610.75), (10072.34, 19343.91)]),
        "U3": ("KITCHEN OPEN", [(10072.33, 10885.44), (12377.02, 7619.26)]),
        "U4": ("KITCHEN OPEN", [(29223.04, 7619.26), (31527.73, 10885.44)])}
