"""The G+4 sample: the office's own door/window estimate, reproduced from the quantities it implies, and those
quantities checked against what the drawings show.

Two separate records live here:
- REFERENCE quantities: read off the office's Excel (door-window-estimate.xlsx), nothing added.
- DRAWING observations: counted on the DXF sheets (mark texts and drawn door symbols), verified below with ezdxf.
Where they disagree the test expects a question, never a correction.
"""

from __future__ import annotations

import re
from collections import Counter
from pathlib import Path

import pytest

from b2m.estimate import (DrawingObservation, Opening, ProjectInfo, Quantities, Railing, Storey, build_estimate,
                          compare_workbooks, crosscheck, load_rules, write_xlsx)

SAMPLE = Path(__file__).resolve().parents[1] / "fixtures" / "samples" / "g4-sample"
REFERENCE_XLSX = SAMPLE / "door-window-estimate.xlsx"
TYPICAL = [1, 2, 3, 4]

PROJECT = ProjectInfo(
    name="G+4 sample", location="Golapbag More, GT Road, Bardhaman",
    storeys=[Storey(index=0, name="Ground floor (stilt)", kind="ground"),
             *[Storey(index=i, name=f"Storey {i}", kind="typical") for i in TYPICAL],
             Storey(index=5, name="Roof", kind="roof")])


def reference_quantities() -> Quantities:
    """What the office's Excel implies, row by row (per typical floor unless noted)."""
    ref = "door-window-estimate.xlsx"
    ops: list[Opening] = []
    for s in TYPICAL:
        ops += [
            # rows 7-9, 15-17: wood frames and shutters per floor; rows 54-61: wood paint 16 / 48 = x4 floors
            Opening(mark="Entrance", kind="door", width_mm=1000, height_mm=2100, storey=s, material="wood", count=4,
                    source=f"{ref} rows 7, 15, 54"),
            Opening(mark="D", kind="door", width_mm=900, height_mm=2100, storey=s, material="wood", count=12,
                    source=f"{ref} rows 8, 16, 57"),
            Opening(mark="D1", kind="door", width_mm=800, height_mm=2100, storey=s, material="wood", count=12,
                    source=f"{ref} rows 9, 17, 60"),
            # rows 22, 26: PVC D1, 8 per floor (+ 1 on the ground floor below)
            Opening(mark="D1", kind="door", width_mm=800, height_mm=2100, storey=s, material="pvc", count=8,
                    source=f"{ref} rows 22, 26"),
            # rows 46-50 (shutters, per floor) and 28-42 (frames, x4 floors)
            Opening(mark="W", kind="window", width_mm=1500, height_mm=1200, storey=s, count=24, source=f"{ref} row 46"),
            # W1: the shutter rows (47, 65) say 1.0 m wide, the frame row (32) says 1.05 m. One opening has one
            # width, so 1000 is used here only to run the comparison; D32 is then reported, not decided.
            Opening(mark="W1", kind="window", width_mm=1000, height_mm=1200, storey=s, count=4, source=f"{ref} row 47"),
            Opening(mark="W2", kind="window", width_mm=640, height_mm=640, storey=s, count=4, source=f"{ref} row 48"),
            Opening(mark="W3", kind="window", width_mm=500, height_mm=640, storey=s, count=4, source=f"{ref} row 49"),
            Opening(mark="W4", kind="window", width_mm=1000, height_mm=1600, storey=s, count=1, source=f"{ref} row 50"),
        ]
    ops += [
        Opening(mark="D1", kind="door", width_mm=800, height_mm=2100, storey=0, material="pvc", count=1,
                source=f"{ref} row 25"),
        Opening(mark="D", kind="door", width_mm=900, height_mm=2100, storey=5, zone="terrace", material="wood",
                count=2, source=f"{ref} rows 12, 20"),
    ]
    rails = [
        Railing(kind="stair", length_mm=2800, count=10, source=f"{ref} row 72"),
        Railing(kind="verandah", subkind="main", length_mm=8125, count=16, source=f"{ref} row 74"),
        Railing(kind="verandah", subkind="attached", length_mm=8500, count=16, source=f"{ref} row 75"),
    ]
    return Quantities(project=PROJECT, openings=ops, railings=rails)


# What the drawings show, counted on the DXF sheets (verified by test_drawing_observations_match_the_dxf).
PLAN_TYPICAL, PLAN_GROUND, PLAN_ROOF = "2.PLAN 1st to 4th.dxf", "1.GROUND FLOOR PLAN.dxf", "3.ROOF PLAN.dxf"
MARK_TEXTS = {  # sheet -> (storeys it stands for, {mark text: count on the sheet})
    PLAN_TYPICAL: (TYPICAL, {"D": 12, "D1": 20, "w": 24, "w1": 4, "w2": 4, "w3": 4, "w4": 1}),
    PLAN_GROUND: ([0], {"D1": 1}),
    PLAN_ROOF: ([5], {"D": 2}),
}
DOOR_SYMBOLS = {  # sheet -> {drawn door width (swing radius) mm: count}
    PLAN_TYPICAL: {1000: 4, 900: 16, 800: 16},
    PLAN_GROUND: {800: 1},
    PLAN_ROOF: {900: 2},
}


def drawing_observations() -> list[DrawingObservation]:
    obs = []
    for sheet, (storeys, marks) in MARK_TEXTS.items():
        for mark, n in marks.items():
            kind = "window" if mark.lower().startswith("w") else "door"
            obs.append(DrawingObservation(source=f"{sheet}: TEXT {mark!r} x{n}", storeys=storeys, kind=kind,
                                          mark=mark, count_per_storey=n))
    for sheet, widths in DOOR_SYMBOLS.items():
        for w, n in widths.items():
            obs.append(DrawingObservation(source=f"{sheet}: door symbols r={w} x{n}", storeys=MARK_TEXTS[sheet][0],
                                          kind="door", width_mm=w, count_per_storey=n))
    return obs


# Cells where the office's Excel contradicts itself; no single consistent set of quantities and rules gives them.
EXPECTED_DIFFERENCES = {
    ("G22", "formula_vs_constant"),  # typed 160; its own row gives 33 x 5.0 = 165 (160 = 32 x 5, ground one left out?)
    ("G22", "value"),
    ("H22", "text"),                 # 'Sqm.' for a running length; the sheet's other lengths say 'm.'
    ("C28", "blank"),                # =24*4 on W's heading only; W1-W4 headings leave NUMBER blank
    ("I28", "text"),                 # 'Total: -24' vs 'Total:- 4' on the other marks
    ("D32", "number"),               # W1 frame width 1.05 vs 1.0 in the shutter (D47) and steel paint (D65) rows
    ("G32", "value"),                # follows from D32
    ("G43", "value"),                # follows from D32
    ("B47", "text"),                 # 'ii)w1' vs 'ii)W1' in the frame rows
    ("B65", "text"),
}


@pytest.fixture(scope="module")
def sample(tmp_path_factory):
    rules = load_rules("office-default")
    est = build_estimate(reference_quantities(), rules)
    out = write_xlsx(est, rules, tmp_path_factory.mktemp("estimate") / "door-window-estimate.xlsx")
    return est, out, compare_workbooks(out, REFERENCE_XLSX, est)


def test_engine_reproduces_the_reference_except_its_own_contradictions(sample):
    est, out, report = sample
    print(report.text())
    assert {(d.cell, d.kind) for d in report.differences} == EXPECTED_DIFFERENCES
    assert report.rows_compared == 69 and report.cells_compared >= 250 and report.values_compared >= 60
    assert not [d for d in report.differences if d.kind in ("missing_row", "extra_row", "layout", "formula")]
    cosmetic = {d.cell for d in report.differences if d.cosmetic}
    assert cosmetic == {"I28", "B47", "B65"}


def test_every_contradiction_becomes_a_question(sample):
    _, _, report = sample
    questions = report.questions()
    assert len(questions) == 6  # rows 22, 28, 32, 43, 47, 65
    q22 = next(q for q in questions if q.startswith("Row 22"))
    assert "160" in q22 and "165" in q22 and "Sqm." in q22
    q32 = next(q for q in questions if q.startswith("Row 32"))
    assert "ii)W1" in q32 and "1.05" in q32


def test_python_values_match_the_cached_excel_values(sample):
    est, _, _ = sample
    vals = est.values()
    assert vals["G10"] == pytest.approx(1.42)
    assert vals["G18"] == pytest.approx(51.24)
    assert vals["G52"] == pytest.approx(210.0736)
    assert vals["G62"] == pytest.approx(590.94)
    assert vals["G44"] == pytest.approx(322.56)
    assert vals["G76"] == pytest.approx(294)
    assert est.cells()["G52"] == "=G51*4" and est.cells()["G43"] == "=G29+G32+G35+G38+G41"


def test_estimate_questions_on_the_reference_quantities(sample):
    est, _, _ = sample
    # every opening and railing is measured by some rule; only the rules' duplicate SL. NO 3 is asked
    assert [q.topic for q in est.questions] == ["rules"]
    assert "SL. NO 3" in est.questions[0].text


def test_crosscheck_reference_quantities_against_the_drawings():
    check = crosscheck(reference_quantities(), drawing_observations())
    print(check.text())
    status = {f.subject: f.status for f in check.findings}
    # marks agree: D 12, D1 20 (= 12 wood + 8 PVC), w..w4, ground D1 1, terrace D 2; entrance doors by width
    for subject in ("door mark 'D', storeys 1-4", "door mark 'D1', storeys 1-4", "window mark 'w', storeys 1-4",
                    "window mark 'w1', storeys 1-4", "window mark 'w4', storeys 1-4", "door mark 'D1', storey 0",
                    "door mark 'D', storey 5", "doors drawn 1000 wide, storeys 1-4"):
        assert status[subject] == "agrees", subject
    # the drawn door widths disagree with the marks the office followed
    assert status["doors drawn 900 wide, storeys 1-4"] == "disagrees"
    assert status["doors drawn 800 wide, storeys 1-4"] == "disagrees"
    assert status["door 'Entrance', storeys 1-4"] == "not_on_drawing"
    assert status["material of D1, storeys 1-4"] == "not_on_drawing"
    questions = check.questions
    assert len(questions) == 4
    assert any("drawn 900 wide" in q and "16 per storey" in q and "12 per storey" in q for q in questions)
    assert any("drawn 800 wide" in q and "16 per storey" in q and "20 per storey" in q for q in questions)
    assert any("Which D1 doors are which material" in q for q in questions)
    assert any("W1: width and height (1000 x 1200)" == u for u in check.unverified)


# ---- the recorded drawing facts, verified on the DXF ---------------------------------------------------------------

MARK = re.compile(r"^(D\d*|w\d*)$", re.IGNORECASE)


@pytest.mark.parametrize("sheet", [PLAN_TYPICAL, PLAN_GROUND, PLAN_ROOF])
def test_drawing_observations_match_the_dxf(sheet):
    ezdxf = pytest.importorskip("ezdxf")
    doc = ezdxf.readfile(SAMPLE / "dxf" / sheet)
    msp = doc.modelspace()
    texts = Counter(t.dxf.text.strip() for t in msp.query("TEXT") if MARK.match(t.dxf.text.strip()))
    assert dict(texts) == MARK_TEXTS[sheet][1]
    # a door symbol: an anonymous (dynamic) block whose only arcs are 4 swing arcs of one radius = the leaf width
    doors: Counter = Counter()
    for ins in msp.query("INSERT"):
        arcs = [e.dxf.radius for e in doc.blocks.get(ins.dxf.name) if e.dxftype() == "ARC"]
        if ins.dxf.name.startswith("*U") and len(arcs) == 4 and len({round(r) for r in arcs}) == 1:
            doors[round(arcs[0])] += 1
    assert dict(doors) == DOOR_SYMBOLS[sheet]
