"""Page border, title, scale and page label on the sample sheets, and the border found from four lines."""

import ezdxf

from plinth.cad import reader, sheet

# What the sample sheets print (read off drawing-set.pdf): title, scale 1:N, page number.
EXPECTED = {
    "1.GROUND FLOOR PLAN.dxf": ("GROUND FLOOR PLAN", 250, 28),
    "2.PLAN 1st to 4th.dxf": ("FIRST STOREY TO FOURTH STOREY FLOOR PLAN", 150, 29),
    "3.ROOF PLAN.dxf": ("ROOF PLAN", 150, 30),
    "4.SITE PLAN.dxf": ("SITE PLAN", 3000, 36),
    "5.FRONT ELEVATION.dxf": ("FRONT ELEVATION", 150, 31),
    "6.SECTION- AA.dxf": ("SECTION - AA", 150, 32),
    "7.SECTION- BB.dxf": ("SECTION - BB", 150, 33),
    "8.Foundation Details.dxf": ("FOUNDATION DETAILS", 250, 34),
    "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf": ("TIE BEAM LAYOUT (GROUND FLOOR)", 150, 35),
}

# Drawing entities (inside the frame) by kind at the top level, and the total with block contents: a change here
# means the reader or the frame detection changed what it hands to recognition.
COUNTS = {
    "1.GROUND FLOOR PLAN.dxf": ({"arc": 9, "circle": 4, "dimension": 14, "hatch": 41, "insert": 11, "line": 1374,
                                 "text": 12}, 6272),
    "2.PLAN 1st to 4th.dxf": ({"dimension": 12, "hatch": 59, "insert": 44, "line": 651, "point": 22,
                               "polyline": 2, "text": 129}, 3035),
    "3.ROOF PLAN.dxf": ({"dimension": 2, "hatch": 17, "insert": 2, "line": 322, "text": 16}, 381),
    "4.SITE PLAN.dxf": ({"arc": 20, "circle": 2, "hatch": 7, "line": 374, "other": 1, "polyline": 3, "text": 28}, 435),
    "5.FRONT ELEVATION.dxf": ({"arc": 115, "circle": 14, "hatch": 32, "insert": 21, "line": 3111, "polyline": 39,
                               "text": 2}, 5696),
    "6.SECTION- AA.dxf": ({"circle": 20, "dimension": 15, "hatch": 55, "insert": 25, "line": 1036,
                           "polyline": 69, "text": 15}, 4792),
    "7.SECTION- BB.dxf": ({"dimension": 24, "hatch": 33, "insert": 8, "line": 1017, "polyline": 3, "text": 3}, 2028),
    "8.Foundation Details.dxf": ({"arc": 2, "circle": 10, "dimension": 15, "hatch": 45, "line": 397,
                                  "polyline": 120, "text": 27}, 616),
    "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf": ({"dimension": 8, "line": 150, "point": 34}, 192),
}


def test_titles_scales_and_pages_are_read_from_the_drawings(sample_sheets):
    assert set(sample_sheets) == set(EXPECTED)
    for name, (title, scale, page) in EXPECTED.items():
        s = sample_sheets[name]
        assert (s.title, s.scale, s.page_number) == (title, scale, page), name
        assert s.title_basis == "underlined_near_scale" and s.title_rotation == 0 and not s.issues, name


def test_border_title_scale_and_page_are_excluded_and_drawing_lies_inside(sample_sheets):
    for name, s in sample_sheets.items():
        reasons = sorted(x.reason for x in s.excluded)
        assert reasons == ["border", "border", "page", "scale", "title"], name
        x0, y0, x1, y1 = s.inner
        assert s.outer[0] < x0 and s.outer[1] < y0 and x1 < s.outer[2] and y1 < s.outer[3], name
        excluded = {x.handle for x in s.excluded}
        assert s.title_handle in excluded and not excluded & {e.handle for e in s.drawing_entities}, name
        assert len(s.drawing_entities) + len(s.excluded) == len(s.cad.entities), name
        for e in s.drawing_entities:
            if e.bounds() is None:  # no geometry to place (an ACAD_PROXY_ENTITY on the site plan)
                assert e.kind == "other", (name, e.handle)
                continue
            bx0, by0, bx1, by1 = e.bounds()
            assert x0 <= (bx0 + bx1) / 2 <= x1 and y0 <= (by0 + by1) / 2 <= y1, (name, e.handle)


def test_drawing_entity_counts_are_stable(sample_sheets):
    for name, (kinds, exploded) in COUNTS.items():
        drawing = sample_sheets[name].drawing_entities
        assert reader.kind_counts(drawing) == kinds, name
        assert sum(reader.kind_counts(drawing, deep=True).values()) == exploded, name


def test_foundation_sheet_keeps_its_view_titles_as_drawing(sample_sheets):
    s = sample_sheets["8.Foundation Details.dxf"]
    underlined = {e.text for e in s.drawing_entities if e.kind == "text" and e.underlined}
    assert {"PLAN", "SECTION- AA", "DETAILED SPECIFICATION OF SECTION- AA"} <= underlined


def _framed_doc(title: str = "%%UGROUND FLOOR PLAN"):
    doc = ezdxf.new(units=ezdxf.units.MM)
    msp = doc.modelspace()
    for start, end in [((0, 0), (42000, 0)), ((42000, 0), (42000, 29700)), ((42000, 29700), (0, 29700)),
                       ((0, 29700), (0, 0))]:
        msp.add_line(start, end)
    msp.add_lwpolyline([(500, 500), (41500, 500), (41500, 29200), (500, 29200)], close=True)
    msp.add_text(title, height=600).set_placement((15000, 2000))
    msp.add_text("SCALE- 1:100", height=400).set_placement((16000, 1000))
    msp.add_text("Page 7", height=400).set_placement((-800, 3000))
    for y in range(5000, 25000, 1000):
        msp.add_line((5000, y), (20000, y))
    msp.add_line((-5000, -5000), (-4000, -5000))  # outside the page: not drawing
    return doc


def test_border_drawn_as_four_lines_is_found(tmp_path):
    path = tmp_path / "framed.dxf"
    _framed_doc().saveas(path)
    s = sheet.detect(reader.read_dxf(path))
    assert s.outer == (0, 0, 42000, 29700) and s.inner == (500, 500, 41500, 29200)
    assert (s.title, s.scale, s.page, s.page_number) == ("GROUND FLOOR PLAN", 100, "Page 7", 7)
    assert [e.points[0] for e in s.drawing_entities] == [(5000, y) for y in range(5000, 25000, 1000)]
    assert sorted(x.reason for x in s.excluded) == ["border"] * 5 + ["outside_border", "page", "scale", "title"]


def test_sheet_without_border_or_title_reports_issues(tmp_path):
    doc = ezdxf.new(units=ezdxf.units.MM)
    doc.modelspace().add_line((0, 0), (100, 0))
    path = tmp_path / "bare.dxf"
    doc.saveas(path)
    s = sheet.detect(reader.read_dxf(path))
    assert s.outer is None and s.title is None and len(s.drawing_entities) == 1
    assert [i.topic for i in s.issues] == ["sheet", "sheet", "scale"]
