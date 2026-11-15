"""Build-plan op to add-in command mapping (PRD F6.2).

The only module that knows add-in command names and parameter names. The add-in speaks millimetres,
so lengths pass through unchanged; type names pass through and the add-in resolves them, returning the
valid options on a miss.
"""

from __future__ import annotations

from typing import Any

from b2m.plan import (KEY_SEPARATOR, BeamOp, ColumnOp, DoorOp, FloorOp, GridOp, LevelOp, Op, RailingOp, RoomOp, ShaftOp,
                      StairOp, WallOp, WindowOp)
from b2m.revit.types import TypeMap

NO_MAPPING = TypeMap()

# Every command the build flow needs; checked against the add-in at startup (F6.1).
REQUIRED_COMMANDS = [
    "get_current_view_info", "batch_execute", "create_level", "create_grid", "create_line_based_element",
    "create_surface_based_element", "create_floor_opening", "create_point_based_element", "create_room",
    "create_structural_column", "create_structural_beam", "create_stair", "create_railing",
    "delete_element", "analyze_model_statistics", "get_model_warnings_summary",
]

# Ops the runner calls directly, one call (and one Revit undo step) per op, never inside batch_execute:
# the add-in refuses create_stair in a batch, and a railing needs its stair's id and railings first.
DIRECT_OPS = frozenset({"stair", "railing"})

# Revit's built-in category for railings (shown as "Railings"); a stair's own railings are counted under it too.
RAILINGS = "OST_StairsRailing"

# Language-independent category each op creates, for read-back.
CATEGORY = {
    "level": "OST_Levels", "grid": "OST_Grids", "wall": "OST_Walls", "floor": "OST_Floors",
    "shaft": "OST_ShaftOpening", "door": "OST_Doors", "window": "OST_Windows", "room": "OST_Rooms",
    "column": "OST_StructuralColumns", "beam": "OST_StructuralFraming", "stair": "OST_Stairs",
    "railing": RAILINGS,
}


def _points(points: list[tuple[float, float]]) -> list[dict[str, float]]:
    return [{"x": x, "y": y} for x, y in points]


def to_command(op: Op, plan: dict[str, Op], ids: dict[str, int], types: TypeMap = NO_MAPPING) -> dict[str, Any]:
    """One batch_execute sub-command for an op. ``ids`` maps already-built keys to element ids;
    ``types`` converts logical type names to the project's own."""
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
                params["typeName"] = types.wall(op.type)
            if op.top_level:
                params.update(topLevel=op.top_level, topOffset=op.top_offset_mm)
            else:
                params["height"] = op.height_mm
            return {"command": "create_line_based_element", "params": params}
        case FloorOp():
            params = {"elementType": "floor", "level": op.level, "points": _points(op.points)}
            if op.type:
                params["typeName"] = types.floor(op.type)
            if op.offset_mm:  # Only when set, so plans without offsets give the same commands as before.
                params["heightOffset"] = op.offset_mm
            return {"command": "create_surface_based_element", "params": params}
        case ColumnOp():
            mapped = types.column(op.type, op.family)
            params = {"type_name": mapped.type, "level": op.level, "x": op.point[0], "y": op.point[1],
                      "top_level": op.top_level}
            if mapped.family:
                params["family_name"] = mapped.family
            if op.base_offset_mm:
                params["base_offset"] = op.base_offset_mm
            if op.top_offset_mm:
                params["top_offset"] = op.top_offset_mm
            if op.rotation_deg:
                params["rotation_deg"] = op.rotation_deg
            return {"command": "create_structural_column", "params": params}
        case BeamOp():
            mapped = types.beam(op.type, op.family)
            params = {"type_name": mapped.type, "level": op.level, "start_x": op.start[0], "start_y": op.start[1],
                      "end_x": op.end[0], "end_y": op.end[1]}
            if mapped.family:
                params["family_name"] = mapped.family
            return {"command": "create_structural_beam", "params": params}
        case StairOp():
            params = {"base_level": op.base_level, "top_level": op.top_level, "width_mm": op.width_mm,
                      "tread_mm": op.tread_mm,
                      "runs": [{"start": list(r.start), "end": list(r.end), "risers": r.risers} for r in op.runs]}
            if op.type:
                params["type_name"] = types.stair(op.type)
            if op.base_offset_mm:
                params["base_offset_mm"] = op.base_offset_mm
            return {"command": "create_stair", "params": params}
        case RailingOp():
            params = {}
            if op.type:
                params["type_name"] = types.railing(op.type)
            if op.stair_key is not None:
                params.update(stair_id=ids[op.stair_key], placement=op.placement)
            else:
                params.update(path=[list(pt) for pt in op.path], level=op.level)
                if op.base_offset_mm:
                    params["base_offset_mm"] = op.base_offset_mm
            return {"command": "create_railing", "params": params}
        case ShaftOp():
            return {"command": "create_floor_opening", "params": {
                "points": _points(op.points), "baseLevel": op.base_level, "topLevel": op.top_level,
                "baseOffset": op.base_offset_mm, "topOffset": op.top_offset_mm}}
        case DoorOp() | WindowOp():
            host = plan[op.host_key]
            mapped = types.door(op.type, op.family) if isinstance(op, DoorOp) else types.window(op.type, op.family)
            params = {
                "level": host.level, "host_id": ids[op.host_key], "hostOffset": op.offset_mm,
                "typeName": mapped.type, "builtInCategory": CATEGORY[op.op],
                "flipFacing": op.flip_facing, "flipHand": op.flip_hand,
            }
            if mapped.family:
                params["familyName"] = mapped.family
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


def created_ids(op: Op, data: dict[str, Any]) -> list[int]:
    """Element ids a command's result reports for an op, main element first. Each add-in command names its id
    field differently. A stair's runs and landings belong to it and go when it is deleted, so only the stair id
    is kept; railings Revit attached to it on its own are separate elements and are kept too."""
    match op:
        case ColumnOp():
            return [data["column_id"]]
        case BeamOp():
            return [data["element_id"]]
        case StairOp():
            return [data["stair_id"], *(data.get("railing_ids") or [])]
        case RailingOp():
            return list(data["railing_ids"])
    return [data["elementId"]]


def category_of(key: str, op: Op) -> str:
    """Read-back category of one run-store entry: a stair's extra entries are its railings."""
    if isinstance(op, StairOp) and key != op.key:
        return RAILINGS
    return CATEGORY[op.op]


def created_keys(op: Op, ids: list[int]) -> dict[str, int]:
    """Run-store entries for an op's elements: the op key for the first, key#2, key#3... for the rest."""
    return {(op.key if i == 0 else f"{op.key}{KEY_SEPARATOR}{i + 1}"): element_id for i, element_id in enumerate(ids)}


def op_key(created_key: str) -> str:
    """The op key behind a run-store entry (``F1-STAIR#2`` -> ``F1-STAIR``)."""
    return created_key.split(KEY_SEPARATOR, 1)[0]


def expected_level(op: Op, plan: dict[str, Op]) -> str | None:
    """The level Revit reports for the element an op creates, or None where it has none we check. Columns,
    beams, stairs and railings are counted per category only: whether Revit files them under a base, reference
    or host level is not something the read-back should depend on."""
    match op:
        case WallOp() | FloorOp() | RoomOp():
            return op.level
        case DoorOp() | WindowOp():
            return plan[op.host_key].level
    return None
