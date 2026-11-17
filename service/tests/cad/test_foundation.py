"""Foundation recognition (T4): footings, founding depth, column sections, rebar notes tied to members."""

from plinth.cad.foundation import recognise_foundation
from tests.cad import synthetic


def test_synthetic_footings_section_and_column(tmp_path):
    rec = recognise_foundation(synthetic.foundation(tmp_path))
    assert [(f.kind, f.width_mm, f.length_mm) for f in rec.footings] == [("isolated", 2000, 2000)] * 2
    assert all(f.founding_depth_mm == 1500 and f.depth_mm == 400 for f in rec.footings)
    assert all(f.column_id for f in rec.footings) and len(rec.plan_columns) == 2
    assert any(f.topic == "footing_size" for f in rec.findings)  # the second footing is not dimensioned
    assert [(c.width_mm, c.depth_mm) for c in rec.column_sections] == [(300, 300)]
    by = {(r.member, r.role): r for r in rec.rebar}
    main = by[("column 300x300", "longitudinal")]
    assert main.bar_dia_mm == 16 and main.count == 4
    stirrup = by[("column 300x300", "stirrup")]
    assert stirrup.bar_dia_mm == 8 and stirrup.legs == 2 and stirrup.spacing_mm == 150
    assert by[("column 300x300", "cover")].cover_mm == 40
    footing_bar = next(r for r in rec.rebar if r.member == "footing")
    assert footing_bar.bar_dia_mm is None and footing_bar.spacing_mm == 200
    assert any(f.topic == "rebar" and "#10" in f.text for f in rec.findings)  # a bar number is a question
    assert all(r.evidence.handles for r in rec.rebar)


def test_sample_foundation(sample_sheets):
    rec = recognise_foundation(sample_sheets["8.Foundation Details.dxf"])
    [raft] = rec.footings
    assert (raft.kind, raft.width_mm, raft.length_mm, raft.depth_mm, raft.founding_depth_mm) == (
        "raft", 32250, 19250, 500, 1200)
    assert len(rec.plan_columns) == 56
    assert sorted((c.width_mm, c.depth_mm) for c in rec.column_sections) == [(250, 250), (500, 250)]
    specs = {(r.member, r.role, r.bar_dia_mm, r.spacing_mm, r.count, r.legs, r.cover_mm) for r in rec.rebar}
    assert ("raft F1", "main", 16, 150, None, None, None) in specs
    assert ("raft F1", "distribution", 16, 150, None, None, None) in specs
    assert ("column on raft F1", "stirrup", 8, 200, None, None, None) in specs
    assert ("column 250x250", "stirrup", 8, None, None, 2, None) in specs
    assert ("column 250x500", "stirrup", 8, None, None, 3, None) in specs
    assert ("column 250x250", "longitudinal", 16, None, 4, None, None) in specs
    assert ("column 250x500", "longitudinal", 16, None, 6, None, None) in specs
    assert {r.cover_mm for r in rec.rebar if r.role == "cover"} == {75, 60, 40}
    assert not any(r.member == "unlinked" for r in rec.rebar)
    # the sheet's own SECTION- AA is namespaced to the sheet
    assert any(f.topic == "section_mark" and "8.Foundation Details.dxf#" in f.text for f in rec.findings)
