"""The G+4 sample through plan recognition (T3), sections/foundation/registration (T4) and the plan conversion (T6).

Without answers the conversion refuses with the list of values the drawings lack; with the test-only answers in
answers-test.json it gives a plan whose op counts per kind and level are pinned here. Expected counts follow the
recognition tests' pinned figures (54 walls, 56 columns, 37 windows, 34 room labels per typical storey).
"""

import json

import pytest

from b2m.cad import structure
from b2m.cad.recognise import recognise_plan
from b2m.imported import to_plan
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
        assert counts[f"room@{storey}"] == 25  # 34 labels, 5 shared spaces answered one room
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
    assert ground.type == "B2M 225mm PCC 150 + BFS 75"  # the floor build-up from the sections, as layers
    raft = ops["8.Foundation Details.dxf-F1-foundation"]
    assert (raft.level, raft.type) == ("Foundation", "B2M 500mm Foundation Slab")
    assert ops[f"{TIE}-B1-0"].type == "RCC Beam 250x450"
    # every op of a recognised element points back at it
    assert all(src.sheet and src.element for key, src in result.sources.items()
               if not key.startswith(("TYPE-", "LEVEL-")))
    assert {n.what for n in result.not_modelled} >= {"reinforcement", "roof falls (slope arrows)",
                                                     "beams at Floor 1, Floor 2, Floor 3, Floor 4, Roof"}
