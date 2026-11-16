"""Registration (T4): sheets into one building frame; sections vertically; cut lines from plan marks."""

import math

import pytest

from b2m.cad import register
from b2m.cad.register import Features, FLine, FPoint
from b2m.cad.structure import recognise_set


def _features(name, pts, transform=lambda p: p):
    return Features(name, points=[FPoint("column", "250x500", transform(p), [f"h{i}"]) for i, p in enumerate(pts)])


ASYMMETRIC = [(0, 0), (4000, 0), (9000, 0), (0, 5000), (4000, 5000), (2000, 8000)]


def test_quarter_turn_and_shift_are_recovered():
    ref = _features("ref", ASYMMETRIC)
    # the moving sheet draws the same columns turned -90 degrees and shifted; +90 brings them back
    mov = _features("mov", ASYMMETRIC, lambda p: (p[1] + 100, -p[0] - 50))
    reg, findings = register.register(ref, mov)
    assert reg.rotation_deg == 90 and reg.residual_mm < 1e-6
    tr = register.transform_of(reg)
    assert all(math.dist(tr.apply(m.xy), r.xy) < 1e-6 for m, r in zip(mov.points, ref.points))
    assert not [f for f in findings if f.severity != "info"]


def test_small_rotation_is_refined():
    a = math.radians(0.2)
    ref = _features("ref", ASYMMETRIC)
    mov = _features("mov", ASYMMETRIC, lambda p: (math.cos(a) * p[0] + math.sin(a) * p[1] + 7,
                                                 -math.sin(a) * p[0] + math.cos(a) * p[1] - 3))
    reg, _ = register.register(ref, mov, tol=50)
    assert reg.rotation_deg == pytest.approx(0.2, abs=1e-6) and reg.residual_mm < 1e-3


def test_symmetric_sheet_is_said_not_asked():
    square = [(0, 0), (6000, 0), (0, 4000), (6000, 4000)]
    reg, findings = register.register(_features("ref", square), _features("mov", square, lambda p: (p[0] + 10, p[1])))
    assert reg.rotation_deg == 0 and reg.translation == (-10, 0)
    assert [f.severity for f in findings] == ["info"]


def test_symmetric_features_with_asymmetric_content_is_a_question():
    square = [(0, 0), (6000, 0), (0, 4000), (6000, 4000)]
    content = _features("mov", square)
    content.lines.append(FLine("beam 250", (0, 0), (6000, 0), ["b"]))  # only along one side
    reg, findings = register.register(_features("ref", square), _features("mov", square), content)
    assert any(f.severity == "check" and f.topic == "registration" and len(f.options) >= 2 for f in findings)


def test_too_few_matches_is_blocking():
    reg, findings = register.register(_features("ref", ASYMMETRIC), _features("mov", [(1, 1), (2, 2)]))
    assert reg is None and findings[0].severity == "blocking"


@pytest.fixture(scope="module")
def sample_building(sample_sheets):
    return recognise_set(list(sample_sheets.values()))


def test_sample_registrations(sample_building):
    regs = {r.sheet: r for r in sample_building.registrations}
    frame = "2.PLAN 1st to 4th.dxf"
    assert regs[frame].translation == (0, 0) and regs[frame].residual_mm == 0
    for sheet in ("1.GROUND FLOOR PLAN.dxf", "3.ROOF PLAN.dxf", "8.Foundation Details.dxf",
                  "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf"):
        assert regs[sheet].frame == "plan" and regs[sheet].residual_mm <= 1.0, sheet
        assert abs(regs[sheet].rotation_deg) < 1e-3, sheet
    # each floor is drawn at its own origin
    assert regs["1.GROUND FLOOR PLAN.dxf"].translation == pytest.approx((-13186.7, -11860.5), abs=1)
    assert regs["3.ROOF PLAN.dxf"].translation == pytest.approx((62, -1023.4), abs=1)
    assert regs["9.TIE BEAM LAYOUT(GROUND FLOOR).dxf"].translation == pytest.approx((181, 643.2), abs=1)
    # the tie beam layout is symmetric: said, not asked
    assert any(f.topic == "registration" and f.severity == "info" and "symmetric" in f.text
               for f in sample_building.findings)
    assert not any(f.topic == "registration" and f.severity != "info" for f in sample_building.findings)
    # sections: AA by its PL mark, BB by repeating AA's floor levels
    assert regs["6.SECTION- AA.dxf"].frame == "vertical"
    assert regs["7.SECTION- BB.dxf"].method.startswith("floor levels matched")


def test_sample_cut_lines(sample_building):
    aa, bb = sample_building.sections
    assert aa.cut_sheet == "2.PLAN 1st to 4th.dxf" and aa.view_direction == (0, 1)
    (x0, y0), (x1, y1) = aa.cut_line
    assert y0 == y1 and abs(x1 - x0) > 30000
    assert bb.cut_line is None and len(bb.cut_path) == 4 and bb.view_direction == (-1, 0)
    assert bb.datum == "PL"
    several = [f for f in sample_building.findings if f.id.startswith("section-cut-several")]
    assert len(several) == 2 and all(f.severity == "info" for f in several)


def test_sample_clear_height_rule_finding(sample_building):
    aa = sample_building.sections[0]
    rule = [f for f in aa.findings if f.topic == "rule"]
    assert rule and "2750" in rule[0].text and "2550" in rule[0].text
    assert aa.storey_heights_mm == [2700] * 5  # a finding, not a fix
