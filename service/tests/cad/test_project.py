"""Import project: the sample classified end to end, persistence, questions, assumptions and the inspect CLI."""

import json

import ezdxf
import pytest

from plinth.cad import convert, inspect, project
from tests.cad.conftest import SAMPLE_DXF

EXPECTED_ROLES = {
    "1.GROUND FLOOR PLAN.dxf": ("floor_plan", [0], None),
    "2.PLAN 1st to 4th.dxf": ("floor_plan", [1, 2, 3, 4], None),
    "3.ROOF PLAN.dxf": ("roof_plan", [], None),
    "4.SITE PLAN.dxf": ("site_plan", [], None),
    "5.FRONT ELEVATION.dxf": ("elevation", [], "FRONT"),
    "6.SECTION- AA.dxf": ("section", [], "AA"),
    "7.SECTION- BB.dxf": ("section", [], "BB"),
    "8.Foundation Details.dxf": ("foundation", [], None),
    "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf": ("beam_layout", [0], None),
}


@pytest.fixture(scope="module")
def sample_project() -> project.ImportProject:
    return project.analyse(SAMPLE_DXF)


def test_every_sample_sheet_is_classified_from_its_title(sample_project):
    assert [s.file for s in sample_project.sheets] == list(EXPECTED_ROLES)  # natural order
    for s in sample_project.sheets:
        assert (s.role, s.storeys, s.name) == EXPECTED_ROLES[s.file], s.file
        assert s.decided_by == "title" and s.units == "mm", s.file
    assert sample_project.questions == []
    assert {s.file: s.scale for s in sample_project.sheets if s.scale != 150} == {
        "1.GROUND FLOOR PLAN.dxf": 250, "4.SITE PLAN.dxf": 3000, "8.Foundation Details.dxf": 250}


def test_project_file_round_trip(monkeypatch, tmp_path):
    monkeypatch.setenv("PLINTH_HOME", str(tmp_path))
    store = project.ImportStore()
    files = [SAMPLE_DXF / "3.ROOF PLAN.dxf", SAMPLE_DXF / "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf"]
    created = store.create(files)
    path = tmp_path / "imports" / created.id / "project.json"
    assert path.is_file()
    assert [s.sha256 for s in created.sources] == [convert.sha256(f) for f in files]

    created.set_location(city="Bardhaman", municipality="Bardhaman Municipality", state="West Bengal")
    q = created.add_question("heights", "What is the floor-to-floor height?", [project.Evidence(sheet="x.dxf")],
                             options=["2700", "3000"])
    created.answer(q.id, "2700")
    created.assume("Plinth height 450 mm", basis="Bardhaman Municipality building rules")
    created.set_value("lift_count", 2)
    created.approve("review", by="architect")
    store.save(created)

    loaded = store.get(created.id)
    assert loaded == created
    assert json.loads(path.read_text(encoding="utf-8"))["questions"][0]["answered_by"] == "user"
    assert loaded.open_questions() == [] and loaded.assumptions[0].id == "A1"
    assert store.get("../escape") is None and store.get("missing") is None


def test_assumption_needs_the_project_location_first():
    p = project.ImportProject(id="t", created="now")
    q = p.add_question("heights", "Parapet height?")
    with pytest.raises(project.LocationRequired, match="ask the user for the project location first"):
        p.assume("Parapet 1000 mm", basis="typical practice")
    with pytest.raises(project.LocationRequired):
        p.answer(q.id, "1000", by="assumed", basis="typical practice")
    assert q.open and p.assumptions == []

    p.set_location(city="Kolkata", rule_set="KMC Building Rules 2009")
    p.answer(q.id, "1000", by="assumed", basis="KMC Building Rules 2009")
    assert (q.answered_by, q.answer) == ("assumed", "1000")
    assert p.assumptions[0].question == q.id and p.inputs.rule_set == "KMC Building Rules 2009"
    with pytest.raises(ValueError):
        p.assume("Something", basis=" ")


def _sheet(path, title):
    doc = ezdxf.new(units=ezdxf.units.MM)
    msp = doc.modelspace()
    msp.add_lwpolyline([(0, 0), (42000, 0), (42000, 29700), (0, 29700)], close=True)
    msp.add_lwpolyline([(500, 500), (41500, 500), (41500, 29200), (500, 29200)], close=True)
    msp.add_text(title, height=600).set_placement((15000, 2000))
    msp.add_text("SCALE- 1:50", height=400).set_placement((16000, 1000))
    for y in range(5000, 25000, 1000):
        msp.add_line((5000, y), (20000, y))
    doc.saveas(path)


def test_unknown_title_becomes_a_question_with_evidence(tmp_path):
    _sheet(tmp_path / "A-101.dxf", "%%UDOOR SCHEDULE")
    p = project.analyse(tmp_path)
    (s,) = p.sheets
    assert (s.role, s.title, s.scale, s.page) == ("unknown", "DOOR SCHEDULE", 50, None)
    role = [q for q in p.questions if q.topic == "sheet_role"]
    assert len(role) == 1 and "DOOR SCHEDULE" in role[0].text
    assert role[0].evidence == [project.Evidence(sheet="A-101.dxf", handles=[s.title_handle])]
    assert "floor_plan" in role[0].options and "unknown" not in role[0].options
    assert [q.topic for q in p.questions] == ["sheet_role"]


def test_inspect_prints_each_sheet_and_the_open_questions(tmp_path, capsys):
    _sheet(tmp_path / "1.TYPICAL FLOOR PLAN.dxf", "%%UTYPICAL FLOOR PLAN")
    assert inspect.main([str(tmp_path)]) == 0
    out = capsys.readouterr().out
    assert "role floor_plan | storeys - | decided by title" in out and "scale 1:50" in out
    assert "Open questions: 1" in out and "which storeys" in out
    assert inspect.main([str(tmp_path), "--json"]) == 0
    assert json.loads(capsys.readouterr().out)["sheets"][0]["role"] == "floor_plan"
