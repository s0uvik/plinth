"""Section marks on plans (T4): labelled arrows joined by a cut line, tails removed, jogs kept."""

from plinth.cad import marks
from plinth.cad.annot import name_of
from plinth.cad.sections import main
from tests.cad import synthetic
from tests.cad.conftest import SAMPLE_DXF


def _plan(tmp_path):
    d = synthetic.Drawing()
    d.rect(0, 0, 10000, 8000)  # something to cut through
    # straight section A with tails: cut along y=4000, looking +y
    d.msp.add_lwpolyline([(-1000, 4500), (-1000, 4000), (11000, 4000), (11000, 4500)],
                         dxfattribs={"layer": "0", "color": 6, "linetype": "CENTER"})
    d.arrow((-1000, 5000), (-1000, 4500), 80)
    d.arrow((11000, 5000), (11000, 4500), 80)
    d.text("A", (-1200, 5100), height=300)
    d.text("A", (10800, 5100), height=300)
    # jogged section B looking -x
    d.msp.add_lwpolyline([(6000, 9000), (5000, 9000), (5000, 3000), (3000, 3000), (3000, -1000), (2000, -1000)],
                         dxfattribs={"layer": "0", "color": 6, "linetype": "CENTER"})
    d.arrow((1500, -1000), (2000, -1000), 80)
    d.arrow((5500, 9000), (6000, 9000), 80)
    d.text("B", (1200, -1400), height=300)
    d.text("B", (5300, 9100), height=300)
    # a stair arrow with no letter: not a section mark
    d.msp.add_lwpolyline([(7000, 1000), (7000, 3000)], dxfattribs={"layer": "0", "color": 6})
    d.arrow((7000, 3500), (7000, 3000), 80)
    return d.sheet(tmp_path, "plan.dxf")


def test_marks_straight_and_jogged(tmp_path):
    sh = _plan(tmp_path)
    found, findings = marks.find_marks(name_of(sh), sh.drawing_entities)
    by = {m.label: m for m in found}
    assert set(by) == {"A", "B"}
    assert by["A"].cut == [(-1000, 4000), (11000, 4000)] or by["A"].cut == [(11000, 4000), (-1000, 4000)]
    assert round(by["A"].view[0], 6) == 0 and round(by["A"].view[1], 6) == 1
    assert len(by["B"].cut) == 4 and round(by["B"].view[0], 6) == -1
    assert not [f for f in findings if f.severity != "info"]


def test_mark_label_from_section_name():
    assert marks.mark_label("AA") == "A" and marks.mark_label("1-1") == "1" and marks.mark_label("X") == "X"


def test_cli_prints_the_whole_set(capsys):
    assert main([str(SAMPLE_DXF)]) == 0
    out = capsys.readouterr().out
    for word in ("SECTION AA", "SECTION BB", "FOUNDATION", "BEAM LAYOUT", "REGISTRATION", "rule:"):
        assert word in out
