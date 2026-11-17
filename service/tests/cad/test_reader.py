"""CAD reader: entity counts against the DXF itself, units, and block explosion (nested, rotated, mirrored)."""

import math
from collections import Counter

import ezdxf
import pytest

from plinth.cad import reader
from tests.cad.conftest import SAMPLE_DXF


def test_every_model_space_entity_is_read_with_its_handle(sample_sheets):
    for name, s in sample_sheets.items():
        msp = ezdxf.readfile(SAMPLE_DXF / name).modelspace()
        assert Counter(e.dxftype for e in s.cad.entities) == Counter(e.dxftype() for e in msp), name
        assert [e.handle for e in s.cad.entities] == [e.dxf.handle for e in msp], name
        assert s.cad.units == "mm" and s.cad.unit_factor == 1.0 and not s.cad.issues, name


def test_children_carry_parent_handle_and_index(sample_sheets):
    plan = sample_sheets["2.PLAN 1st to 4th.dxf"].cad
    for insert in (e for e in plan.walk() if e.kind == "insert"):
        assert [c.handle for c in insert.children] == [f"{insert.handle}:{i}" for i in range(len(insert.children))]
        assert all(c.parent == insert.handle for c in insert.children)
    assert len(plan.by_handle()) == sum(1 for _ in plan.walk())  # handles are unique


def test_anonymous_dynamic_blocks_name_their_source_block(sample_sheets):
    plan = sample_sheets["2.PLAN 1st to 4th.dxf"].cad
    anonymous = [e for e in plan.entities if e.kind == "insert" and e.block.startswith("*U")]
    assert anonymous and all(e.effective_block for e in anonymous)
    assert "Door - Metric" in {e.effective_block for e in anonymous}


def _nested_doc():
    doc = ezdxf.new(units=ezdxf.units.MM)
    inner = doc.blocks.new("INNER")
    inner.add_line((0, 0), (10, 0))
    inner.add_arc((0, 0), 5, 0, 90)
    inner.add_lwpolyline([(0, 0, 1), (10, 0, 0)], format="xyb")
    inner.add_text("T", height=2.5, dxfattribs={"rotation": 0}).set_placement((0, 0))
    inner.add_circle((20, 0), 3)
    hatch = inner.add_hatch()
    hatch.paths.add_polyline_path([(0, 0), (10, 0), (10, 10), (0, 10)], is_closed=True)
    outer = doc.blocks.new("OUTER")
    outer.add_blockref("INNER", (100, 0), dxfattribs={"rotation": 90})
    outer.add_line((0, 0), (0, 50))
    msp = doc.modelspace()
    mirrored = msp.add_blockref("OUTER", (1000, 2000), dxfattribs={"xscale": -1})
    turned = msp.add_blockref("OUTER", (0, 0), dxfattribs={"xscale": 2, "yscale": 2, "rotation": 30})
    return doc, mirrored.dxf.handle, turned.dxf.handle


def _read(doc, tmp_path, name="synthetic.dxf"):
    path = tmp_path / name
    doc.saveas(path)
    return reader.read_dxf(path)


def _close(a, b, tol=1e-3):
    return all(abs(x - y) <= tol for x, y in zip(a, b))


def test_nested_mirrored_block_explodes_to_world_coordinates(tmp_path):
    doc, mirrored, _ = _nested_doc()
    sheet = _read(doc, tmp_path)
    top = sheet.by_handle()[mirrored]
    assert top.kind == "insert" and top.block == "OUTER" and top.scale == (-1.0, 1.0)
    nested, line = top.children
    assert nested.kind == "insert" and nested.handle == f"{mirrored}:0" and nested.parent == mirrored
    assert line.kind == "line" and _close(line.points[0], (1000, 2000)) and _close(line.points[1], (1000, 2050))
    kids = {c.kind: c for c in nested.children}
    assert [c.handle for c in nested.children] == [f"{mirrored}:0:{i}" for i in range(6)]
    assert all(c.parent == nested.handle for c in nested.children)
    # INNER is turned 90 degrees and moved to (100, 0) inside OUTER, which is mirrored in x and moved to (1000, 2000).
    assert _close(kids["line"].points[0], (900, 2000)) and _close(kids["line"].points[1], (900, 2010))
    arc = kids["arc"]
    assert _close(arc.points[0], (900, 2000)) and arc.radius == pytest.approx(5)
    assert (arc.start_angle, arc.end_angle) == (pytest.approx(0, abs=1e-6), pytest.approx(90))
    poly = kids["polyline"]
    assert _close(poly.points[0], (900, 2000)) and _close(poly.points[1], (900, 2010))
    assert poly.bulges == [pytest.approx(-1), pytest.approx(0)]  # a mirror turns the bulge the other way
    assert _close(kids["circle"].points[0], (900, 2020))
    text = kids["text"]
    assert _close(text.points[0], (900, 2000)) and text.rotation == pytest.approx(90)
    square = {(round(x, 3), round(y, 3)) for x, y in kids["hatch"].boundaries[0]}
    assert square == {(900, 2000), (900, 2010), (910, 2010), (910, 2000)}


def test_scaled_rotated_block_explodes_to_world_coordinates(tmp_path):
    doc, _, turned = _nested_doc()
    top = _read(doc, tmp_path).by_handle()[turned]
    nested, line = top.children
    c, s = math.cos(math.radians(30)), math.sin(math.radians(30))

    def world(x, y):  # OUTER at the origin, scale 2, rotated 30 degrees
        return (2 * (x * c - y * s), 2 * (x * s + y * c))

    assert _close(line.points[1], world(0, 50))
    inner_line = next(k for k in nested.children if k.kind == "line")
    assert _close(inner_line.points[0], world(100, 0)) and _close(inner_line.points[1], world(100, 10))
    circle = next(k for k in nested.children if k.kind == "circle")
    assert circle.radius == pytest.approx(6) and _close(circle.points[0], world(100, 20))


def test_units_convert_to_millimetres(tmp_path):
    doc = ezdxf.new(units=ezdxf.units.M)
    doc.modelspace().add_line((0, 0), (2.5, 0))
    doc.modelspace().add_circle((1, 1), 0.5)
    sheet = _read(doc, tmp_path)
    line, circle = sheet.entities
    assert sheet.units == "m" and sheet.unit_factor == 1000
    assert line.points == [(0, 0), (2500, 0)] and circle.radius == 500 and circle.points == [(1000, 1000)]


def test_unitless_drawing_raises_an_issue_and_keeps_values(tmp_path):
    doc = ezdxf.new()
    doc.header["$INSUNITS"] = 0
    doc.modelspace().add_line((0, 0), (3, 0))
    sheet = _read(doc, tmp_path)
    assert sheet.units == "unitless" and sheet.entities[0].points == [(0, 0), (3, 0)]
    assert [i.topic for i in sheet.issues] == ["units"]


def test_sheet_round_trips_through_json(sample_sheets):
    cad = sample_sheets["3.ROOF PLAN.dxf"].cad
    assert reader.CadSheet.model_validate_json(cad.model_dump_json()) == cad
