"""Sheet role and storeys from titles and file names."""

import pytest

from b2m.cad.classify import classify, file_title


@pytest.mark.parametrize("title, role, storeys, name", [
    ("GROUND FLOOR PLAN", "floor_plan", [0], None),
    ("FIRST STOREY TO FOURTH STOREY FLOOR PLAN", "floor_plan", [1, 2, 3, 4], None),
    ("PLAN 1st to 4th", "floor_plan", [1, 2, 3, 4], None),
    ("TYPICAL FLOOR PLAN (1ST-4TH)", "floor_plan", [1, 2, 3, 4], None),
    ("SECOND & THIRD FLOOR PLAN", "floor_plan", [2, 3], None),
    ("5TH FLOOR PLAN", "floor_plan", [5], None),
    ("ROOF PLAN", "roof_plan", [], None),
    ("SITE PLAN", "site_plan", [], None),
    ("LOCATION PLAN", "site_plan", [], None),
    ("KEY PLAN", "site_plan", [], None),
    ("FRONT ELEVATION", "elevation", [], "FRONT"),
    ("REAR ELEVATION", "elevation", [], "REAR"),
    ("SECTION - AA", "section", [], "AA"),
    ("SECTION B-B", "section", [], "BB"),
    ("FOUNDATION DETAILS", "foundation", [], None),
    ("FOUNDATION PLAN", "foundation", [], None),
    ("TIE BEAM LAYOUT (GROUND FLOOR)", "beam_layout", [0], None),
])
def test_titles(title, role, storeys, name):
    result = classify(title)
    assert (result.role, result.storeys, result.name, result.decided_by) == (role, storeys, name, "title")
    assert result.question is None


@pytest.mark.parametrize("title", ["TYPICAL FLOOR PLAN", "FLOOR PLAN", "STILT FLOOR PLAN", "TIE BEAM LAYOUT"])
def test_plans_without_a_readable_storey_ask(title):
    result = classify(title)
    assert result.role in ("floor_plan", "beam_layout") and result.storeys == [] and result.question


def test_file_name_decides_when_the_title_cannot():
    result = classify(None, "2.PLAN 1st to 4th.dwg")
    assert (result.role, result.storeys, result.decided_by) == ("floor_plan", [1, 2, 3, 4], "file_name")
    assert file_title("9.TIE BEAM LAYOUT(GROUND FLOOR).dwg") == "TIE BEAM LAYOUT(GROUND FLOOR)"
    typical = classify("TYPICAL FLOOR PLAN", "2.PLAN 1st to 4th.dwg")
    assert (typical.storeys, typical.decided_by, typical.question) == ([1, 2, 3, 4], "file_name", None)


def test_title_wins_but_a_disagreeing_file_name_is_a_question():
    result = classify("GROUND FLOOR PLAN", "3.ROOF PLAN.dwg")
    assert (result.role, result.storeys, result.decided_by) == ("floor_plan", [0], "title")
    assert "ROOF PLAN" in result.question


@pytest.mark.parametrize("title", ["DOOR SCHEDULE", "SECTION AND ELEVATION", "PLAN", None])
def test_unclear_sheets_are_unknown_with_a_question(title):
    result = classify(title, "drawing.dwg")
    assert result.role == "unknown" and result.question and result.decided_by is None
