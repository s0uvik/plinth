"""The drawing set of an imported project (T10) on the G+4 sample, against a fake Revit.

The sheet plan follows the source sheets (page order, page labels, titles, scales, A4 paper from the 277 x 190 mm
border); the fake Revit answers like the add-in so the command sequence, the recording and the comparison with the
source can be pinned without Revit.
"""

import itertools
import json
import math

import pytest

from b2m.cad import project as cad_project, structure
from b2m.cad.recognise import recognise_plan
from b2m.imported import to_plan
from b2m.imported.drawings import (ANSWER_TITLE_BLOCK, PlannedSheet, SheetPlan, SourceDimension, answer_elevation,
                                   choose_title_blocks, compare, make_import_drawing_set, paper_for, plan_sheet_set)
from tests.cad.conftest import SAMPLE, SAMPLE_DXF

TYPICAL, GROUND, ROOF = "2.PLAN 1st to 4th.dxf", "1.GROUND FLOOR PLAN.dxf", "3.ROOF PLAN.dxf"
ELEVATION, AA, BB = "5.FRONT ELEVATION.dxf", "6.SECTION- AA.dxf", "7.SECTION- BB.dxf"
FOUNDATION, TIE, SITE = "8.Foundation Details.dxf", "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf", "4.SITE PLAN.dxf"
FRONT = {answer_elevation(ELEVATION): "north"}
A4 = {"min_x": 0, "min_y": 0, "max_x": 297, "max_y": 210, "width": 297, "height": 210, "center_x": 148.5, "center_y": 105}


@pytest.fixture(scope="module")
def sample(sample_sheets):
    building = structure.recognise_set(list(sample_sheets.values()), recognise_plan)
    answers = json.loads((SAMPLE / "answers-test.json").read_text(encoding="utf-8"))
    result = to_plan(building, {k: v for k, v in answers.items() if not k.startswith("_")})
    project = cad_project.analyse(SAMPLE_DXF)
    created = {key: 100000 + i for i, key in enumerate(result.sources)}  # every op built
    return project, building, result, sample_sheets, created


def _views(plan, file):
    return next(s for s in plan.sheets if s.file == file).views


def test_paper_from_the_border():
    assert paper_for((277, 190)) == ("A4", (297.0, 210.0))
    assert paper_for((190, 277)) == ("A4", (210.0, 297.0))
    assert paper_for((400, 287)) == ("A3", (420.0, 297.0))
    assert paper_for((2000, 1000)) is None


def test_sheet_plan_follows_the_source(sample):
    project, building, result, sheets, _ = sample
    plan = plan_sheet_set(project, building, result, sheets, FRONT)
    assert plan.questions == []
    assert [(s.number, s.title, s.scale, s.page) for s in plan.sheets] == [
        ("28", "GROUND FLOOR PLAN", 250, "28 I Page"),
        ("29", "FIRST STOREY TO FOURTH STOREY FLOOR PLAN", 150, "29 I Page"),
        ("30", "ROOF PLAN", 150, "30 I Page"),
        ("31", "FRONT ELEVATION", 150, "31 I Page"),
        ("32", "SECTION - AA", 150, "32 I Page"),
        ("33", "SECTION - BB", 150, "33 I Page"),
        ("34", "FOUNDATION DETAILS", 250, "34 I Page"),
        ("35", "TIE BEAM LAYOUT (GROUND FLOOR)", 150, "35 I Page"),
        ("36", "SITE PLAN", 3000, "36 I Page")]
    assert {(s.paper, s.paper_mm, s.border_mm, s.margins_mm) for s in plan.sheets} == {
        ("A4", (297.0, 210.0), (277.0, 190.0), (10.0, 10.0))}
    # the site plan needs a drafting view the add-in cannot make yet: reported, not faked
    assert [s.file for s in plan.sheets if not s.reproduced] == [SITE]
    assert any(n.sheet == SITE and n.what == "the sheet" for n in plan.not_reproduced)

    kinds = {s.file: [(v.kind, v.create.get("level")) for v in s.views] for s in plan.sheets}
    assert kinds[GROUND] == [("floor_plan", "Ground Floor")]
    assert kinds[TYPICAL] == [("floor_plan", "Floor 1")]  # the first storey the typical plan covers
    assert kinds[ROOF] == [("floor_plan", "Roof")]
    assert kinds[FOUNDATION] == [("floor_plan", "Foundation")]
    assert kinds[TIE] == [("floor_plan", "Ground Floor")]  # tie beams at the plinth
    assert kinds[ELEVATION] == [("elevation", "Ground Floor")]
    assert kinds[AA] == [("section", None)] and kinds[BB] == [("section", None), ("section", None)]
    for s in plan.sheets:
        assert all(v.create["scale"] == s.scale for v in s.views)
        assert all(v.paper_center and 0 < v.paper_center[0] < 297 and 0 < v.paper_center[1] < 210 for v in s.views)


def test_sections_on_the_source_cut_lines(sample):
    project, building, result, sheets, _ = sample
    plan = plan_sheet_set(project, building, result, sheets, FRONT)
    aa = _views(plan, AA)[0].create
    assert (aa["startY"], aa["endY"], aa["side"]) == (13890.6, 13890.6, "left")  # drawn west to east, looks north
    assert aa["startX"] < aa["endX"] and aa["name"] == "SECTION - AA"
    bb = [v.create for v in _views(plan, BB)]
    assert [v["name"] for v in bb] == ["SECTION - BB (1 of 2)", "SECTION - BB (2 of 2)"]
    assert [(v["startX"], v["endX"]) for v in bb] == [(21548.6, 21548.6), (29474.5, 29474.5)]  # the jog is left out
    assert {v["side"] for v in bb} == {"right"}  # both look west, as the marks' arrows
    # the far clip is one plane for both segments
    assert bb[0]["depth"] - bb[1]["depth"] == pytest.approx(bb[0]["startX"] - bb[1]["startX"], abs=0.2)
    x0 = [v.paper_center[0] for v in _views(plan, BB)]
    assert x0[0] < x0[1]  # side by side, south part on the left when looking west
    assert any(n.sheet == BB and n.what == "segmented section" for n in plan.not_reproduced)
    steps = [(s.command, s.params) for s in _views(plan, AA)[0].steps]
    dims = next(p for c, p in steps if c == "auto_dimension_section")
    assert dims["include"] == ["levels", "slabs"] and dims["level_names"][:3] == ["GL", "Ground Floor", "Floor 1"]
    spots = next(p for c, p in steps if c == "spot_level")
    assert sorted((t["label"], t["level_name"]) for t in spots["targets"]) == [("GL", "GL"), ("PL", "Ground Floor")]


def test_plan_annotation_from_the_source(sample):
    project, building, result, sheets, _ = sample
    plan = plan_sheet_set(project, building, result, sheets, FRONT)
    typical = next(s for s in plan.sheets if s.file == TYPICAL)
    steps = typical.views[0].steps
    grid_dims = [s for s in steps if s.command == "create_dimensions"]
    assert sorted(s.purpose for s in grid_dims) == ["source dimension 13375 (grids)", "source dimension 17000 (grids)",
                                                    "source dimension 3250 (grids)"]
    for s in grid_dims:  # the line runs between the two grids, where the source draws it
        line = s.params["line"]
        length = math.dist((line["start"]["x"], line["start"]["y"]), (line["end"]["x"], line["end"]["y"]))
        assert f"{length:g}" in s.purpose and line["start"]["z"] == 2700
    labels = next(s for s in steps if s.command == "label_rooms").params
    assert labels["text_mode"] == "given" and len(labels["rooms"]) == 14
    assert {r["text"] for r in labels["rooms"]} == {"3500 X 4000", "4000 X 4000", "4500 X 4000"}
    tags = next(s for s in steps if s.command == "tag_elements").params["element_keys"]
    assert len(tags) == 25 - 14
    assert [s.params["category"] for s in steps if s.command == "tag_all_by_category"] == ["OST_Doors", "OST_Windows"]
    assert typical.source_marks == {"door": {"D": 12, "D1": 20}, "window": {"W": 24, "W1": 4, "W2": 4, "W3": 4, "W4": 1}}
    kinds = sorted(d.kind for d in typical.source_dimensions)
    assert kinds == ["grids"] * 3 + ["local"] * 9
    tie = next(s for s in plan.sheets if s.file == TIE)
    assert sorted(d.text for d in tie.source_dimensions if d.kind == "grids") == [
        "12750", "13375", "17000", "30000", "4250", "6000"]
    hide = next(s for s in tie.views[0].after_create if s.command == "set_category_visibility").params["categories"]
    assert "OST_Walls" in hide and "OST_StructuralFraming" not in hide and "OST_StructuralColumns" not in hide


def test_the_elevation_direction_is_asked(sample):
    project, building, result, sheets, _ = sample
    plan = plan_sheet_set(project, building, result, sheets)
    assert [(q.id, q.options) for q in plan.questions] == [
        (answer_elevation(ELEVATION), ["north", "south", "east", "west"])]


class FakeRevit:
    def __init__(self, titleblocks=("Plinth A4: A4 Landscape", "Plinth A1: A1 Metric"), frame=A4):
        self.calls = []
        self.ids = itertools.count(5000)
        self.titleblocks = titleblocks
        self.frame = frame

    def call(self, command, params=None, **_):
        self.calls.append((command, params))
        n = next(self.ids)
        if command == "list_titleblocks":
            return {"titleblocks": [{"display_name": t, "type_id": i} for i, t in enumerate(self.titleblocks)]}
        if command == "create_sheet":
            return {"sheet_id": n, "sheet_number": params["sheet_number"], "sheet_name": params["sheet_name"],
                    "frame_sheet_mm": self.frame}
        if command == "create_view":
            return {"viewId": n, "viewName": params["name"], "scale": params.get("scale"),
                    "elevationMarkerId": n + 100000 if params["viewType"] == "elevation" else None}
        if command == "create_dimensions":
            s, e = params["line"]["start"], params["line"]["end"]
            return {"dimension_id": n, "value_mm": round(math.dist((s["x"], s["y"]), (e["x"], e["y"])), 1)}
        if command in ("auto_dimension_plan", "auto_dimension_section"):
            values = [2700.0] * 5 + [900.0] if command == "auto_dimension_section" else [3000.0]
            return {"dimension_ids": [n], "dimensions": [{"dimension_id": n, "values_mm": values}], "skipped": []}
        if command == "label_rooms":
            return {"rooms": [{"room_id": r["room_id"], "room_name": "BED ROOM", "tag_id": n * 10 + i,
                               "text_note_id": n * 10 + i + 5, "text": r["text"]} for i, r in enumerate(params["rooms"])]}
        if command == "tag_elements":
            return {"tags": [{"tag_id": n * 10 + i, "element_id": e} for i, e in enumerate(params["element_ids"])]}
        if command == "tag_all_by_category":
            return {"tags": [{"tag_id": n * 10 + i} for i in range(3)], "created": 3}
        if command == "spot_level":
            return {"placed": [{"element_id": n * 10 + i, "label": t["label"]} for i, t in enumerate(params["targets"])],
                    "element_ids": []}
        if command in ("set_view_crop", "set_category_visibility", "delete_element"):
            return {}
        if command == "place_view_on_sheet":
            return {"viewport_id": n}
        if command == "export_pdf":
            return {"path": params["output_folder"] + "\\" + params["file_name"]}
        raise AssertionError(command)


def test_drawing_set_in_revit(sample, tmp_path):
    project, building, result, sheets, created = sample
    revit, recorded = FakeRevit(), {}
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT,
                                  record=lambda k, i: recorded.__setitem__(k, i))
    assert out["status"] == "done" and out["failed"] == []
    assert out["sheets"] == [str(n) for n in range(28, 36)]  # source pages 28-35 in order; 36 (site plan) not made
    commands = [c for c, _ in revit.calls]
    assert commands[:2] == ["list_titleblocks", "create_sheet"] and commands[-1] == "export_pdf"
    sheets_made = [p for c, p in revit.calls if c == "create_sheet"]
    assert {p["title_block_name"] for p in sheets_made} == {"Plinth A4: A4 Landscape"}
    assert [(p["sheet_number"], p["sheet_name"]) for p in sheets_made][:2] == [
        ("28", "GROUND FLOOR PLAN"), ("29", "FIRST STOREY TO FOURTH STOREY FLOOR PLAN")]
    exported = [p for c, p in revit.calls if c == "export_pdf"][0]["view_ids"]
    assert exported == [recorded[f"DWG-SHEET-{n}"] for n in out["sheets"]]
    assert all(k.startswith("DWG-") for k in recorded)
    assert sum(k.startswith("DWG-VIEW-") and not k.endswith("MARKER") for k in recorded) == 9  # BB has two
    assert "DWG-VIEW-31-V1-MARKER" in recorded

    # per sheet: views, then their crop/visibility, annotation, then the viewport
    seq = [c for c, p in revit.calls if c not in ("list_titleblocks", "export_pdf")]
    typical = seq[seq.index("create_sheet", 1) + 1:]
    assert typical[:9] == ["create_view", "create_dimensions", "create_dimensions", "create_dimensions", "label_rooms",
                           "tag_elements", "tag_all_by_category", "tag_all_by_category", "place_view_on_sheet"]
    places = [p for c, p in revit.calls if c == "place_view_on_sheet"]
    assert all("x_mm" in p for p in places) and len(places) == 9

    comparison = json.loads((tmp_path / "drawings-comparison.json").read_text(encoding="utf-8"))
    assert (tmp_path / "drawings-comparison.html").read_text(encoding="utf-8").startswith("<!doctype html>")
    assert comparison["order"]["same"] and len(comparison["sheets"]) == 9
    by_file = {s["file"]: s for s in comparison["sheets"]}
    typical = by_file[TYPICAL]
    assert typical["made"] == {"number": "29", "title": "FIRST STOREY TO FOURTH STOREY FLOOR PLAN", "scales": [150]}
    assert typical["dimensions"]["missing"] == [1200, 1500, 1500, 1500, 2250, 2375, 2687.5, 2687.5, 5875]
    assert typical["dimensions"]["extra"] == []
    assert typical["room_labels"]["missing"] == [] or typical["room_labels"]["made"] == 14
    assert typical["marks"] == {"door": {"source": 32, "tagged": 3}, "window": {"source": 37, "tagged": 3}}
    assert by_file[SITE]["made"] is None and "sheet not reproduced" in by_file[SITE]["differences"]
    assert by_file[AA]["level_marks"] == {"source": ["PL", "GL"], "made": ["GL", "PL"]}


def test_nothing_is_made_while_asking(sample, tmp_path):
    project, building, result, sheets, created = sample
    revit = FakeRevit()
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets)
    assert out["status"] == "questions" and revit.calls == []
    revit = FakeRevit(titleblocks=("Plinth A1: A1 Metric", "Plinth A3: A3"))
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT)
    assert out["status"] == "questions" and [c for c, _ in revit.calls] == ["list_titleblocks"]
    assert out["questions"][0]["options"] == ["Plinth A1: A1 Metric", "Plinth A3: A3"]


def test_a_title_block_of_the_wrong_size_is_not_used(sample, tmp_path):
    project, building, result, sheets, created = sample
    a3 = {**A4, "max_x": 420, "max_y": 297, "width": 420, "height": 297}
    revit, recorded = FakeRevit(frame=a3), {}
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT,
                                  record=lambda k, i: recorded.__setitem__(k, i))
    assert out["status"] == "questions" and "420 x 297" in out["questions"][0]["text"]
    assert [c for c, _ in revit.calls] == ["list_titleblocks", "create_sheet", "delete_element"] and recorded == {}


def test_title_block_choice():
    rows = [{"display_name": "Office: A4 Landscape"}, {"display_name": "Office: A3"}, {"display_name": "Office: A40"}]
    papers = {"A4": (297.0, 210.0)}
    assert choose_title_blocks(rows, papers, {}, {}) == ({"A4": "Office: A4 Landscape"}, [])
    sized = [{"display_name": "X", "sheet_width_mm": 210, "sheet_height_mm": 297},
             {"display_name": "Office: A4 Landscape", "sheet_width_mm": 420, "sheet_height_mm": 297}]
    assert choose_title_blocks(sized, papers, {}, {})[0] == {"A4": "X"}  # a reported size wins over the name
    chosen, questions = choose_title_blocks(rows + [{"display_name": "Other A4"}], papers, {}, {})
    assert chosen == {} and questions[0].options == ["Office: A4 Landscape", "Other A4"]
    assert choose_title_blocks(rows, papers, {}, {ANSWER_TITLE_BLOCK: "Office: A3"})[0] == {"A4": "Office: A3"}


def test_comparison_by_value_multiset():
    sheet = PlannedSheet(file="p.dxf", role="floor_plan", title="PLAN", page="1 I Page", page_number=1, number="1",
                         scale=100, paper="A4", paper_mm=(297, 210), border_mm=(277, 190), margins_mm=(10, 10),
                         source_dimensions=[SourceDimension(handle=h, value=v, text=t, axis="h", kind="grids")
                                            for h, v, t in (("1", 2700, "2700"), ("2", 2700, "2700"),
                                                            ("3", 150, "150"), ("4", None, "40mm Clear Cover"))],
                         source_rooms=[{"name": "BED ROOM", "size": "3500 X 4000"}, {"name": "TOILET", "size": None}],
                         source_marks={"door": {"D": 2}}, source_level_marks=["PL"])
    results = [{"file": "p.dxf", "number": "1", "name": "PLAN", "sheet_id": 1, "views": [
        {"scale": 100, "dimensions": [2700.2, 150.0, 900.0], "labels": [{"name": "BED ROOM", "size": "3500 X 4000"}],
         "tags": {"OST_Doors": 2, "rooms": 1}, "spots": ["PL"], "skipped": []}]}]
    c = compare(SheetPlan(sheets=[sheet]), results)["sheets"][0]
    assert c["dimensions"]["missing"] == [2700] and c["dimensions"]["extra"] == [900]
    assert c["dimensions"]["source_text"] == ["40mm Clear Cover"]
    assert c["room_labels"] == {"source": 1, "made": 1, "missing": [], "extra": [], "name_tags": 1}
    assert c["marks"] == {"door": {"source": 2, "tagged": 2}}
    assert c["differences"] == ["dimension values differ"]
