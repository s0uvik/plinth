"""Estimate engine units: rules format, expressions, selection, questions, Excel writer and workbook comparison."""

from __future__ import annotations

import copy
import re

import openpyxl
import pytest
from pydantic import ValidationError

from plinth.estimate import (DrawingObservation, Opening, ProjectInfo, Quantities, Railing, Storey, build_estimate,
                          compare_workbooks, crosscheck, load_rules, write_xlsx)
from plinth.estimate.engine import ordinal, roman
from plinth.estimate.ruleset import ExpressionError, Rules, evaluate


def project(typical: int = 2) -> ProjectInfo:
    return ProjectInfo(name="t", storeys=[Storey(index=0, name="G", kind="ground"),
                                          *[Storey(index=i, name=f"S{i}", kind="typical")
                                            for i in range(1, typical + 1)]])


def door(mark="D", w=900, h=2100, storey=1, count=1, material="wood", zone=None) -> Opening:
    return Opening(mark=mark, kind="door", width_mm=w, height_mm=h, storey=storey, count=count, material=material,
                   zone=zone)


def window(mark="W", w=1500, h=1200, storey=1, count=1) -> Opening:
    return Opening(mark=mark, kind="window", width_mm=w, height_mm=h, storey=storey, count=count)


def small_rules(**section_overrides) -> Rules:
    data = {
        "name": "t", "title": "T", "units": {"length": "m.", "area": "Sqm.", "volume": "Cum."},
        "constants": {"faces": 2},
        "layout": load_rules().layout.model_dump(),
        "sections": [{
            "sl": 1, "heading": "Shutters ({typical_from_ord} to {typical_to_ord})",
            "groups": [{"select": {"kind": "door", "storeys": "typical"}, "count": "per_storey",
                        "rows": [{"label": "{roman}) {mark} ({width_mm} x {height_mm})", "number": "n",
                                  "length": "W", "height": "H"}],
                        "totals": [{"label": "Total"}, {"label": "All Floor", "of": "previous", "times": "storeys"}]}],
            **section_overrides,
        }],
    }
    return Rules.model_validate(data)


# ---- format and helpers -------------------------------------------------------------------------------------------

def test_office_default_rules_load_and_validate():
    rules = load_rules("office-default")
    assert rules.title == "Door Window Estimation"
    assert [s.sl for s in rules.sections] == [1, 2, 3, 3, 4, 5, None, None, None]
    assert rules.layout.headers[6] == "VOLUME/AREA"


def test_expressions_are_arithmetic_only():
    assert evaluate("2*H + W", {"H": 2.1, "W": 0.9}) == pytest.approx(5.1)
    assert evaluate("members * n", {"members": 2, "n": 24}) == 48
    assert evaluate(0.3, {}) == 0.3
    with pytest.raises(ExpressionError, match="unknown name"):
        evaluate("x + 1", {})
    with pytest.raises(ExpressionError, match="only numbers"):
        evaluate("__import__('os')", {})
    with pytest.raises(ExpressionError):
        evaluate("W.real", {"W": 1})


def test_rules_reject_bad_shapes():
    with pytest.raises(ValidationError, match="exactly one of kind"):
        small_rules(groups=[{"select": {}, "rows": [{"label": "x"}]}])
    with pytest.raises(ValidationError, match="per_storey needs a storeys selector"):
        small_rules(groups=[{"select": {"kind": "door"}, "count": "per_storey", "rows": [{"label": "x"}]}])
    with pytest.raises(ValidationError, match="constants may not be named"):
        Rules.model_validate({**load_rules().model_dump(), "constants": {"n": 1}})


def test_roman_and_ordinal():
    assert [roman(i) for i in (1, 2, 3, 4, 5, 9, 14)] == ["i", "ii", "iii", "iv", "v", "ix", "xiv"]
    assert [ordinal(i) for i in (1, 2, 3, 4, 11, 12, 13, 21, 22)] == \
        ["1st", "2nd", "3rd", "4th", "11th", "12th", "13th", "21st", "22nd"]


# ---- engine -------------------------------------------------------------------------------------------------------

def test_per_storey_rows_totals_and_all_floor_multiplier():
    q = Quantities(project=project(3), openings=[door("D1", 800, storey=s, count=2) for s in (1, 2, 3)]
                   + [door("D", storey=s, count=3) for s in (1, 2, 3)])
    est = build_estimate(q, small_rules())
    cells = est.cells()
    assert cells["A5"] == 1 and cells["B5"] == "Shutters (1st to 3rd)"
    assert cells["B6"] == "i) D (900 x 2100)" and cells["C6"] == 3          # natural order: D before D1
    assert cells["B7"] == "ii) D1 (800 x 2100)" and cells["D7"] == 0.8 and "E7" not in cells
    assert cells["G6"].startswith("=IF(ISBLANK(D6), C6*E6*F6")
    assert cells["G8"] == "=SUM(G6:G7)" and cells["H8"] == "Sqm."
    assert cells["F9"] == "All Floor" and cells["G9"] == "=G8*3"            # 3 typical storeys, from the project
    assert est.values()["G9"] == pytest.approx((3 * 0.9 * 2.1 + 2 * 0.8 * 2.1) * 3)
    assert est.questions == []


def test_floors_that_differ_are_asked_not_averaged():
    q = Quantities(project=project(2), openings=[door(storey=1, count=3), door(storey=2, count=4)])
    est = build_estimate(q, small_rules())
    row = est.row(6)
    assert row.number is None and row.quantity is None and row.quantity_formula is None
    assert len(est.questions) == 1 and "storey 1: 3, storey 2: 4" in est.questions[0].text


def test_a_mark_with_two_sizes_is_asked():
    q = Quantities(project=project(1), openings=[door(storey=1), door(w=1000, storey=1)])
    est = build_estimate(q, small_rules())
    assert any("Mark D has more than one size (900 x 2100, 1000 x 2100)" in x.text for x in est.questions)


def test_openings_no_rule_measures_are_asked():
    q = Quantities(project=project(1), openings=[door(storey=1), door(storey=0), window(storey=1)],
                   railings=[Railing(kind="stair", length_mm=2800, count=2)])
    est = build_estimate(q, small_rules())
    texts = [x.text for x in est.questions if x.topic == "coverage"]
    assert len(texts) == 3
    assert any("storey 0" in t for t in texts) and any("window W" in t for t in texts)
    assert any("stair railing" in t for t in texts)


def test_unknown_material_is_not_defaulted():
    rules = small_rules(groups=[{"select": {"kind": "door", "material": "wood"}, "rows": [{"label": "{mark}",
                                                                                           "length": "W"}]}])
    est = build_estimate(Quantities(project=project(1), openings=[door(storey=1, material=None)]), rules)
    assert [r.role for r in est.rows] == []  # nothing selected, so the section is left out
    assert "material not known" in est.questions[0].text


def test_zone_storey_and_number_factors():
    rules = small_rules(heading=None, groups=[
        {"select": {"kind": "door", "zone": "terrace"}, "units_on": "rows",
         "rows": [{"label": "{mark}", "number": ["n", "faces"], "length": "W", "height": "H"}]}])
    q = Quantities(project=project(1), openings=[door(storey=1), door(storey=1, zone="terrace", count=2)])
    est = build_estimate(q, rules)
    cells = est.cells()
    assert cells["A5"] == 1 and cells["B5"] == "D" and cells["C5"] == "=2*2" and cells["H5"] == "Sqm."
    assert est.values()["C5"] == 4


def test_one_dimension_rows_use_the_simple_formula_and_plus_sums():
    rules = load_rules()
    q = Quantities(project=project(2), openings=[window(storey=s, count=2) for s in (1, 2)]
                   + [window("W1", 1000, storey=s) for s in (1, 2)])
    est = build_estimate(q, rules)
    frame = [r for r in est.rows if r.path and r.path[0] == "Window Frame (Total)"]
    tb = [r for r in frame if r.item == "(a) Bottom & Top Frame"]
    assert [r.number_formula for r in tb] == ["=4*2", "=2*2"]
    assert tb[0].quantity_formula == f"=C{tb[0].row}*D{tb[0].row}"
    total = next(r for r in frame if r.total_label == "Top bottom")
    assert total.quantity_formula == f"=G{tb[0].row}+G{tb[1].row}" and total.units is None
    assert total.quantity == pytest.approx(8 * 1.5 + 4 * 1.0)


def test_heading_on_previous_row_and_sections_without_items_are_left_out():
    rules = load_rules()
    q = Quantities(project=project(1), openings=[window(storey=1), door("D", storey=1)])
    est = build_estimate(q, rules)
    wood_paint = next(r for r in est.rows if r.item == "Wood paint")
    assert wood_paint.total_label == "All Floor"           # shares the window shutter section's last row
    assert not any(r.item and r.item.startswith("PVC") for r in est.rows)


# ---- writer and comparison ----------------------------------------------------------------------------------------

@pytest.fixture
def written(tmp_path):
    rules = load_rules()
    q = Quantities(project=project(2), openings=[door(storey=s, count=2) for s in (1, 2)]
                   + [window(storey=s, count=3) for s in (1, 2)],
                   railings=[Railing(kind="stair", length_mm=2800, count=4)])
    est = build_estimate(q, rules)
    return rules, est, write_xlsx(est, rules, tmp_path / "e.xlsx")


def test_writer_office_layout(written):
    rules, est, path = written
    ws = openpyxl.load_workbook(path).active
    assert ws["A1"].value == "Door Window Estimation" and "A1:I3" in {str(m) for m in ws.merged_cells.ranges}
    assert [ws.cell(4, c).value for c in range(1, 10)] == rules.layout.headers
    assert ws.column_dimensions["B"].width == pytest.approx(39.7109375)
    for coord, value in est.cells().items():
        assert ws[coord].value == value


def test_comparing_a_workbook_with_itself_is_exact(written):
    _, est, path = written
    rep = compare_workbooks(path, path, est)
    assert rep.exact, rep.text()
    assert "EXACT MATCH" in rep.text()


def test_comparison_realigns_rows_after_an_inserted_row(written, tmp_path):
    _, est, path = written
    wb = openpyxl.load_workbook(path)
    ws = wb.active
    ws.insert_rows(8)
    ws["B8"] = "extra line"
    for row in ws.iter_rows(min_row=9):                    # Excel would shift the references; do it by hand
        for c in row:
            if isinstance(c.value, str) and c.value.startswith("="):
                c.value = re.sub(r"([A-Z])(\d+)", lambda m: m.group(1) + str(int(m.group(2)) + (int(m.group(2)) >= 8)),
                                 c.value)
    ref = tmp_path / "ref.xlsx"
    wb.save(ref)
    rep = compare_workbooks(path, ref)
    kinds = sorted(d.kind for d in rep.differences)
    assert kinds == ["layout", "missing_row"], rep.text()   # no formula differences after re-mapping


def test_comparison_reports_typed_values_and_text(written, tmp_path):
    _, est, path = written
    wb = openpyxl.load_workbook(path)
    ws = wb.active
    g_cell = next(f"G{r.row}" for r in est.rows if r.quantity_formula and r.role == "item")
    ws[g_cell] = 1.0
    b_cell = next(f"B{r.row}" for r in est.rows if r.role == "item")
    ws[b_cell] = ws[b_cell].value.upper()
    ref = tmp_path / "ref.xlsx"
    wb.save(ref)
    rep = compare_workbooks(path, ref, est)
    found = {(d.cell, d.kind, d.cosmetic) for d in rep.differences}
    assert (g_cell, "formula_vs_constant", False) in found and (g_cell, "value", False) in found
    assert (b_cell, "text", True) in found
    assert len(rep.questions()) == 1  # one question per row


def test_crosscheck_counts_and_unverified():
    q = Quantities(project=project(2), openings=[door(storey=s, count=2) for s in (1, 2)])
    obs = [DrawingObservation(source="plan", storeys=[1, 2], kind="door", mark="d", count_per_storey=2),
           DrawingObservation(source="plan", storeys=[1, 2], kind="door", width_mm=900, count_per_storey=3)]
    check = crosscheck(q, obs)
    assert [f.status for f in check.findings] == ["agrees", "disagrees"]
    assert len(check.questions) == 1 and "drawn 900 wide" in check.questions[0]
    assert check.unverified[0] == "D: height (900 x 2100)"


def test_rules_are_not_mutated_by_a_build():
    rules = load_rules()
    before = copy.deepcopy(rules.model_dump())
    build_estimate(Quantities(project=project(1), openings=[door(storey=1)]), rules)
    assert rules.model_dump() == before


def test_units_on_heading_for_a_section_and_a_group():
    rules = small_rules(units_on="heading", groups=[
        {"select": {"kind": "door"}, "rows": [{"label": "{mark}", "length": "W", "height": "H"}]},
        {"heading": "Windows", "units_on": "heading", "select": {"kind": "window"},
         "rows": [{"label": "{mark}", "length": "W"}]}])
    q = Quantities(project=project(1), openings=[door(storey=1), window(storey=1)])
    cells = build_estimate(q, rules).cells()
    assert cells["H5"] == "Sqm." and cells["B7"] == "Windows" and cells["H7"] == "m."
    assert "H6" not in cells and "H8" not in cells
