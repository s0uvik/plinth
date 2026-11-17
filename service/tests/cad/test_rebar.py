"""Rebar note parser (T4): the usual spellings."""

import pytest

from plinth.cad.rebar import parse


@pytest.mark.parametrize("text, dia, spacing, count, legs, cover, role", [
    ("Ø16mm @150 c/c", 16, 150, None, None, None, None),
    ("16 dia @ 150 C/C", 16, 150, None, None, None, None),
    ("%%c12 @ 100", 12, 100, None, None, None, None),
    ("2 legged", None, None, None, 2, None, "stirrup"),
    ("3-legged", None, None, None, 3, None, "stirrup"),
    ("clear cover 40 mm", None, None, None, None, 40, "cover"),
    ("75 mm Clear Cover", None, None, None, None, 75, "cover"),
    ("Ø16mm Main Bar\n@150 (C/C)", 16, 150, None, None, None, "main"),
    ("Ø16mm Distribution Bar\n@150 (C/C)", 16, 150, None, None, None, "distribution"),
    ("Ø 8 Stirrup \n@200mm (C/C)", 8, 200, None, None, None, "stirrup"),
    ("Ø8mm 2 Leggeg Stirrup", 8, None, None, 2, None, "stirrup"),
    ("Ø16 mm Dia Bar", 16, None, None, None, None, None),
    ("4-16Ø", 16, None, 4, None, None, None),
    ("4 nos 16 dia", 16, None, 4, None, None, None),
    ("4T16", 16, None, 4, None, None, None),
    ("8 dia 2L stirrups @ 150 c/c", 8, 150, None, 2, None, "stirrup"),
    ("T10 @ 200 c/c", 10, 200, None, None, None, None),
])
def test_spellings(text, dia, spacing, count, legs, cover, role):
    r = parse(text)
    assert (r.bar_dia_mm, r.spacing_mm, r.count, r.legs, r.cover_mm, r.role) == (dia, spacing, count, legs, cover, role)


def test_bar_number_is_kept_not_converted():
    r = parse("#8 @200")
    assert r.bar_mark == "#8" and r.bar_dia_mm is None and r.spacing_mm == 200


def test_plain_text_is_not_rebar():
    assert not parse("150 MM THICK PCC").is_rebar
    assert not parse("LIFT").is_rebar
