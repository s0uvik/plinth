"""Command tests against a live Revit (PRD: Testing, command tests). Every add-in command the build
flow uses is called at least once on a SCRATCH project, far from existing geometry, and cleaned up.

    set PLINTH_LIVE=1
    set PLINTH_TITLE=<scratch project title>
    .venv\\Scripts\\python -m pytest tests/live -v
"""

from __future__ import annotations

import os
import time

import pytest

from plinth.revit import RevitClient, RevitError
from plinth.revit.commands import REQUIRED_COMMANDS

pytestmark = pytest.mark.skipif(
    not (os.environ.get("PLINTH_LIVE") == "1" and os.environ.get("PLINTH_TITLE")),
    reason="needs PLINTH_LIVE=1 and PLINTH_TITLE=<scratch project title>",
)

OX, OY = -200000.0, -200000.0  # Far from anything already in the project.


@pytest.fixture(scope="module")
def revit():
    with RevitClient.connect(required_commands=REQUIRED_COMMANDS + ["list_levels", "list_grids", "list_rooms",
                                                                    "get_room_boundaries", "auto_create_rooms_from_walls",
                                                                    "get_available_family_types"]) as client:
        client.confirm_document(os.environ["PLINTH_TITLE"])
        yield client


@pytest.fixture(scope="module")
def box(revit):
    """Two levels and a closed 6 x 4 m box of walls between them, removed afterwards."""
    stamp = time.strftime("%H%M%S")
    low, high = f"CT {stamp} A", f"CT {stamp} B"
    corners = [(OX, OY), (OX + 6000, OY), (OX + 6000, OY + 4000), (OX, OY + 4000)]
    walls = [{"command": "create_line_based_element", "params": {
        "elementType": "wall", "level": low, "topLevel": high,
        "startX": a[0], "startY": a[1], "endX": b[0], "endY": b[1]}} for a, b in zip(corners, corners[1:] + corners[:1])]
    data = revit.call("batch_execute", {"commands": [
        {"command": "create_level", "params": {"elevation": 80000, "name": low}},
        {"command": "create_level", "params": {"elevation": 83000, "name": high}},
        *walls,
    ]})
    ids = [r["data"]["elementId"] for r in data["results"]]
    created = {"levels": ids[:2], "walls": ids[2:], "low": low, "high": high, "corners": corners, "extra": []}
    yield created
    revit.call("delete_element", {"elementIds": list(reversed(created["extra"])) + ids[::-1]})


def first_type(revit, category):
    data = revit.call("get_available_family_types", {"builtInCategory": category})
    assert data["totalTypes"] > 0, f"the project has no {category} types loaded"
    return data["categories"][0]["types"][0]


def test_view_info_carries_document_target(revit):
    response = revit.request("get_current_view_info")
    assert response["data"]["viewName"]
    assert response["_target"]["document_title"] == os.environ["PLINTH_TITLE"]


def test_levels_and_grids_round_trip(revit, box):
    names = [lv["name"] for lv in revit.call("list_levels")["levels"]]
    assert box["low"] in names and box["high"] in names
    with pytest.raises(RevitError) as dup:
        revit.call("create_level", {"elevation": 1, "name": box["low"]})
    assert dup.value.code == "NAME_IN_USE"

    grid = revit.call("create_grid", {"startX": OX, "startY": OY - 2000, "endX": OX, "endY": OY + 6000, "name": f"CT-{box['low']}"})
    box["extra"].append(grid["elementId"])
    assert grid["startMm"] == [OX, OY - 2000]
    assert any(g["elementId"] == grid["elementId"] for g in revit.call("list_grids")["grids"])


def test_wall_errors_list_valid_options(revit, box):
    with pytest.raises(RevitError) as e:
        revit.call("create_line_based_element", {"elementType": "wall", "level": "No Such Level",
                                                 "startX": 0, "startY": 0, "endX": 1000, "endY": 0})
    assert e.value.code == "LEVEL_NOT_FOUND" and box["low"] in e.value.data["valid_levels"]
    with pytest.raises(RevitError) as e:
        revit.call("create_line_based_element", {"elementType": "wall", "level": box["low"], "typeName": "No Such Type",
                                                 "startX": 0, "startY": 0, "endX": 1000, "endY": 0})
    assert e.value.code == "TYPE_NOT_FOUND" and e.value.data["valid_wall_types"]
    with pytest.raises(RevitError) as e:
        revit.call("create_line_based_element", {"elementType": "wall", "level": box["low"], "startx": 0,
                                                 "startY": 0, "endX": 1000, "endY": 0})
    assert e.value.code == "INVALID_PARAMS"


def test_wall_location_line_keeps_the_given_points(revit, box):
    wall = revit.call("create_line_based_element", {
        "elementType": "wall", "level": box["low"], "height": 2500, "locationLine": "finish_face_exterior",
        "startX": OX + 10000, "startY": OY, "endX": OX + 14000, "endY": OY})
    box["extra"].append(wall["elementId"])
    assert wall["startMm"] == [OX + 10000, OY] and wall["endMm"] == [OX + 14000, OY]
    assert wall["locationLine"] == "finish_face_exterior" and wall["heightMm"] == 2500


def test_floor_floor_opening_and_shaft(revit, box):
    floor = revit.call("create_surface_based_element", {"elementType": "floor", "level": box["high"],
                                                        "points": [{"x": x, "y": y} for x, y in box["corners"]]})
    box["extra"].append(floor["elementId"])
    assert floor["areaM2"] == pytest.approx(24, rel=0.01)
    hole = revit.call("create_floor_opening", {"floorId": floor["elementId"], "points": [
        {"x": OX + 500, "y": OY + 500}, {"x": OX + 1500, "y": OY + 500}, {"x": OX + 1500, "y": OY + 1500}, {"x": OX + 500, "y": OY + 1500}]})
    box["extra"].append(hole["elementId"])
    shaft = revit.call("create_floor_opening", {"baseLevel": box["low"], "topLevel": box["high"], "baseOffset": 2000, "topOffset": 500,
                                                "points": [{"x": OX + 3000, "y": OY + 1000}, {"x": OX + 4000, "y": OY + 1000},
                                                           {"x": OX + 4000, "y": OY + 3000}, {"x": OX + 3000, "y": OY + 3000}]})
    box["extra"].append(shaft["elementId"])
    assert shaft["kind"] == "shaft"


def test_door_and_window_by_offset_along_wall(revit, box):
    door_type, window_type = first_type(revit, "OST_Doors"), first_type(revit, "OST_Windows")
    door = revit.call("create_point_based_element", {"level": box["low"], "host_id": box["walls"][0], "hostOffset": 1500,
                                                     "typeId": door_type["typeId"]})
    box["extra"].append(door["elementId"])
    assert door["host_id"] == box["walls"][0]
    assert door["location_mm"]["x"] == pytest.approx(OX + 1500, abs=1) and door["location_mm"]["y"] == pytest.approx(OY, abs=1)
    window = revit.call("create_point_based_element", {"level": box["low"], "host_id": box["walls"][2], "hostOffset": 2000,
                                                       "typeName": window_type["typeName"], "familyName": window_type["familyName"],
                                                       "builtInCategory": "OST_Windows", "sillHeight": 900})
    box["extra"].append(window["elementId"])
    assert window["sillHeightMm"] == pytest.approx(900, abs=1)
    with pytest.raises(RevitError) as e:
        revit.call("create_point_based_element", {"level": box["low"], "host_id": box["walls"][0], "hostOffset": 99999,
                                                  "typeId": door_type["typeId"]})
    assert e.value.code == "OFFSET_OUT_OF_RANGE"


def test_rooms_create_list_and_boundaries(revit, box):
    with pytest.raises(RevitError) as e:
        revit.call("create_room", {"x": OX - 5000, "y": OY - 5000, "level": box["low"], "name": "Outside"})
    assert e.value.code in ("ROOM_NOT_ENCLOSED", "NOT_COMMITTED")
    assert revit.call("auto_create_rooms_from_walls", {"level_name": box["low"]})["candidate_count"] >= 1  # dry run
    room = revit.call("create_room", {"x": OX + 3000, "y": OY + 2000, "level": box["low"], "name": "Test Room",
                                      "number": "CT1", "department": "CT-FLAT"})
    box["extra"].append(room["elementId"])
    assert room["department"] == "CT-FLAT" and room["areaM2"] > 20
    listed = revit.call("list_rooms", {"department": "CT-FLAT"})
    assert [r["element_id"] for r in listed["rooms"]] == [room["elementId"]]
    boundary = revit.call("get_room_boundaries", {"room_id": room["elementId"]})
    assert boundary["loop_count"] == 1 and len(boundary["loops"][0]["segments"]) == 4


def test_counts_and_warnings_for_given_ids(revit, box):
    ids = box["levels"] + box["walls"] + [999999999]
    stats = revit.call("analyze_model_statistics", {"elementIds": ids, "groupByLevel": True})
    walls = [r for r in stats["categories"] if r["builtInCategory"] == "OST_Walls"]
    assert walls == [{"category": walls[0]["category"], "builtInCategory": "OST_Walls", "count": 4, "level": box["low"]}]
    assert stats["missingIds"] == [999999999]
    assert revit.call("get_model_warnings_summary", {"elementIds": box["walls"]})["total_warnings"] == 0


def test_delete_counts_cascaded_elements(revit, box):
    door_type = first_type(revit, "OST_Doors")
    wall = revit.call("create_line_based_element", {"elementType": "wall", "level": box["low"], "height": 3000,
                                                    "startX": OX + 20000, "startY": OY, "endX": OX + 25000, "endY": OY})
    door = revit.call("create_point_based_element", {"level": box["low"], "host_id": wall["elementId"], "hostOffset": 2000,
                                                     "typeId": door_type["typeId"]})
    result = revit.call("delete_element", {"elementIds": [wall["elementId"], door["elementId"]]})
    assert result["failedCount"] == 0 and result["deleted"] == 2
