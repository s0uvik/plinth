"""Section recognition (T4): levels and heights from dimensions, meanings from geometry."""

import pytest

from b2m.cad.sections import recognise_section
from tests.cad import synthetic


def _levels(rec):
    return {lv.name: lv for lv in rec.levels}


def test_synthetic_section_levels_heights_and_meanings(tmp_path):
    rec = recognise_section(synthetic.section(tmp_path), "XX")
    assert rec.datum == "PL"
    assert rec.storey_heights_mm == [3000, 3000, 3000]
    assert rec.slab_thickness_mm == 200
    assert rec.beam_depth_mm == 400
    assert rec.parapet_height_mm == 900
    assert rec.plinth_height_mm == 450
    assert rec.door_heights_mm == [2100]
    assert rec.window_sills_mm == [900] and rec.window_heads_mm == [2100]
    assert rec.foundation_layers == [("PCC", 100)]
    levels = _levels(rec)
    assert levels["PL"].elevation_mm == 0 and levels["PL"].storey == 0
    assert levels["GL"].elevation_mm == -450 and levels["GL"].kind == "ground"
    assert [lv.elevation_mm for lv in rec.levels if lv.kind == "floor"] == [3000, 6000]
    assert levels["roof"].elevation_mm == 9000
    assert levels["parapet top"].elevation_mm == 9900
    assert levels["founding level (1200 below GL)"].elevation_mm == -1650
    assert levels["top of foundation (400 thick)"].elevation_mm == -1250
    assert any(lv.kind == "other" and lv.elevation_mm == 11400 for lv in rec.levels)
    topics = {f.topic for f in rec.findings}
    assert "roof_structure" in topics and "beam_depth" in topics
    # the only unexplained dimension is the horizontal width
    assert len(rec.unexplained) == 1 and "horizontal" in rec.unexplained[0].note
    coverage = next(f for f in rec.findings if f.topic == "dimensions")
    assert "interpreted" in coverage.text
    # every level keeps its evidence
    assert all(lv.evidence.handles for lv in rec.levels)


def test_section_without_level_marks_starts_from_lowest_floor(tmp_path):
    rec = recognise_section(synthetic.section(tmp_path, marks=False), "XX")
    assert rec.datum == "lowest floor (unlabelled)"
    assert rec.storey_heights_mm == [3000, 3000, 3000]
    assert rec.plinth_height_mm is None
    assert any(f.topic == "datum" for f in rec.findings)


@pytest.mark.parametrize("file, name", [("6.SECTION- AA.dxf", "AA"), ("7.SECTION- BB.dxf", "BB")])
def test_sample_sections(sample_sheets, file, name):
    rec = recognise_section(sample_sheets[file], name)
    assert rec.storey_heights_mm == [2700] * 5
    assert rec.slab_thickness_mm == 150
    assert rec.beam_depth_mm == 450
    assert rec.parapet_height_mm == 900
    assert rec.foundation_layers == [("PCC", 150), ("BFS", 75)]
    levels = _levels(rec)
    assert levels["roof"].elevation_mm == 13500
    assert levels["parapet top"].elevation_mm == 14400
    assert [lv.storey for lv in rec.levels if lv.storey is not None and lv.kind in ("plinth", "floor")] == [0, 1, 2, 3, 4]
    assert any(f.topic == "foundation_layer" and "SAND FILLING" in f.text for f in rec.findings)
    assert any(f.topic == "roof_structure" for f in rec.findings)


def test_sample_section_aa_datum_plinth_and_founding(sample_sheets):
    rec = recognise_section(sample_sheets["6.SECTION- AA.dxf"], "AA")
    assert rec.datum == "PL"
    assert rec.plinth_height_mm == 600
    levels = _levels(rec)
    assert levels["GL"].elevation_mm == -600
    assert levels["founding level (1200 below GL)"].elevation_mm == -1800
    assert levels["top of foundation (500 thick)"].elevation_mm == -1300
    assert len(rec.unexplained) == 2  # the sloping projection's 100 and 200


def test_sample_section_bb_window_and_no_marks(sample_sheets):
    rec = recognise_section(sample_sheets["7.SECTION- BB.dxf"], "BB")
    assert rec.datum == "lowest floor (unlabelled)"
    assert rec.window_sills_mm == [900] and rec.window_heads_mm == [2100]
    assert any(f.topic == "window_sill" for f in rec.findings)
