"""Plan recognition on small drawings made here, with exactly known answers: a room with a door block and a window,
L and T junctions, a wall running on through a plain opening, columns, and the overlay SVG."""

from pathlib import Path

import ezdxf
import pytest

from plinth.cad import overlay, reader, sheet
from plinth.cad.classify import Classification
from plinth.cad.recognise import recognise_plan, recognise_plan_detailed

PLAN = Classification(role="floor_plan", storeys=[1])


def _pair(msp, a, b, t, cap_start=False, cap_end=False):
    """A wall drawn as two lines: centreline a->b, thickness t (axis-aligned), optional end caps."""
    (x0, y0), (x1, y1) = a, b
    if y0 == y1:
        msp.add_line((x0, y0 - t / 2), (x1, y0 - t / 2))
        msp.add_line((x0, y0 + t / 2), (x1, y0 + t / 2))
        caps = [((x, y0 - t / 2), (x, y0 + t / 2)) for x, on in ((x0, cap_start), (x1, cap_end)) if on]
    else:
        msp.add_line((x0 - t / 2, y0), (x0 - t / 2, y1))
        msp.add_line((x0 + t / 2, y0), (x0 + t / 2, y1))
        caps = [((x0 - t / 2, y), (x0 + t / 2, y)) for y, on in ((y0, cap_start), (y1, cap_end)) if on]
    for p, q in caps:
        msp.add_line(p, q)


def _read(doc, tmp_path: Path, name: str) -> sheet.Sheet:
    path = tmp_path / name
    doc.saveas(path)
    cad = reader.read_dxf(path)
    return sheet.Sheet(cad=cad, drawing_entities=cad.entities)


def _new():
    doc = ezdxf.new("R2018")
    doc.header["$INSUNITS"] = 4  # mm
    return doc, doc.modelspace()


def room_with_door_and_window(tmp_path: Path) -> sheet.Sheet:
    """A 4000 x 3000 room (clear) in 250 walls: a 900 door block in the bottom wall (hinge at x=1000 on the inner
    face, opening inwards) and a 1200 window (jambs + two glazing lines) in the top wall, whose faces run on."""
    doc, msp = _new()
    door = doc.blocks.new("DOOR900")
    door.add_arc((0, 0), 900, 0, 90)  # swing from the closed end (900, 0) to the open leaf (0, 900)
    door.add_lwpolyline([(0, 0), (0, 900), (-40, 900), (-40, 0)], close=True)  # the leaf, drawn open
    # outer and inner faces; bottom wall broken for the door (x 1000..1900) with caps
    msp.add_line((-250, 3250), (4250, 3250))
    msp.add_line((0, 3000), (4000, 3000))
    msp.add_line((-250, -250), (-250, 3250))
    msp.add_line((0, 0), (0, 3000))
    msp.add_line((4250, -250), (4250, 3250))
    msp.add_line((4000, 0), (4000, 3000))
    msp.add_line((-250, -250), (1000, -250))
    msp.add_line((1900, -250), (4250, -250))
    msp.add_line((0, 0), (1000, 0))
    msp.add_line((1900, 0), (4000, 0))
    msp.add_line((1000, -250), (1000, 0))
    msp.add_line((1900, -250), (1900, 0))
    msp.add_blockref("DOOR900", (1000, 0))
    # window in the top wall: jambs at 1500 and 2700, glazing at 3100 and 3150
    msp.add_line((1500, 3000), (1500, 3250))
    msp.add_line((2700, 3000), (2700, 3250))
    msp.add_line((1500, 3100), (2700, 3100))
    msp.add_line((1500, 3150), (2700, 3150))
    for text, at in (("BED ROOM", (1400, 1600)), ("4000 X 3000", (1300, 1200)), ("d1", (1350, -650)),
                     ("W1", (1950, 3450))):
        msp.add_text(text, height=200).set_placement(at)
    hatch = msp.add_hatch()  # a 300 x 300 column filled solid at the outer corner
    hatch.set_solid_fill()
    hatch.paths.add_polyline_path([(-250, -250), (50, -250), (50, 50), (-250, 50)], is_closed=True)
    return _read(doc, tmp_path, "room.dxf")


def test_room_with_door_and_window(tmp_path):
    rec, report = recognise_plan_detailed(room_with_door_and_window(tmp_path), PLAN)
    assert [c for c, _ in report.thickness_classes] == pytest.approx([250.0], abs=1)
    assert len(rec.walls) == 4 and {w.thickness_mm for w in rec.walls} == {250.0}
    assert {w.kind for w in rec.walls} == {"exterior"}
    # centrelines meet at the corners
    corners = {(round(x), round(y)) for w in rec.walls for x, y in (w.start, w.end)}
    assert corners == {(-125, -125), (4125, -125), (4125, 3125), (-125, 3125)}
    assert rec.outline is not None and len(rec.outline) == 4

    doors = [o for o in rec.openings if o.kind == "door"]
    windows = [o for o in rec.openings if o.kind == "window"]
    assert len(rec.openings) == 2 and len(doors) == 1 and len(windows) == 1
    d, w = doors[0], windows[0]
    assert d.width_mm == 900 and d.center == pytest.approx((1450, -125)) and d.hinge == pytest.approx((1000, 0))
    assert d.swing == "in" and d.leaves == 1 and d.block == "DOOR900"
    assert d.mark == "d1" and d.mark_normalised == "D1"
    bottom = next(x for x in rec.walls if round(x.start[1]) == -125)
    assert d.wall_id == bottom.id
    assert w.width_mm == 1200 and w.center == pytest.approx((2100, 3125)) and w.mark_normalised == "W1"

    assert len(rec.rooms) == 1
    room = rec.rooms[0]
    assert room.name == "BED ROOM" and room.labelled_size_mm == (4000, 3000)
    assert room.area_m2 == pytest.approx(12.0, abs=0.01)
    assert len(rec.columns) == 1
    col = rec.columns[0]
    assert (col.width_mm, col.depth_mm) == (300, 300) and col.center == pytest.approx((-100, -100))
    assert not [f for f in rec.findings if f.severity != "info"]
    assert rec.unclaimed == []
    # every element keeps its evidence
    for element in rec.walls + rec.openings + rec.rooms + rec.columns:
        assert element.evidence.sheet == "room.dxf" and element.evidence.handles


def test_l_and_t_junctions_meet_on_centrelines(tmp_path):
    doc, msp = _new()
    # H: 250 wall along y 0..250, x 0..5000; V: 250 wall along x 0..250 from y 250 up (an L at the origin);
    # T: 125 wall along x 2500..2625 from H's upper face up; H's upper face is broken where T joins it.
    msp.add_line((0, 0), (5000, 0))
    msp.add_line((250, 250), (2500, 250))
    msp.add_line((2625, 250), (5000, 250))
    msp.add_line((5000, 0), (5000, 250))
    msp.add_line((0, 0), (0, 4000))
    msp.add_line((250, 250), (250, 4000))
    msp.add_line((0, 4000), (250, 4000))
    msp.add_line((2500, 250), (2500, 3000))
    msp.add_line((2625, 250), (2625, 3000))
    msp.add_line((2500, 3000), (2625, 3000))
    rec = recognise_plan(_read(doc, tmp_path, "lt.dxf"), PLAN)
    got = sorted((w.thickness_mm, tuple(round(v, 1) for v in w.start), tuple(round(v, 1) for v in w.end))
                 for w in rec.walls)
    assert got == [(125.0, (2562.5, 125.0), (2562.5, 3000.0)),
                   (250.0, (125.0, 125.0), (125.0, 4000.0)),
                   (250.0, (125.0, 125.0), (5000.0, 125.0))]
    assert rec.openings == []


def test_wall_runs_on_through_a_plain_opening(tmp_path):
    doc, msp = _new()
    _pair(msp, (0, 0), (2000, 0), 250, cap_start=True, cap_end=True)
    _pair(msp, (2900, 0), (5000, 0), 250, cap_start=True, cap_end=True)
    _pair(msp, (0, 6000), (5000, 6000), 250, cap_start=True, cap_end=True)  # a second wall: classes need data
    rec = recognise_plan(_read(doc, tmp_path, "gap.dxf"), PLAN)
    walls = sorted(rec.walls, key=lambda w: w.start[1])
    assert len(walls) == 2
    assert walls[0].start == pytest.approx((0, 0)) and walls[0].end == pytest.approx((5000, 0))
    assert len(rec.openings) == 1
    o = rec.openings[0]
    assert (o.kind, o.width_mm, o.wall_id) == ("opening", 900, walls[0].id)
    assert o.center == pytest.approx((2450, 0))


def test_wide_uncapped_gap_is_two_walls(tmp_path):
    doc, msp = _new()
    _pair(msp, (0, 0), (2000, 0), 250)
    _pair(msp, (2900, 0), (5000, 0), 250)
    rec = recognise_plan(_read(doc, tmp_path, "gap2.dxf"), PLAN)
    assert len(rec.walls) == 2 and rec.openings == []


def test_columns_from_solid_fills(tmp_path):
    doc, msp = _new()
    h = msp.add_hatch()
    h.set_solid_fill()
    h.paths.add_polyline_path([(0, 0), (300, 0), (300, 600), (0, 600)], is_closed=True)
    h.paths.add_polyline_path([(3000, 0), (3600, 0), (3600, 300), (3000, 300)], is_closed=True)
    msp.add_solid([(6000, 0), (6400, 0), (6000, 400), (6400, 400)])  # SOLID corner order 1-2-4-3
    tri = msp.add_hatch()  # an arrowhead, not a column
    tri.set_solid_fill()
    tri.paths.add_polyline_path([(9000, 0), (9100, 0), (9050, 400)], is_closed=True)
    big = msp.add_hatch()  # a filled slab, far too big for a column
    big.set_solid_fill()
    big.paths.add_polyline_path([(0, 5000), (5000, 5000), (5000, 9000), (0, 9000)], is_closed=True)
    rec = recognise_plan(_read(doc, tmp_path, "cols.dxf"), PLAN)
    got = sorted((round(c.center[0]), round(c.center[1]), c.width_mm, c.depth_mm) for c in rec.columns)
    assert got == [(150, 300, 300, 600), (3300, 150, 600, 300), (6200, 200, 400, 400)]
    assert len(rec.unclaimed) == 2  # the arrowhead and the slab are listed, not dropped


def test_overlay_svg_draws_source_and_recognition(tmp_path):
    sh = room_with_door_and_window(tmp_path)
    rec = recognise_plan(sh, PLAN)
    svg = overlay.render(sh.drawing_entities, rec)
    assert svg.startswith("<svg") and svg.rstrip().endswith("</svg>")
    for element in rec.walls + rec.openings + rec.columns + rec.rooms:
        assert f'data-id="{element.id}"' in svg
    assert 'class="src"' in svg
