"""Beam layout recognition (T4): beams from edge pairs along centre lines, grids, column points."""

from b2m.cad.beams import recognise_beam_layout
from tests.cad import synthetic


def test_synthetic_frame(tmp_path):
    rec = recognise_beam_layout(synthetic.beam_layout(tmp_path), 0)
    beams = sorted((b.start, b.end, b.width_mm) for b in rec.beams)
    assert beams == [((0, 0), (0, 5000), 300), ((0, 0), (6000, 0), 300), ((0, 5000), (6000, 5000), 300),
                     ((6000, 0), (6000, 5000), 300)]
    assert all(b.depth_mm is None for b in rec.beams)
    assert any(f.topic == "beam_depth" for f in rec.findings)
    assert len(rec.grids) == 4 and len(rec.column_points) == 4 and len(rec.column_evidence) == 4
    assert rec.unexplained == []  # every edge belongs to a beam, the dimension runs between grid lines


def test_sample_tie_beams(sample_sheets):
    rec = recognise_beam_layout(sample_sheets["9.TIE BEAM LAYOUT(GROUND FLOOR).dxf"], 0)
    assert len(rec.beams) == 23 and {b.width_mm for b in rec.beams} == {250}
    assert len(rec.column_points) == 34 and len(rec.grids) == 17
    xs = sorted({b.start[0] for b in rec.beams} | {b.end[0] for b in rec.beams})
    assert xs[0] == 5619 and xs[-1] == 35619  # beams run grid to grid (30000 apart)
    assert any(f.topic == "column" and "POINTS1" in f.text for f in rec.findings)
    assert [e.note for e in rec.unexplained] == ["4250: does not run between centre lines"]
