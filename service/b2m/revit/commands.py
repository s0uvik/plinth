"""Build-plan op to add-in command mapping (PRD F6.2).

The only module that knows add-in command names and parameter names. The add-in speaks millimetres,
so lengths pass through unchanged; type names pass through and the add-in resolves them, returning the
valid options on a miss.
"""

from __future__ import annotations

from typing import Any

from b2m.plan import DoorOp, FloorOp, GridOp, LevelOp, Op, RoomOp, ShaftOp, WallOp, WindowOp

# Every command the build flow needs; checked against the add-in at startup (F6.1).
REQUIRED_COMMANDS = [
    "get_current_view_info", "batch_execute", "create_level", "create_grid", "create_line_based_element",
    "create_surface_based_element", "create_floor_opening", "create_point_based_element", "create_room",
    "delete_element", "analyze_model_statistics", "get_model_warnings_summary",
]

# Language-independent category each op creates, for read-back.
CATEGORY = {
    "level": "OST_Levels", "grid": "OST_Grids", "wall": "OST_Walls", "floor": "OST_Floors",
    "shaft": "OST_ShaftOpening", "door": "OST_Doors", "window": "OST_Windows", "room": "OST_Rooms",
}


def _points(points: list[tuple[float, float]]) -> list[dict[str, float]]:
    return [{"x": x, "y": y} for x, y in points]


def to_command(op: Op, plan: dict[str, Op], ids: dict[str, int]) -> dict[str, Any]:
    """One batch_execute sub-command for an op. ``ids`` maps already-built keys to element ids."""
    match op:
        case LevelOp():
            return {"command": "create_level", "params": {"elevation": op.elevation_mm, "name": op.name}}
        case GridOp():
            return {"command": "create_grid", "params": {
                "startX": op.start[0], "startY": op.start[1], "endX": op.end[0], "endY": op.end[1], "name": op.name}}
        case WallOp():
            params: dict[str, Any] = {
                "elementType": "wall", "level": op.level,
                "startX": op.start[0], "startY": op.start[1], "endX": op.end[0], "endY": op.end[1],
                "locationLine": op.location_line, "baseOffset": op.base_offset_mm,
            }
            if op.type:
                params["typeName"] = op.type
            if op.top_level:
                params.update(topLevel=op.top_level, topOffset=op.top_offset_mm)
            else:
                params["height"] = op.height_mm
            return {"command": "create_line_based_element", "params": params}
        case FloorOp():
            params = {"elementType": "floor", "level": op.level, "points": _points(op.points)}
            if op.type:
                params["typeName"] = op.type
            return {"command": "create_surface_based_element", "params": params}
        case ShaftOp():
            return {"command": "create_floor_opening", "params": {
                "points": _points(op.points), "baseLevel": op.base_level, "topLevel": op.top_level,
                "baseOffset": op.base_offset_mm, "topOffset": op.top_offset_mm}}
        case DoorOp() | WindowOp():
            host = plan[op.host_key]
            params = {
                "level": host.level, "host_id": ids[op.host_key], "hostOffset": op.offset_mm,
                "typeName": op.type, "builtInCategory": CATEGORY[op.op],
                "flipFacing": op.flip_facing, "flipHand": op.flip_hand,
            }
            if op.family:
                params["familyName"] = op.family
            if isinstance(op, WindowOp) and op.sill_mm is not None:
                params["sillHeight"] = op.sill_mm
            return {"command": "create_point_based_element", "params": params}
        case RoomOp():
            params = {"x": op.point[0], "y": op.point[1], "level": op.level, "name": op.name}
            if op.number:
                params["number"] = op.number
            if op.flat:
                params["department"] = op.flat
            return {"command": "create_room", "params": params}
    raise ValueError(f"unknown op {op!r}")


def expected_level(op: Op, plan: dict[str, Op]) -> str | None:
    """The level Revit reports for the element an op creates, or None where it has none we check."""
    match op:
        case WallOp() | FloorOp() | RoomOp():
            return op.level
        case DoorOp() | WindowOp():
            return plan[op.host_key].level
    return None
