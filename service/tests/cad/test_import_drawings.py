"""The drawing set of an imported project (T10) on the G+4 sample, against a fake Revit.

The sheet plan follows the source sheets (page order, page labels, titles, scales, A4 paper from the 277 x 190 mm
border); the fake Revit answers like the add-in so the command sequence, the recording and the comparison with the
source can be pinned without Revit.
"""

import itertools
import json
import math

import pytest

from plinth.cad import project as cad_project, structure
from plinth.cad.recognise import recognise_plan
from plinth.imported import to_plan
from plinth.imported.drawings import (ANSWER_TITLE_BLOCK, PlannedSheet, SheetPlan, SourceDimension, answer_elevation,
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
    assert not plan.blocking() and [q.topic for q in plan.questions] == ["title_field"] * 7
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
    # every sheet is made; the site plan is the source DWG on a drafting view (not modelled, said so)
    assert [s.file for s in plan.sheets if not s.reproduced] == []
    site = _views(plan, SITE)[0]
    assert (site.kind, site.create) == ("drafting", {"viewType": "drafting", "name": "SITE PLAN", "scale": 3000})
    assert [(st.command, st.params["unit"]) for st in site.after_create] == [("import_cad_to_view", "millimeter")]
    assert any(n.sheet == SITE and n.what == "site plan content" for n in plan.not_reproduced)
    # foundation and tie beam sheets are structural plans looking down past the footing / beam bottoms
    found, tie = _views(plan, FOUNDATION)[0], _views(plan, TIE)[0]
    assert (found.kind, found.create["level"], tie.create["level"]) == ("structural_plan", "Foundation", "Ground Floor")
    assert found.create["viewRange"] == {"top_offset_mm": 700.0, "cut_offset_mm": 350.0, "bottom_offset_mm": -600.0,
                                         "depth_offset_mm": -600.0}  # raft 500 thick; GL 700 above
    assert tie.create["viewRange"]["bottom_offset_mm"] == -550.0  # beams 450 deep
    assert not any(n.what == "structural plan view" for n in plan.not_reproduced)

    kinds = {s.file: [(v.kind, v.create.get("level")) for v in s.views] for s in plan.sheets}
    assert kinds[GROUND] == [("floor_plan", "Ground Floor")]
    assert kinds[TYPICAL] == [("floor_plan", "Floor 1")]  # the first storey the typical plan covers
    assert kinds[ROOF] == [("floor_plan", "Roof")]
    assert kinds[FOUNDATION] == [("structural_plan", "Foundation")]
    assert kinds[TIE] == [("structural_plan", "Ground Floor")]  # tie beams at the plinth
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
    assert dims["include"] == ["foundation", "levels", "slabs"] and dims["ground_level"] == "GL"
    assert dims["level_names"][:4] == ["Foundation", "GL", "Ground Floor", "Floor 1"]
    aa = {d.text: d.kind for d in next(s for s in plan.sheets if s.file == AA).source_dimensions if d.kind != "levels"}
    assert aa == {"1200": "foundation", "500": "foundation", "150": "slabs", "200": "local", "100": "local",
                  "450": "local"}  # 450: the upper-floor beam, which the plan does not model
    bb = next(p for c, p in ((s.command, s.params) for v in _views(plan, BB) for s in v.steps)
              if c == "auto_dimension_section")
    assert bb["include"] == ["foundation", "levels", "openings", "slabs"]  # BB dimensions the storey 4 window
    spots = next(p for c, p in steps if c == "spot_level")
    assert sorted((t["label"], t["level_name"]) for t in spots["targets"]) == [("GL", "GL"), ("PL", "Ground Floor")]


def test_plan_annotation_from_the_source(sample):
    project, building, result, sheets, _ = sample
    plan = plan_sheet_set(project, building, result, sheets, FRONT)
    typical = next(s for s in plan.sheets if s.file == TYPICAL)
    steps = typical.views[0].steps
    grid_dims = [s for s in steps if s.command == "create_dimensions" and s.purpose.endswith("(grids)")]
    assert sorted(s.purpose for s in grid_dims) == ["source dimension 13375 (grids)", "source dimension 17000 (grids)",
                                                    "source dimension 3250 (grids)"]
    faces = [s for s in steps if s.command == "create_dimensions" and s.purpose.endswith("(references)")]
    assert sorted(s.purpose.split()[2] for s in faces) == ["1500", "1500", "2250", "2375", "2687.5", "2687.5", "5875"]
    for s in faces:  # room sizes between wall faces: the face on the measured side of each wall
        assert [r["mode"] for r in s.params["references"]] == ["wall_face", "wall_face"]
        line = s.params["line"]
        length = math.dist((line["start"]["x"], line["start"]["y"]), (line["end"]["x"], line["end"]["y"]))
        assert f"{length:g}" == s.purpose.split()[2]
    for s in grid_dims:  # the line runs between the two grids, where the source draws it
        line = s.params["line"]
        length = math.dist((line["start"]["x"], line["start"]["y"]), (line["end"]["x"], line["end"]["y"]))
        assert f"{length:g}" in s.purpose and line["start"]["z"] == 2700
    labels = next(s for s in steps if s.command == "label_rooms").params
    assert labels["text_mode"] == "given" and len(labels["rooms"]) == 14
    assert {r["text"] for r in labels["rooms"]} == {"3500 X 4000", "4000 X 4000", "4500 X 4000"}
    tags = next(s for s in steps if s.command == "tag_elements").params["element_keys"]
    assert len(tags) == 29 - 14  # 29 rooms per storey (each flat's kitchen its own room), 14 labelled with a size
    assert [s.params["category"] for s in steps if s.command == "tag_all_by_category"] == ["OST_Doors", "OST_Windows"]
    # the kitchens' room separation lines are Plinth's, not the source's: hidden in the view
    assert any(s.command == "set_category_visibility" and s.params["categories"] == ["OST_RoomSeparationLines"]
               for s in typical.views[0].after_create)
    assert typical.source_marks == {"door": {"D": 12, "D1": 20}, "window": {"W": 24, "W1": 4, "W2": 4, "W3": 4, "W4": 1}}
    kinds = sorted(d.kind for d in typical.source_dimensions)
    assert kinds == ["grids"] * 3 + ["local"] * 2 + ["references"] * 7
    tie = next(s for s in plan.sheets if s.file == TIE)
    assert sorted(d.text for d in tie.source_dimensions if d.kind == "grids") == [
        "12750", "13375", "17000", "30000", "4250", "6000"]
    hide = next(s for s in tie.views[0].after_create if s.command == "set_category_visibility").params["categories"]
    assert "OST_Walls" in hide and "OST_StructuralFraming" not in hide and "OST_StructuralColumns" not in hide


def test_the_elevation_direction_is_asked(sample):
    project, building, result, sheets, _ = sample
    plan = plan_sheet_set(project, building, result, sheets)
    assert [(q.id, q.options) for q in plan.questions if q.blocks] == [
        (answer_elevation(ELEVATION), ["north", "south", "east", "west"])]


class FakeRevit:
    VIEW_TYPES = {"floor_plan": "FloorPlan", "structural_plan": "EngineeringPlan", "section": "Section",
                  "elevation": "Elevation", "drafting": "DraftingView"}

    def __init__(self, titleblocks=("Plinth A4: A4 Landscape", "Plinth A1: A1 Metric"), frame=A4, sizes=None,
                 levels=(), views=(), active=1, missing=()):
        self.sizes = sizes or {}
        self.calls = []
        self.ids = itertools.count(5000)
        self.titleblocks = titleblocks
        self.frame = frame
        self.levels = list(levels)  # list_levels rows
        self.views = list(views)  # list_views rows; views made here are added
        self.active = active  # the active view's id
        self.missing = set(missing)  # ids delete_element does not find (deleted by hand)

    def call(self, command, params=None, **_):
        self.calls.append((command, params))
        n = next(self.ids)
        if command == "list_levels":
            return {"levels": self.levels}
        if command == "list_views":
            kind = (params or {}).get("viewType")
            return {"views": [v for v in self.views if kind in (None, v["viewType"])]}
        if command == "get_current_view_info":
            return {"viewId": self.active}
        if command == "activate_view":
            self.active = params["view_id"]
            return {"active_view_id": self.active}
        if command == "delete_element":
            ids = params["elementIds"]
            assert self.active not in ids, "Revit cannot delete the active view"
            gone = [i for i in ids if i in self.missing]
            return {"deleted": len(ids) - len(gone), "failedIds": gone, "errors": [f"{i}: not found" for i in gone]}
        if command in ("set_grid_heads", "operate_element", "set_project_info", "set_element_parameter_values"):
            return {}
        if command == "list_titleblocks":
            return {"titleblocks": [{"display_name": t, "type_id": i, "sheet_width_mm": self.sizes.get(t, (None,))[0],
                                     "sheet_height_mm": self.sizes.get(t, (None, None))[1]}
                                    for i, t in enumerate(self.titleblocks)]}
        if command == "create_sheet":
            return {"sheet_id": n, "sheet_number": params["sheet_number"], "sheet_name": params["sheet_name"],
                    "frame_sheet_mm": self.frame}
        if command == "create_view":
            self.views.append({"id": n, "name": params["name"], "viewType": self.VIEW_TYPES[params["viewType"]],
                               "levelName": params.get("level"), "isTemplate": False})
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
        if command in ("set_view_crop", "set_category_visibility"):
            return {}
        if command == "place_view_on_sheet":
            return {"viewport_id": n}
        if command == "import_cad_to_view":
            return {"instance_id": n}
        if command == "export_pdf":
            return {"path": params["output_folder"] + "\\" + params["file_name"]}
        raise AssertionError(command)


def test_drawing_set_in_revit(sample, tmp_path):
    project, building, result, sheets, created = sample
    revit, recorded = FakeRevit(), {}
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT,
                                  record=lambda k, i: recorded.__setitem__(k, i))
    assert out["status"] == "done" and out["failed"] == []
    assert out["sheets"] == [str(n) for n in range(28, 37)]  # source pages 28-36 in order
    commands = [c for c, _ in revit.calls]
    assert commands[:2] == ["list_titleblocks", "create_sheet"] and commands[-1] == "export_pdf"
    sheets_made = [p for c, p in revit.calls if c == "create_sheet"]
    assert {p["title_block_name"] for p in sheets_made} == {"Plinth A4: A4 Landscape"}
    assert [(p["sheet_number"], p["sheet_name"]) for p in sheets_made][:2] == [
        ("28", "GROUND FLOOR PLAN"), ("29", "FIRST STOREY TO FOURTH STOREY FLOOR PLAN")]
    exported = [p for c, p in revit.calls if c == "export_pdf"][0]["view_ids"]
    assert exported == [recorded[f"DWG-SHEET-{n}"] for n in out["sheets"]]
    assert all(k.startswith("DWG-") for k in recorded)
    assert sum(k.startswith("DWG-VIEW-") and not k.endswith("MARKER") for k in recorded) == 10  # BB has two
    assert "DWG-VIEW-31-V1-MARKER" in recorded

    # per sheet: views, then their crop/visibility, annotation, then the viewport
    seq = [c for c, p in revit.calls if c not in ("list_titleblocks", "export_pdf")]
    typical = seq[seq.index("create_sheet", 1) + 1:]
    # (set_category_visibility: the kitchens' room separation lines hidden, the source does not draw them)
    assert typical[:18] == ["create_view", "set_grid_heads", "set_category_visibility"] + ["create_dimensions"] * 10 + [
        "label_rooms", "tag_elements", "tag_all_by_category", "tag_all_by_category", "place_view_on_sheet"]
    places = [p for c, p in revit.calls if c == "place_view_on_sheet"]
    assert all("x_mm" in p for p in places) and len(places) == 10
    # the drawing (its crop region) goes where the source has it, the viewport title right under it
    assert all(p["anchor"] == "crop" and p["title_below_crop"] for p in places)
    assert [p["unit"] for c, p in revit.calls if c == "import_cad_to_view"] == ["millimeter"]

    comparison = json.loads((tmp_path / "drawings-comparison.json").read_text(encoding="utf-8"))
    assert (tmp_path / "drawings-comparison.html").read_text(encoding="utf-8").startswith("<!doctype html>")
    assert comparison["order"]["same"] and len(comparison["sheets"]) == 9
    by_file = {s["file"]: s for s in comparison["sheets"]}
    typical = by_file[TYPICAL]
    assert typical["made"] == {"number": "29", "title": "FIRST STOREY TO FOURTH STOREY FLOOR PLAN", "scales": [150]}
    assert typical["dimensions"]["missing"] == [1200, 1500]  # 9 before the face/reference dimensions
    assert typical["dimensions"]["extra"] == []
    assert typical["room_labels"]["missing"] == [] or typical["room_labels"]["made"] == 14
    assert typical["marks"] == {"door": {"source": 32, "tagged": 3}, "window": {"source": 37, "tagged": 3}}
    assert by_file[SITE]["made"] == {"number": "36", "title": "SITE PLAN", "scales": [3000]}
    # source dimension values no command draws, over the whole sample: 62 before the T10b commands, 30 now
    left_out = {n["sheet"]: len(n["values"]) for n in out["not_reproduced"] if n["what"] == "dimensions"}
    assert sum(left_out.values()) == 30 and left_out == {GROUND: 3, TYPICAL: 2, AA: 3, BB: 13, FOUNDATION: 9}
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


def test_a_title_block_that_reports_its_size_needs_no_trial_sheet(sample, tmp_path):
    project, building, result, sheets, created = sample
    revit = FakeRevit(sizes={"Plinth A4: A4 Landscape": (297, 210)})
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT)
    assert out["status"] == "done" and "delete_element" not in [c for c, _ in revit.calls]
    revit = FakeRevit(titleblocks=("Plinth A4: A4 Landscape",), sizes={"Plinth A4: A4 Landscape": (420, 297)})
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT)
    assert out["status"] == "questions" and [c for c, _ in revit.calls] == ["list_titleblocks"]


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


def test_views_fit_a_title_block_of_another_size():
    """An A4 set on an A1 title block: each view gets the largest standard scale that fits its share of the drawing
    area (the frame less the title strip), centred in it; two views on a sheet sit side by side."""
    from plinth.imported.drawings import PlannedSheet, PlannedView, _fit_layout, fit_scale
    a1 = {"min_x": 0.0, "min_y": 0.0, "width": 841.0, "height": 594.0}
    plan = PlannedView(key="V1", kind="floor_plan", name="p", create={"cropMinX": 0, "cropMinY": 0, "cropMaxX": 34000,
                                                                       "cropMaxY": 21000})
    sheet = PlannedSheet(file="f", role="floor_plan", title="t", page=None, page_number=None, number="29", scale=150,
                         paper="A4", paper_mm=(297, 210), border_mm=(277, 190), margins_mm=(10, 10), views=[plan])
    (centre, slot), = _fit_layout(a1, sheet)
    assert slot == (841.0 * 0.82, 594.0) and centre == (841.0 * 0.82 / 2, 297.0)
    assert fit_scale((34000, 21000), slot) == 75  # 1:150 on A4 becomes 1:75 on A1
    assert fit_scale((340000, 210000), slot) == 750
    two = sheet.model_copy(update={"views": [plan, plan]})
    slots = _fit_layout(a1, two)
    assert [round(c[0]) for c, _ in slots] == [172, 517] and all(sz[0] == 841.0 * 0.82 / 2 for _, sz in slots)


def _made(revit, command):
    return [p for c, p in revit.calls if c == command]


def test_grid_heads_outside_the_building(sample, tmp_path):
    """Every plan runs Plinth's grids past the building to heads at both ends, every section and elevation from the
    lowest level to a head above the highest; the crop region is grown to hold the heads, and the drawing stays
    where the source has it (the viewport is anchored on the crop centre, moved with it)."""
    from plinth.imported.drawings import GRID_HEAD_MM, GRID_ROOM_MM
    project, building, result, sheets, created = sample
    plan = plan_sheet_set(project, building, result, sheets, FRONT)
    ground = _views(plan, GROUND)[0]
    heads = next(st for st in ground.after_create if st.command == "set_grid_heads").params
    assert len(heads["element_keys"]) == 33 and heads["head_offset_mm"] == GRID_HEAD_MM
    assert set(heads["bounds"]) == {"min_x_mm", "min_y_mm", "max_x_mm", "max_y_mm"}
    for f in (AA, BB, ELEVATION):
        for v in _views(plan, f):
            b = next(st for st in v.after_create if st.command == "set_grid_heads").params["bounds"]
            assert b == {"min_z_mm": -1300.0, "max_z_mm": 15900.0}  # Foundation to Upper Roof
    crop = dict(ground.create)

    revit = FakeRevit()
    make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT)
    made = next(p for p in _made(revit, "create_view") if p["name"] == "GROUND FLOOR PLAN")
    reach = (GRID_HEAD_MM + GRID_ROOM_MM) * 250  # paper mm at 1:250
    b = heads["bounds"]
    assert made["cropMinX"] <= b["min_x_mm"] - reach and made["cropMaxX"] >= b["max_x_mm"] + reach
    assert made["cropMinY"] <= b["min_y_mm"] - reach and made["cropMaxY"] >= b["max_y_mm"] + reach
    assert made["cropMinX"] <= crop["cropMinX"] and made["cropMaxY"] >= crop["cropMaxY"]  # never smaller
    sections = [p for p in _made(revit, "create_view") if p["viewType"] == "section"]
    assert all(p["top"] >= 15900 + (GRID_HEAD_MM + GRID_ROOM_MM) * p["scale"] for p in sections)
    elevation_crop = _made(revit, "set_view_crop")[0]["bounds"]
    assert elevation_crop["max_z_mm"] >= 15900 + (GRID_HEAD_MM + GRID_ROOM_MM) * 150
    # the drawing keeps its source place: the anchor moves with the crop centre
    place = _made(revit, "place_view_on_sheet")[0]
    moved = ((made["cropMinX"] + made["cropMaxX"]) - (crop["cropMinX"] + crop["cropMaxX"])) / 2 / 250
    assert place["x_mm"] == pytest.approx(ground.paper_center[0] + moved, abs=0.1)


def test_levels_plinth_did_not_make_are_hidden_in_sections_and_elevations(sample, tmp_path):
    project, building, result, sheets, created = sample
    ours = next(iter(created.values()))
    revit = FakeRevit(levels=[{"elementId": 311, "name": "Level 1"}, {"elementId": 312, "name": "Level 2"},
                              {"elementId": ours, "name": "Ground Floor"}])
    make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT)
    hidden = _made(revit, "operate_element")
    assert all(p["operation"] == "hide" and p["elementIds"] == [311, 312] for p in hidden)
    # AA, BB (two views) and the elevation; never the plans
    assert sorted(p["view_id"] for p in hidden) == sorted(v["id"] for v in revit.views
                                                          if v["viewType"] in ("Section", "Elevation"))
    assert len(hidden) == 4


def test_an_elevation_gets_a_plan_for_its_marker_when_its_level_has_none():
    """Revit hangs an elevation marker on a floor plan of its level; with none there, Plinth makes its own (recorded)."""
    from plinth.imported.drawings import PlannedView, _Exec, _marker_plan
    sheet = PlannedSheet(file="e.dxf", role="elevation", title="FRONT ELEVATION", page=None, page_number=31,
                         number="31", scale=150, paper="A4", paper_mm=(297, 210), border_mm=(277, 190),
                         margins_mm=(10, 10))
    view = PlannedView(key="V1", kind="elevation", name="FRONT ELEVATION",
                       create={"viewType": "elevation", "level": "Ground Floor", "x": 0, "y": -9000,
                               "direction": "north", "name": "FRONT ELEVATION", "scale": 150})
    revit, recorded = FakeRevit(views=[{"id": 7, "name": "Level 1", "viewType": "FloorPlan", "levelName": "Level 1",
                                        "isTemplate": False}]), {}
    _marker_plan(_Exec(revit, {}, recorded.__setitem__), sheet, view)
    assert _made(revit, "create_view") == [{"viewType": "floor_plan", "level": "Ground Floor",
                                            "name": "Ground Floor - plan for FRONT ELEVATION"}]
    assert list(recorded) == ["DWG-VIEW-31-V1-PLAN"]
    _marker_plan(_Exec(revit, {}, recorded.__setitem__), sheet, view)  # now there is one: nothing more
    assert len(_made(revit, "create_view")) == 1


def test_title_block_fields_from_the_answers(sample, tmp_path):
    """Title block fields are questions that never stop the set: answered ones fill Project Information and the
    sheets; an unanswered sheet field is left blank on Plinth's sheets, an unanswered project field is not touched."""
    from plinth.imported.drawings import answer_title
    project, building, result, sheets, created = sample
    answers = {**FRONT, answer_title("project_name"): "G+4 Residential, Bardhaman", answer_title("client"): "S. Roy",
               answer_title("drawn_by"): "AK"}
    revit = FakeRevit()
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=answers)
    assert out["status"] == "done"
    assert _made(revit, "set_project_info") == [{"name": "G+4 Residential, Bardhaman", "client_name": "S. Roy"}]
    fields = {p["parameterName"]: p["value"] for p in _made(revit, "set_element_parameter_values")}
    assert fields == {"Drawn By": "AK", "Checked By": "", "Sheet Issue Date": ""}
    assert all(len(p["elementIds"]) == 9 for p in _made(revit, "set_element_parameter_values"))
    commands = [c for c, _ in revit.calls]
    assert commands.index("set_element_parameter_values") < commands.index("export_pdf")  # on the PDF too
    assert out["title_fields"]["filled"] == {"project_name": "G+4 Residential, Bardhaman", "client": "S. Roy",
                                             "drawn_by": "AK"}
    assert [q["id"] for q in out["questions"]] == [answer_title(f) for f in (
        "project_number", "address", "checked_by", "issue_date")]
    assert not any(q["blocks"] for q in out["questions"])


def test_title_block_suggestions_from_the_source_texts():
    from plinth.cad import sheet as cad_sheet
    from plinth.cad.reader import CadEntity, CadSheet
    from plinth.imported.drawings import title_suggestions

    def text(handle, x, y, value, tag=None):
        return CadEntity(handle=handle, kind="text", dxftype="ATTRIB" if tag else "TEXT", layer="0", points=[(x, y)],
                         text=value, height=2.5, tag=tag)

    def sheet(*entities):
        return cad_sheet.Sheet(cad=CadSheet(source="s.dxf", sha256="", dxf="s.dxf", units="mm", unit_factor=1.0,
                                            entities=list(entities)))

    a = sheet(text("1", 0, 0, "PROJECT NAME"), text("2", 40, 0.5, "PROPOSED G+4 RESIDENTIAL BUILDING"),
              text("3", 0, -10, "PROJECT NO.: 2024/17"), text("4", 0, -20, "DRAWN BY : A. KUNDU"),
              text("5", 0, -30, "CLIENT"), text("6", 1, -35, "SRI S. ROY"), text("7", 200, 0, "SCALE- 1:100"),
              text("8", 0, -60, "S. DAS", tag="CHECKED_BY"))
    b = sheet(text("1", 0, 0, "DRAWN BY : A. KUNDU"), text("2", 0, -10, "DATE- 12.03.2024"))
    found = title_suggestions({"a": a, "b": b, "c": None})
    assert found["project_name"] == ["PROPOSED G+4 RESIDENTIAL BUILDING"]  # right of the bare label
    assert found["project_number"] == ["2024/17"]  # after the label (not taken for the project name)
    assert found["drawn_by"] == ["A. KUNDU"]
    assert found["client"] == ["SRI S. ROY"]  # just under the bare label
    assert found["checked_by"] == ["S. DAS"]  # an attribute tagged CHECKED_BY
    assert found["issue_date"] == ["12.03.2024"]
    assert found["address"] == []


def test_remake_deletes_the_previous_set_and_only_it(sample, tmp_path):
    """A remake deletes the previous set's sheets and views (recorded DWG- keys), sheets first, after making a view
    that is not being deleted active; elements already deleted by hand count as gone; then the set is made again."""
    from plinth.imported.drawings import _Exec, remove_drawing_set
    project, building, result, sheets, created = sample
    first, recorded = FakeRevit(), {}
    make_import_drawing_set(first, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT,
                            record=recorded.__setitem__)
    previous = dict(recorded)
    sheet28 = previous["DWG-SHEET-28"]
    revit = FakeRevit(active=sheet28, missing=[previous["DWG-VIEW-36-V1"]],
                      views=[{"id": sheet28, "name": "GROUND FLOOR PLAN", "viewType": "DrawingSheet"},
                             {"id": 3, "name": "{3D}", "viewType": "ThreeD", "isTemplate": False}])
    forgotten, again = [], {}
    out = make_import_drawing_set(revit, project, building, result, created, tmp_path, sheets=sheets, answers=FRONT,
                                  previous=previous, record=again.__setitem__, forget=forgotten.extend)
    assert out["status"] == "done" and out["removed"]["failed"] == []
    commands = [c for c, _ in revit.calls]
    assert commands.index("delete_element") < commands.index("create_sheet")  # the sheet numbers are free again
    assert _made(revit, "activate_view") == [{"view_id": 3}]
    deleted = _made(revit, "delete_element")[0]["elementIds"]
    assert sorted(deleted) == sorted(previous.values())  # nothing that was not recorded
    assert all(previous[k] in deleted[:9] for k in previous if k.startswith("DWG-SHEET-"))
    assert out["removed"]["already_gone"] == [previous["DWG-VIEW-36-V1"]]
    assert sorted(forgotten) == sorted(previous)
    views = [k for k in previous if k.startswith(("DWG-SHEET-", "DWG-VIEW-"))]
    assert sorted(k for k in again if k.startswith(("DWG-SHEET-", "DWG-VIEW-"))) == sorted(views)  # made again

    # a delete Revit refuses stops the remake before anything new is made, and keeps those keys
    class Refusing(FakeRevit):
        def call(self, command, params=None, **kw):
            if command == "delete_element":
                self.calls.append((command, params))
                return {"deleted": 1, "failedIds": [sheet28], "errors": [f"{sheet28}: in use"]}
            return super().call(command, params, **kw)

    revit = Refusing()
    removed = remove_drawing_set(_Exec(revit, {}, lambda k, i: None), previous)
    assert removed["failed"] == [sheet28] and "DWG-SHEET-28" not in removed["removed_keys"]
    out = make_import_drawing_set(Refusing(), project, building, result, created, tmp_path, sheets=sheets,
                                  answers=FRONT, previous=previous)
    assert out["status"] == "failed" and "could not be deleted" in out["error"]
