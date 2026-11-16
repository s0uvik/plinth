"""Plan recognition on the G+4 sample's floor and roof plans, pinned so a regression shows.

Expected values were read off the printed set (drawing-set.pdf, pages 28-30) and the office's door/window estimate
(door-window-estimate.xlsx), not taken from the recogniser: per typical floor 4 flats of 3 bedrooms, marks as
written w x24, w1 x4, w2 x4, w3 x4, w4 x1, D x12, D1 x20, four unmarked flat entrance doors (estimate: "Entrance
1000 x 2100" x4), window widths W 1500, W1 1050, W2 640, W3 500 (estimate), doors D 900 and D1 800 (estimate; four
D1 swings are drawn at 900, which the recogniser must report, not fix), 2 lifts, 1 dog-leg stair, walls 250/125.
"""

from collections import Counter

import pytest

from b2m.cad import classify
from b2m.cad.recognise import recognise_plan_detailed


@pytest.fixture(scope="module")
def recognised(sample_sheets):
    out = {}
    for name in ("1.GROUND FLOOR PLAN.dxf", "2.PLAN 1st to 4th.dxf", "3.ROOF PLAN.dxf"):
        sh = sample_sheets[name]
        out[name] = recognise_plan_detailed(sh, classify.classify(sh.title, name))
    return out


def test_typical_plan_walls(recognised):
    rec, report = recognised["2.PLAN 1st to 4th.dxf"]
    assert rec.role == "floor_plan" and rec.storeys == [1, 2, 3, 4]
    assert [round(c) for c, _ in report.thickness_classes] == [125, 250]
    assert {w.thickness_mm for w in rec.walls} == {125.0, 250.0}
    assert Counter((w.kind, w.thickness_mm) for w in rec.walls) == {
        ("exterior", 250.0): 8, ("interior", 250.0): 42, ("interior", 125.0): 4}
    assert rec.outline is not None and len(rec.outline) == 12  # two flat blocks joined by the core: not a rectangle
    assert len(rec.columns) == 56
    for element in rec.walls + rec.columns + rec.openings + rec.rooms + rec.stairs + rec.shafts + rec.projections:
        assert element.evidence.sheet == "2.PLAN 1st to 4th.dxf" and element.evidence.handles


def test_typical_plan_openings_and_marks(recognised):
    rec, report = recognised["2.PLAN 1st to 4th.dxf"]
    marks_as_written = Counter(o.mark for o in rec.openings if o.mark)
    assert marks_as_written == {"w": 24, "D1": 20, "D": 12, "w1": 4, "w2": 4, "w3": 4, "w4": 1}
    assert report.marks["texts"] == {"D": 12, "D1": 20, "W": 24, "W1": 4, "W2": 4, "W3": 4, "W4": 1}
    assert report.marks["marked"] == report.marks["texts"]  # every mark text found its opening
    widths = Counter((o.kind, o.mark_normalised, o.width_mm) for o in rec.openings)
    assert widths == {
        ("window", "W", 1500.0): 24, ("window", "W1", 1050.0): 4, ("window", "W2", 640.0): 4,
        ("window", "W3", 500.0): 4, ("window", "W4", 1000.0): 1,
        ("door", "D", 900.0): 12, ("door", "D1", 800.0): 16, ("door", "D1", 900.0): 4,
        ("door", None, 1000.0): 4,  # flat entrance doors, unmarked
        ("door", None, 1025.0): 2,  # lift landing doors (sliding-door lines in the lift shaft wall)
    }
    lift_doors = [o for o in rec.openings if o.swing == "sliding"]
    assert len(lift_doors) == 2 and all(o.evidence.note.startswith("lift landing door") for o in lift_doors)
    # every opening sits on a wall, except the four entrance doors drawn between two walls (asked about)
    off_wall = [o for o in rec.openings if o.wall_id is None]
    assert len(off_wall) == 4 and all(o.width_mm == 1000 for o in off_wall)
    walls = {w.id for w in rec.walls}
    assert all(o.wall_id in walls for o in rec.openings if o.wall_id)
    doors = [o for o in rec.openings if o.kind == "door" and o.swing != "sliding"]
    assert all(o.hinge is not None and o.leaves == 1 and o.block == "Door - Metric" for o in doors)


def test_typical_plan_rooms_units_stair_shafts(recognised):
    rec, report = recognised["2.PLAN 1st to 4th.dxf"]
    rooms = Counter((r.name, r.labelled_size_mm) for r in rec.rooms)
    assert rooms == {
        ("BED ROOM", (3500.0, 4000.0)): 8, ("BED ROOM", (4000.0, 4000.0)): 4, ("LIVING ROOM", (4500.0, 4000.0)): 4,
        ("DINING", None): 4, ("OPEN KITCHEN", None): 2, ("KITCHEN OPEN", None): 2, ("TOILET", None): 8,
        ("ENTRANCE", None): 2}
    bedrooms = [r for r in rec.rooms if r.name == "BED ROOM"]
    # labelled sizes are clear sizes here: the enclosed areas agree with the labels
    assert all(r.area_m2 == pytest.approx(r.labelled_size_mm[0] * r.labelled_size_mm[1] / 1e6, abs=0.02)
               for r in bedrooms)
    assert all(r.boundary for r in bedrooms + [r for r in rec.rooms if r.name == "TOILET"])
    assert report.units == 4
    assert Counter(r.unit for r in rec.rooms) == {"U1": 8, "U2": 8, "U3": 8, "U4": 8, None: 2}
    assert [(s.kind, s.width_mm, s.tread_mm, s.risers_per_flight) for s in rec.stairs] == [
        ("dog_leg", 1200.0, 250.0, [8, 8])]
    assert rec.stairs[0].up_arrow is not None
    assert Counter(s.kind for s in rec.shafts) == {"lift": 2, "duct": 2}
    assert Counter(p.kind for p in rec.projections) == {"chajja": 20, "balcony": 8, "unknown": 1}
    assert sorted(m.name for m in rec.section_marks) == ["A", "B"]


def test_typical_plan_findings_and_unclaimed(recognised):
    rec, _ = recognised["2.PLAN 1st to 4th.dxf"]
    assert Counter(f.topic for f in rec.findings) == {
        "door_not_in_wall": 4,  # the flat entrance doors close the gap between two walls
        "opening_without_mark": 4,  # ... and carry no mark
        "mark_width_mismatch": 1,  # D1 drawn at 800 (x16) and 900 (x4)
        "rooms_share_space": 5,  # living/dining/kitchen open plan (x4), the two ENTRANCE labels in the lobby
        "room_name_order": 1,  # "OPEN KITCHEN" vs "KITCHEN OPEN" in the mirrored flats
        "projection_kind": 1,  # the box over w4 drawn in full lines
    }
    mismatch = next(f for f in rec.findings if f.topic == "mark_width_mismatch")
    assert "800 mm x16" in mismatch.text and "900 mm x4" in mismatch.text
    assert all(f.evidence.handles for f in rec.findings)
    assert len(rec.unclaimed) <= 20


def test_ground_plan_stilts(recognised):
    rec, _ = recognised["1.GROUND FLOOR PLAN.dxf"]
    assert Counter(w.kind for w in rec.walls)["boundary"] == 5
    assert len(rec.columns) == 56
    assert len(rec.stairs) == 1
    assert [(o.kind, o.mark_normalised) for o in rec.openings if o.kind == "door"] == [("door", "D1")]
    toilet = next(r for r in rec.rooms if r.name == "TOILET")
    assert toilet.boundary is not None
    notes = " ".join(e.note or "" for e in rec.unclaimed)
    assert "DRAIN" in notes  # the drains are line pairs labelled DRAIN: kept apart from walls, listed
    assert "block \"Vehicles - Metric\"" in notes


def test_roof_plan(recognised):
    rec, report = recognised["3.ROOF PLAN.dxf"]
    assert rec.role == "roof_plan"
    assert [round(c) for c, _ in report.thickness_classes] == [100, 250]
    kinds = Counter(w.kind for w in rec.walls)
    assert kinds["parapet"] > 0 and kinds["parapet"] > kinds["exterior"] + kinds["interior"]
    assert len(rec.falls) == 8 and all(f.label == "SLOPE" for f in rec.falls)
    assert len(rec.stairs) == 1
    assert {"walls_side_by_side"} <= {f.topic for f in rec.findings}
    names = {r.name for r in rec.rooms}
    assert {"OPEN TERRACE", "LIFT ROOM", "MUMTY ROOM"} <= names
    assert [(o.kind, o.mark_normalised) for o in rec.openings if o.kind == "door"] == [("door", "D"), ("door", "D")]


def test_every_finding_and_unclaimed_item_has_evidence(recognised):
    for name, (rec, _) in recognised.items():
        assert all(f.evidence.sheet == name and f.evidence.handles for f in rec.findings), name
        assert all(u.sheet == name and u.handles for u in rec.unclaimed), name
        assert all(u.note for u in rec.unclaimed), name


def test_hidden_dynamic_block_states_are_marked_invisible(sample_sheets):
    """The door blocks carry every visibility state; only the one shown is visible (DXF group 60)."""
    sh = sample_sheets["2.PLAN 1st to 4th.dxf"]
    door = next(e for e in sh.drawing_entities if e.kind == "insert" and e.effective_block == "Door - Metric")
    arcs = [c for c in door.walk() if c.kind == "arc"]
    assert len(arcs) == 4 and sum(not a.invisible for a in arcs) == 1


def test_cli_prints_counts_and_writes_svg(tmp_path, capsys):
    from b2m.cad.recognise.__main__ import main
    from tests.cad.conftest import SAMPLE_DXF
    assert main([str(SAMPLE_DXF / "2.PLAN 1st to 4th.dxf"), str(SAMPLE_DXF / "6.SECTION- AA.dxf"),
                 "--svg", str(tmp_path), "--json", str(tmp_path)]) == 0
    out = capsys.readouterr().out
    assert "thickness classes found: 125 mm" in out and "mark texts D 12, D1 20, W 24" in out
    assert "6.SECTION- AA.dxf: section, skipped" in out
    assert (tmp_path / "2.PLAN 1st to 4th.svg").read_text(encoding="utf-8").startswith("<svg")
    assert (tmp_path / "2.PLAN 1st to 4th.json").exists()
