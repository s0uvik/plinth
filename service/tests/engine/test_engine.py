import itertools
from collections import defaultdict

import pytest

from b2m.engine import LayoutError, generate
from b2m.engine import catalog
from b2m.plan import DoorOp, FloorOp, LevelOp, RoomOp, ShaftOp, WallOp, WindowOp
from b2m.spec import BuildingSpec

OPENING_WIDTH = {t.name: t.width for t in list(catalog.DOORS.values()) + list(catalog.WINDOWS.values())}


def make(plot, ground="stilt_parking", floors=3, units=(("2BHK", 2),), lift=False, targets=None, ftf=3000):
    return BuildingSpec.model_validate({
        "site": {"plot_mm": list(plot), "road_width_mm": 9000},
        "building": {"ground": ground, "upper_floors": floors, "floor_to_floor_mm": ftf},
        "units": [{"type": t, "per_floor": n, **({"target_carpet_m2": targets[t]} if targets and t in targets else {})}
                  for t, n in units],
        "core": {"lift": lift},
    })


def assert_plan_sound(plan, spec):
    """Checks on the emitted plan alone, independent of the engine's own validation."""
    by_key = plan.by_key()
    levels = [op for op in plan.ops if isinstance(op, LevelOp)]
    assert [lv.elevation_mm for lv in levels] == [i * spec.building.floor_to_floor_mm for i in range(len(levels))]
    assert len(levels) == spec.building.upper_floors + 2

    walls_by_line = defaultdict(list)
    for op in plan.ops:
        if isinstance(op, WallOp):
            (x0, y0), (x1, y1) = op.start, op.end
            assert all(v % 10 == 0 for v in (x0, y0, x1, y1)), op.key
            assert (x0 == x1) != (y0 == y1), f"{op.key} is not axis-aligned"
            length = abs(x1 - x0) + abs(y1 - y0)
            assert length >= catalog.MIN_WALL_LENGTH, op.key
            axis, c, a, b = ("x", x0, min(y0, y1), max(y0, y1)) if x0 == x1 else ("y", y0, min(x0, x1), max(x0, x1))
            walls_by_line[(op.level, axis, c)].append((a, b, op.key))
    for line, spans in walls_by_line.items():
        spans.sort()
        for (a0, b0, k0), (a1, b1, k1) in zip(spans, spans[1:]):
            assert b0 <= a1, f"walls {k0} and {k1} overlap on {line}"

    taken = defaultdict(list)
    for op in plan.ops:
        if isinstance(op, (DoorOp, WindowOp)):
            host = by_key[op.host_key]
            length = abs(host.end[0] - host.start[0]) + abs(host.end[1] - host.start[1])
            half = OPENING_WIDTH[op.type] / 2
            assert half + catalog.OPENING_CLEARANCE <= op.offset_mm <= length - half - catalog.OPENING_CLEARANCE, op.key
            for lo, hi, other in taken[op.host_key]:
                assert op.offset_mm + half <= lo or op.offset_mm - half >= hi, f"{op.key} overlaps {other}"
            taken[op.host_key].append((op.offset_mm - half, op.offset_mm + half, op.key))

    slabs = [op for op in plan.ops if isinstance(op, FloorOp)]
    assert len(slabs) == len(levels)
    shafts = [op for op in plan.ops if isinstance(op, ShaftOp)]
    assert len(shafts) == (2 if spec.core.lift else 1)
    rooms = [op for op in plan.ops if isinstance(op, RoomOp)]
    assert len({(r.level, r.point) for r in rooms}) == len(rooms)


def test_same_spec_same_plan():
    spec = make((15000, 25000))
    assert generate(spec).plan.model_dump() == generate(spec).plan.model_dump()
    assert generate(spec).plan.spec_hash == spec.hash()


def test_prd_example_is_refused_with_the_depth_it_needs():
    spec = make((15000, 25000), units=(("2BHK", 4),), targets={"2BHK": 60})
    with pytest.raises(LayoutError, match=r"T4 with these flats needs [\d.]+ m of building depth; the envelope allows 19.50 m"):
        generate(spec)


def test_narrow_plot_is_refused_with_a_reason():
    with pytest.raises(LayoutError, match="wide"):
        generate(make((9000, 30000)))


def test_report_describes_the_layout():
    result = generate(make((15000, 25000)))
    report = result.report
    assert report["template"] == "T2" and report["setbacks_mm"]["verified"] is False
    assert any("provisional" in n for n in report["notes"])
    assert {f["letter"] for f in report["flats_per_floor"]} == {"A", "B"}
    assert all(f["carpet_m2"] >= 50 for f in report["flats_per_floor"])
    assert 0 < report["ground_coverage"] < 1


def test_name_prefix_applies_to_levels_and_grids():
    plan = generate(make((15000, 25000)), name_prefix="X ").plan
    assert all(op.name.startswith("X ") for op in plan.ops if isinstance(op, LevelOp))


SWEEP = list(itertools.product(
    [(12000, 22000), (15000, 25000), (16000, 30000), (18000, 34000), (22000, 40000)],
    ["stilt_parking", "flats"],
    [3, 4],
    [(("1BHK", 2),), (("2BHK", 2),), (("3BHK", 2),), (("1BHK", 1), ("3BHK", 1)), (("2BHK", 3),),
     (("1BHK", 2), ("2BHK", 1)), (("1BHK", 4),), (("2BHK", 4),), (("2BHK", 2), ("3BHK", 2))],
    [False, True],
))


def test_every_feasible_layout_in_a_wide_sweep_is_sound():
    built = refused = 0
    for plot, ground, floors, units, lift in SWEEP:
        spec = make(plot, ground, floors, units, lift)
        try:
            result = generate(spec)
        except LayoutError as e:
            refused += 1
            assert str(e).strip()
            continue
        assert_plan_sound(result.plan, spec)
        built += 1
    assert built >= 60, (built, refused)
