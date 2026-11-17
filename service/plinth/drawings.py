"""Drawing set (PRD F9.1, F9.2): views, room tags, schedules and sheets for a built run, exported to one PDF
with the compliance report merged in, plus a 3D image for the results panel.

Sheets for a G+3 block (eight; a G+4 block has nine):
    A101..  one floor plan per storey (ground and every upper floor), rooms tagged
    A201    front elevation and a cross-section through the stair core
    A301    3D view
    A401    door and window schedules
    A402    area statement per flat
Every view, schedule and sheet id is recorded with the run (keys DWG-...), so removing the run removes them.
"""

from __future__ import annotations

from pathlib import Path
from typing import Any, Callable

from pypdf import PdfWriter

from plinth.plan import BuildPlan, LevelOp, WallOp

SCALE = 100
CROP_MARGIN = 3000  # mm around the building on plans
ELEVATION_DISTANCE = 6000  # mm in front of the building
SECTION_OVERRUN = 2000  # mm beyond the building on each side

DOOR_FIELDS = ["SCHEDULE_LEVEL_PARAM", "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_WIDTH_PARAM", "FAMILY_HEIGHT_PARAM", "ALL_MODEL_MARK"]
AREA_FIELDS = ["ROOM_DEPARTMENT", "ROOM_NUMBER", "ROOM_NAME", {"parameterName": "ROOM_AREA", "total": True}]


def _extent(plan: BuildPlan) -> tuple[float, float, float, float]:
    """The building's outline in plan, from its walls."""
    xs = [x for op in plan.ops if isinstance(op, WallOp) for x in (op.start[0], op.end[0])]
    ys = [y for op in plan.ops if isinstance(op, WallOp) for y in (op.start[1], op.end[1])]
    return min(xs), min(ys), max(xs), max(ys)


def build_drawing_set(revit, plan: BuildPlan, out_dir: Path, compliance_pdf: Path | None, prefix: str = "",
                      record: Callable[[str, int], None] = lambda key, element_id: None) -> dict[str, Any]:
    """Create the drawing set in Revit and write out_dir/set.pdf and out_dir/3d.png. ``record`` is told every element
    id created so the run can remove it later."""
    levels = [op for op in plan.ops if isinstance(op, LevelOp)]
    storeys, roof = levels[:-1], levels[-1]
    x0, y0, x1, y1 = _extent(plan)

    def create(key: str, command: str, params: dict[str, Any], id_field: str) -> dict[str, Any]:
        data = revit.call(command, params, timeout_seconds=300)
        record(key, data[id_field])
        return data

    crop = {"cropMinX": x0 - CROP_MARGIN, "cropMinY": y0 - CROP_MARGIN, "cropMaxX": x1 + CROP_MARGIN, "cropMaxY": y1 + CROP_MARGIN}
    plans = []
    for level in storeys:
        view = create(f"DWG-PLAN-{level.key}", "create_view",
                      {"viewType": "floor_plan", "level": level.name, "name": f"{prefix}{level.name} plan", "scale": SCALE, **crop},
                      "viewId")
        tags = revit.call("tag_all_rooms", {"view_id": view["viewId"]})
        for i, tag_id in enumerate(tags.get("tagIds") or []):
            record(f"DWG-TAG-{level.key}-{i}", tag_id)
        plans.append((level, view))

    elevation = create("DWG-ELEV", "create_view", {
        "viewType": "elevation", "level": storeys[0].name, "name": f"{prefix}Front elevation", "scale": SCALE,
        "x": (x0 + x1) / 2, "y": y0 - ELEVATION_DISTANCE, "direction": "north"}, "viewId")
    middle = (y0 + y1) / 2
    section = create("DWG-SECT", "create_view", {
        "viewType": "section", "name": f"{prefix}Section A-A", "scale": SCALE,
        "startX": x0 - SECTION_OVERRUN, "startY": middle, "endX": x1 + SECTION_OVERRUN, "endY": middle,
        "depth": (y1 - y0) / 2 + SECTION_OVERRUN, "bottom": -1000, "top": roof.elevation_mm + 3000}, "viewId")
    view3d = create("DWG-3D", "create_view", {"viewType": "3d", "name": f"{prefix}3D view"}, "viewId")

    doors = create("DWG-SCHED-DOORS", "create_schedule", {
        "category": "OST_Doors", "name": f"{prefix}Door schedule", "fields": DOOR_FIELDS,
        "sortGroup": [{"fieldName": "SCHEDULE_LEVEL_PARAM"}, {"fieldName": "ALL_MODEL_MARK"}]}, "scheduleId")
    windows = create("DWG-SCHED-WINDOWS", "create_schedule", {
        "category": "OST_Windows", "name": f"{prefix}Window schedule", "fields": DOOR_FIELDS,
        "sortGroup": [{"fieldName": "SCHEDULE_LEVEL_PARAM"}, {"fieldName": "ALL_MODEL_MARK"}]}, "scheduleId")
    areas = create("DWG-SCHED-AREAS", "create_schedule", {
        "category": "OST_Rooms", "name": f"{prefix}Area statement", "fields": AREA_FIELDS, "showGrandTotals": True,
        "sortGroup": [{"fieldName": "ROOM_DEPARTMENT", "showHeader": True, "showFooter": True}, {"fieldName": "ROOM_NUMBER"}]},
        "scheduleId")

    sheets: list[tuple[str, dict[str, Any]]] = []

    def sheet(number: str, name: str) -> dict[str, Any]:
        data = create(f"DWG-SHEET-{number}", "create_sheet", {"sheet_number": f"{prefix}{number}", "sheet_name": name}, "sheet_id")
        sheets.append((number, data))
        return data

    def place_view(sheet_data: dict[str, Any], view: dict[str, Any], x: float | None = None, y: float | None = None) -> None:
        params: dict[str, Any] = {"view_id": view["viewId"], "sheet_id": sheet_data["sheet_id"]}
        if x is not None:
            params.update(x_mm=x, y_mm=y)
        data = revit.call("place_view_on_sheet", params)
        record(f"DWG-VP-{data['viewport_id']}", data["viewport_id"])

    def place_schedule(sheet_data: dict[str, Any], schedule: dict[str, Any], x: float, y: float) -> None:
        data = revit.call("place_schedule_on_sheet", {"sheet_id": sheet_data["sheet_id"], "schedule_id": schedule["scheduleId"],
                                                      "x_mm": x, "y_mm": y})
        record(f"DWG-SI-{data['schedule_instance_id']}", data["schedule_instance_id"])

    for i, (level, view) in enumerate(plans, 1):
        place_view(sheet(f"A1{i:02d}", f"{level.name} plan"), view)
    s = sheet("A201", "Elevation and section")
    frame = s["frame_sheet_mm"]
    place_view(s, elevation, frame["center_x"] - frame["width"] / 4, frame["center_y"])
    place_view(s, section, frame["center_x"] + frame["width"] / 4, frame["center_y"])
    place_view(sheet("A301", "3D view"), view3d)
    s = sheet("A401", "Door and window schedules")
    frame = s["frame_sheet_mm"]
    place_schedule(s, doors, frame["min_x"] + 20, frame["max_y"] - 20)
    place_schedule(s, windows, frame["center_x"] + 10, frame["max_y"] - 20)
    s = sheet("A402", "Area statement")
    place_schedule(s, areas, s["frame_sheet_mm"]["min_x"] + 20, s["frame_sheet_mm"]["max_y"] - 20)

    out_dir.mkdir(parents=True, exist_ok=True)
    drawings = revit.call("export_pdf", {"output_folder": str(out_dir), "file_name": "drawings.pdf",
                                         "view_ids": [d["sheet_id"] for _, d in sheets], "overwrite": True}, timeout_seconds=600)
    image = revit.call("capture_view_image", {"view_id": view3d["viewId"], "output_folder": str(out_dir),
                                              "file_name": "3d.png", "overwrite": True})

    merged = out_dir / "set.pdf"
    writer = PdfWriter()
    writer.append(drawings["path"])
    if compliance_pdf is not None and compliance_pdf.exists():
        writer.append(str(compliance_pdf))
    with merged.open("wb") as f:
        writer.write(f)
    writer.close()
    return {"sheets": [number for number, _ in sheets], "set_pdf": str(merged), "image": image["path"],
            "drawings_pdf": drawings["path"], "compliance_included": compliance_pdf is not None and compliance_pdf.exists()}
