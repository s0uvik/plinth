import itertools
from collections import defaultdict

import pytest

from b2m.engine import LayoutError, generate
from b2m.engine import catalog, structure
from b2m.engine.stair import MAX_RISER, TREAD
from b2m.plan import BeamOp, ColumnOp, DoorOp, FloorOp, LevelOp, RailingOp, RoomOp, ShaftOp, StairOp, WallOp, WindowOp
from b2m.spec import BuildingSpec

OPENING_WIDTH = {t.name: t.width for t in list(catalog.DOORS.values()) + [o for opts in catalog.WINDOW_OPTIONS.values() for o in opts]}


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

    slabs = [op for op in plan.ops if isinstance(op, FloorOp) and op.type == spec.standards.slab]
    assert len(slabs) == len(levels) and all(op.offset_mm == 0 for op in slabs)
    shafts = [op for op in plan.ops if isinstance(op, ShaftOp)]
    assert len(shafts) == (2 if spec.core.lift else 1)
    rooms = [op for op in plan.ops if isinstance(op, RoomOp)]
    assert len({(r.level, r.point) for r in rooms}) == len(rooms)
    assert_extras_sound(plan, spec, slabs[0])


def _box(points):
    xs, ys = [x for x, _ in points], [y for _, y in points]
    return min(xs), min(ys), max(xs), max(ys)


def _overlap(a, b):
    return a[0] < b[2] and b[0] < a[2] and a[1] < b[3] and b[1] < a[3]


def assert_extras_sound(plan, spec, slab):
    """Columns, beams, stairs, railings, chajjas and parapet, checked on the plan alone."""
    by_key = plan.by_key()
    levels = [op.name for op in plan.ops if isinstance(op, LevelOp)]
    ftf = spec.building.floor_to_floor_mm
    x0, y0, x1, y1 = _box(slab.points)
    outer = (x0 - 115, y0 - 115, x1 + 115, y1 + 115)

    columns = [op for op in plan.ops if isinstance(op, ColumnOp)]
    beams = [op for op in plan.ops if isinstance(op, BeamOp)]
    if spec.standards.structure == "load_bearing":
        assert not columns and not beams
    else:
        per_storey = defaultdict(list)
        for c in columns:
            assert levels.index(c.top_level) == levels.index(c.level) + 1, c.key
            hx, hy = (115, 225) if c.rotation_deg == 0 else (225, 115)
            per_storey[c.level].append((c.point[0] - hx, c.point[1] - hy, c.point[0] + hx, c.point[1] + hy))
        assert list(per_storey) == levels[:-1]  # every storey, ground to top floor
        plans = list(per_storey.values())
        assert all(p == plans[0] for p in plans), "the frame is the same on every storey"
        for i, r in enumerate(plans[0]):
            assert outer[0] <= r[0] and r[2] <= outer[2] and outer[1] <= r[1] and r[3] <= outer[3], f"column {r} sticks out"
            assert not any(_overlap(r, o) for o in plans[0][i + 1:]), "columns overlap"
        assert {b.level for b in beams} == set(levels[1:])  # every floor above ground, and the roof
        for b in beams:
            assert (b.start[0] == b.end[0]) != (b.start[1] == b.end[1]), f"{b.key} is not axis-aligned"
            assert x0 <= min(b.start[0], b.end[0]) and max(b.start[0], b.end[0]) <= x1, b.key
            assert y0 <= min(b.start[1], b.end[1]) and max(b.start[1], b.end[1]) <= y1, b.key

    stairs = [op for op in plan.ops if isinstance(op, StairOp)]
    assert [s.base_level for s in stairs] == levels[:-2]  # ground to the top floor; the roof is not reached
    shaft = _box(by_key["SHAFT-STAIR"].points)
    for s in stairs:
        risers = sum(r.risers for r in s.runs)
        assert risers % 2 == 0 and ftf / risers <= MAX_RISER and s.tread_mm >= TREAD and s.width_mm == spec.core.stair_width_mm
        assert len(s.runs) == 2 and s.runs[0].risers == s.runs[1].risers
        for r in s.runs:
            length = (r.risers - 1) * s.tread_mm
            dx, dy = r.end[0] - r.start[0], r.end[1] - r.start[1]
            assert dx == 0 and abs(dy) == length, "flights run front to back, end at their last riser"
            footprint = (r.start[0] - s.width_mm / 2, min(r.start[1], r.end[1]), r.start[0] + s.width_mm / 2, max(r.start[1], r.end[1]))
            assert shaft[0] <= footprint[0] and footprint[2] <= shaft[2] and shaft[1] <= footprint[1] and footprint[3] <= shaft[3]
        landing_front = min(s.runs[0].end[1], s.runs[1].start[1]) - s.width_mm
        assert landing_front >= shaft[1], "the mid landing fits in front of the flights"
    railings = [op for op in plan.ops if isinstance(op, RailingOp)]
    assert sorted(r.stair_key for r in railings) == sorted(s.key for s in stairs)

    chajjas = [op for op in plan.ops if isinstance(op, FloorOp) and op.type == structure.CHAJJA]
    assert bool(chajjas) == (spec.standards.chajja_mm > 0)
    by_level = defaultdict(list)
    for c in chajjas:
        box = _box(c.points)
        assert 0 < c.offset_mm < ftf - 150, c.key
        stilt_door = spec.building.ground == "stilt_parking" and c.level == levels[0]  # the core stands in the parking
        assert stilt_door or not _overlap(box, outer), f"{c.key} is not outside the building"
        assert min(box[2] - box[0], box[3] - box[1]) == spec.standards.chajja_mm, c.key
        for other in by_level[(c.level, c.offset_mm)]:
            assert not _overlap(box, other), f"{c.key} overlaps another chajja"
        by_level[(c.level, c.offset_mm)].append(box)

    parapets = [op for op in plan.ops if isinstance(op, WallOp) and op.level == levels[-1]]
    if spec.standards.roof_parapet_mm:
        assert len(parapets) == 4 and all(w.height_mm == spec.standards.roof_parapet_mm for w in parapets)
        for w in parapets:
            for x, y in (w.start, w.end):
                assert x0 < x < x1 and y0 < y < y1, "the parapet stands inside the roof slab edge"
    else:
        assert not parapets


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


def test_frame_stair_chajja_and_parapet_counts_for_a_typical_block():
    spec = make((15000, 25000))
    plan = generate(spec).plan
    ops = defaultdict(list)
    for op in plan.ops:
        ops[op.op].append(op)
    assert len(ops["stair"]) == 3 and len(ops["railing"]) == 3  # ground to floor 3 of a G+3
    columns_per_storey = len(ops["column"]) // 4
    assert columns_per_storey >= 8 and len(ops["column"]) == 4 * columns_per_storey
    windows = [op for op in ops["window"]]
    external_doors = [op for op in ops["door"] if op.type == catalog.DOORS["building"].name]
    chajjas = [op for op in ops["floor"] if op.type == structure.CHAJJA]
    assert 0 < len(chajjas) <= len(windows) + len(external_doors)


def test_options_switch_the_frame_chajjas_and_parapet_off():
    base = make((15000, 25000)).model_dump()
    spec = BuildingSpec.model_validate({**base, "standards": {**base["standards"], "structure": "load_bearing",
                                                               "chajja_mm": 0, "roof_parapet_mm": 0}})
    plan = generate(spec).plan
    assert not [op for op in plan.ops if op.op in ("column", "beam")]
    assert not [op for op in plan.ops if isinstance(op, FloorOp) and op.type == structure.CHAJJA]
    assert not [op for op in plan.ops if isinstance(op, WallOp) and op.key.startswith("ROOF-")]
    assert len([op for op in plan.ops if op.op == "stair"]) == 3  # the stair does not depend on the structure
    assert_plan_sound(plan, spec)
    deep = BuildingSpec.model_validate({**base, "standards": {**base["standards"], "chajja_mm": 900, "roof_parapet_mm": 1200}})
    plan = generate(deep).plan
    assert_plan_sound(plan, deep)
