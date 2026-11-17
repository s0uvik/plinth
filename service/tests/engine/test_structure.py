"""The stair design, the RCC frame, chajjas and the roof parapet."""

import pytest

from plinth.engine import LayoutError, generate, structure
from plinth.engine.floors import build_floor
from plinth.engine.geometry import Rect
from plinth.engine.site import envelope
from plinth.engine.stair import design_stair, stair_core
from plinth.engine.templates import plan_plate
from tests.engine.test_engine import make


def layout(spec):
    plate = plan_plate(spec, envelope(spec), generate(spec).report["variant"] - 1)
    return plate, [build_floor(plate, level) for level in range(spec.building.upper_floors + 1)]


def test_stair_has_even_nbc_risers_in_two_equal_flights():
    zone = Rect(0, 0, 2780, 3070)  # the core's stair rectangle for 3000 mm floors and a 1200 mm stair
    d = design_stair(zone, 3000, 1200)
    assert (d.risers, d.riser, d.tread) == (16, 187.5, 250)
    up, down = d.flights
    assert up.risers == down.risers == 8
    assert up.start == (115 + 600, 3070) and up.end == (715, 3070 - 7 * 250)  # first riser at the lobby edge
    assert down.start == (2780 - 115 - 600, 3070 - 1750) and down.end == (2065, 3070)  # arrives back at the lobby
    assert d.landing_depth >= 1200
    assert design_stair(Rect(0, 0, 2780, 3320), 3100, 1200).risers == 18  # 17 rounded up to even
    for ftf in range(2850, 3601, 50):
        core = stair_core(ftf, 1200)
        assert core.risers % 2 == 0 and ftf / core.risers <= 190 and ftf / (core.risers - 2) > 190


def test_a_stair_that_does_not_fit_its_zone_is_refused_with_what_it_needs():
    with pytest.raises(LayoutError, match=r"need 2550 x 2950 mm clear; the stair zone gives 2550 x 2700 mm"):
        design_stair(Rect(0, 0, 2780, 2815), 3000, 1200)
    with pytest.raises(LayoutError, match="need 2650 x"):
        design_stair(Rect(0, 0, 2780, 3070), 3000, 1250)
    with pytest.raises(LayoutError, match="below the 1000 mm"):
        design_stair(Rect(0, 0, 5000, 5000), 3000, 900)


def test_every_golden_style_core_holds_its_stair():
    for width in (1000, 1200, 1500, 1800):
        for ftf in (2850, 3000, 3300, 3600):
            spec = make((22000, 40000), ftf=ftf)
            spec = spec.model_copy(update={"core": spec.core.model_copy(update={"stair_width_mm": width})})
            stairs = [op for op in generate(spec).plan.ops if op.op == "stair"]
            assert stairs and stairs[0].width_mm == width


def test_columns_stand_in_walls_clear_of_openings_and_beams_never_cross_a_room():
    for spec in (make((15000, 25000)), make((22000, 40000), units=(("2BHK", 3),)), make((18000, 34000), units=(("2BHK", 4),), lift=True)):
        plate, floors = layout(spec)
        typical = floors[1]
        f = structure.frame(plate, typical, floors)
        p = plate.plate
        corners = {(p.x0, p.y0), (p.x1, p.y0), (p.x0, p.y1), (p.x1, p.y1)}
        assert corners <= {c.node for c in f.columns}
        walls = structure._Walls(typical, floors)
        for c in f.columns:
            x, y = c.node
            on_x, on_y = walls.covered("x", x, y, y), walls.covered("y", y, x, x)
            assert on_x or on_y, f"column at {c.node} is not in a wall"
            assert (c.along == "y" and on_x) or (c.along == "x" and on_y), "long side along the wall it sits in"
            assert walls.clear(c.rect)
            r = c.rect
            assert p.x0 - 115 <= r.x0 and r.x1 <= p.x1 + 115 and p.y0 - 115 <= r.y0 and r.y1 <= p.y1 + 115
        for (ax, ay), (bx, by) in f.beams:
            axis, c, a, b = ("x", ax, ay, by) if ax == bx else ("y", ay, ax, bx)
            assert walls.covered(axis, c, a, b), "beams sit on walls"
            assert abs(b - a) <= structure.LONG_SPAN
        # The corner columns are flush with the outer faces, not standing out of them.
        corner = next(c for c in f.columns if c.node == (p.x0, p.y0))
        assert corner.rect.x0 == p.x0 - 115 and corner.rect.y0 == p.y0 - 115
        y = plate.core.stair.y1 + 115
        assert f.landing_beam == ((plate.core.x0, y), (plate.core.x1, y))


def test_frame_ops_cover_every_storey_and_floor():
    spec = make((15000, 25000))
    plan = generate(spec).plan
    plate, floors = layout(spec)
    f = structure.frame(plate, floors[1], floors)
    columns = [op for op in plan.ops if op.op == "column"]
    beams = [op for op in plan.ops if op.op == "beam"]
    assert len(columns) == 4 * len(f.columns)  # ground and three upper storeys
    assert len(beams) == 4 * len(f.beams) + 3  # floors 1-3 and the roof, plus a landing beam where the stair arrives
    assert {op.key for op in beams if op.key.endswith("-BL")} == {"F1-BL", "F2-BL", "F3-BL"}
    assert columns[0].type == structure.COLUMN and beams[0].type == structure.BEAM


def test_chajjas_shade_every_outside_opening_from_the_lintel_outwards():
    spec = make((15000, 25000))
    plate, floors = layout(spec)
    floor = floors[1]
    shades = structure.chajjas(floor, 600)
    p = plate.plate
    outside = [o for o in floor.doors + floor.windows if o.spaces[1] is None]
    covered = 0
    for o in outside:
        w = floor.walls[o.wall]
        top = o.type.sill + o.type.height + structure.LINTEL
        for s in shades:
            xs, ys = [x for x, _ in s.points], [y for _, y in s.points]
            along = (min(ys), max(ys)) if w.axis == "x" else (min(xs), max(xs))
            across = (min(xs), max(xs)) if w.axis == "x" else (min(ys), max(ys))
            expected = (w.c - 115 - 600, w.c - 115) if w.c in (p.x0, p.y0) else (w.c + 115, w.c + 115 + 600)
            if (s.offset == top and across == expected
                    and along[0] <= o.center - o.type.width / 2 - 150 and o.center + o.type.width / 2 + 150 <= along[1]):
                covered += 1
                break
    assert covered == len(outside) and len(shades) <= len(outside)
    assert structure.chajjas(floor, 0) == []


def test_chajjas_that_would_touch_merge_into_one():
    from plinth.engine.catalog import OpeningType
    from plinth.engine.floors import BuiltFloor
    from plinth.engine.geometry import Edge, Wall
    from plinth.engine.openings import PlacedOpening
    window = OpeningType("W", 1200, 1200, 900)
    floor = BuiltFloor(1, None, [Edge("y", 0, 0, 10000, None, ("a", 0))], [Wall("y", 0, 0, 10000, "Brick 230", 230)], {},
                       windows=[PlacedOpening("window", window, 0, 2000, 2000, ("a", None)),
                                PlacedOpening("window", window, 0, 3500, 3500, ("a", None)),  # 300 mm gap: chajjas touch
                                PlacedOpening("window", window, 0, 7000, 7000, ("a", None))])
    shades = structure.chajjas(floor, 600)
    assert [s.points for s in shades] == [((1250, -115), (4250, -115), (4250, -715), (1250, -715)),
                                          ((6250, -115), (7750, -115), (7750, -715), (6250, -715))]
    assert {s.offset for s in shades} == {2250}


def test_parapet_is_a_closed_loop_just_inside_the_roof_slab():
    walls = structure.parapet(Rect(0, 0, 10000, 16000), 1000)
    assert walls == [((60, 60), (9940, 60)), ((9940, 60), (9940, 15940)), ((9940, 15940), (60, 15940)), ((60, 15940), (60, 60))]
    assert structure.parapet(Rect(0, 0, 10000, 16000), 0) == []
    plan = generate(make((15000, 25000))).plan
    roof = [op for op in plan.ops if op.op == "wall" and op.key.startswith("ROOF-P")]
    assert len(roof) == 4 and {op.type for op in roof} == {"Brick 115"} and {op.height_mm for op in roof} == {1000}
