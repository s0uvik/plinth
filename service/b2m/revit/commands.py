"""Build-plan op to add-in command mapping (PRD F6.2).

The only module that knows add-in command names and parameter names. The add-in speaks millimetres,
so lengths pass through unchanged; type names pass through and the add-in resolves them, returning the
valid options on a miss.
"""

from __future__ import annotations

from typing import Any

from b2m.plan import (KEY_SEPARATOR, BeamOp, ColumnOp, DoorOp, FloorOp, FloorTypeOp, FoundationSlabOp, GridOp, LevelOp,
                      Op, OpeningTypeOp, RailingOp, RoomOp, RoomSeparatorOp, ShaftOp, StairOp, WallOp, WallTypeOp,
                      WindowOp)
from b2m.revit.types import TypeMap

NO_MAPPING = TypeMap()

# Every command the build flow needs; checked against the add-in at startup (F6.1).
REQUIRED_COMMANDS = [
    "get_current_view_info", "batch_execute", "create_level", "create_grid", "create_line_based_element",
    "create_surface_based_element", "create_floor_opening", "create_point_based_element", "create_room",
    "create_structural_column", "create_structural_beam", "create_stair", "create_railing",
    "delete_element", "analyze_model_statistics", "get_model_warnings_summary",
]

# What a plan from imported drawings (Step 9) needs on top: its type pre-steps, rafts and room separation lines.
IMPORT_COMMANDS = [
    "ensure_wall_type", "ensure_floor_type", "ensure_family_type", "create_foundation_slab", "create_room_separator",
]

# Ops the runner calls directly, one call (and one Revit undo step) per op, never inside batch_execute:
# the add-in refuses create_stair in a batch, and a railing needs its stair's id and railings first; type pre-steps
# may open, save or duplicate types, which a batch rollback cannot undo, and their results rename what later ops use.
DIRECT_OPS = frozenset({"stair", "railing", "wall_type", "floor_type", "opening_type"})

# Revit's built-in category for railings (shown as "Railings"); a stair's own railings are counted under it too.
RAILINGS = "OST_StairsRailing"

# Language-independent category each op creates, for read-back. Type pre-steps create types, not elements: none.
CATEGORY = {
    "level": "OST_Levels", "grid": "OST_Grids", "wall": "OST_Walls", "floor": "OST_Floors",
    "shaft": "OST_ShaftOpening", "door": "OST_Doors", "window": "OST_Windows", "room": "OST_Rooms",
    "column": "OST_StructuralColumns", "beam": "OST_StructuralFraming", "stair": "OST_Stairs",
    "railing": RAILINGS, "foundation_slab": "OST_StructuralFoundation", "room_separator": "OST_RoomSeparationLines",
}

# What ensure_* reports when the type was already there (nothing was made, so nothing to remove later).
TYPE_FOUND_ACTIONS = frozenset({"existing", "found", "reused"})


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
        case RoomSeparatorOp():
            return {"command": "create_room_separator", "params": {
                "points": _points(op.points), "level_name": op.level, "close_loop": op.close_loop}}
        case FoundationSlabOp():
            params = {"level": op.level, "points": [list(pt) for pt in op.points], "type_name": types.floor(op.type)}
            if op.offset_mm:
                params["height_offset_mm"] = op.offset_mm
            return {"command": "create_foundation_slab", "params": params}
        case WallTypeOp():
            params = {"thickness_mm": op.thickness_mm, "function": op.function, "name": op.name}
            if op.base_type_name:
                params["base_type_name"] = op.base_type_name
            if op.layers:
                params["layers"] = [layer.model_dump(exclude_none=True) for layer in op.layers]
            return {"command": "ensure_wall_type", "params": params}
        case FloorTypeOp():
            params = {"thickness_mm": op.thickness_mm, "name": op.name, "structural": op.structural}
            if op.foundation:
                params["foundation"] = True
            if op.base_type_name:
                params["base_type_name"] = op.base_type_name
            if op.layers:
                params["layers"] = [layer.model_dump(exclude_none=True) for layer in op.layers]
            return {"command": "ensure_floor_type", "params": params}
        case OpeningTypeOp():
            params = {"category": op.category, "dimensions": {"width_mm": op.width_mm, "height_mm": op.height_mm}}
            if op.type_mark:
                params["type_mark"] = op.type_mark
            if op.family:
                params["family_name"] = op.family
            if op.hint:
                params["hint"] = op.hint
            return {"command": "ensure_family_type", "params": params}
    raise ValueError(f"unknown op {op!r}")


def resolved_type(op: WallTypeOp | FloorTypeOp | OpeningTypeOp, data: dict[str, Any], types: TypeMap) -> TypeMap:
    """``types`` with the op's logical name mapped to the project type the ensure_* command returned, so later ops
    use the type actually found or made (it may carry another name: an existing type of the same build-up)."""
    match op:
        case WallTypeOp():
            return types.with_wall(op.name, data["type_name"])
        case FloorTypeOp():
            return types.with_floor(op.name, data["type_name"])
        case OpeningTypeOp():
            return types.with_opening(op.category, op.name, data["type_name"], data.get("family_name"))
    raise ValueError(f"not a type op: {op!r}")


def created_type_ids(data: dict[str, Any]) -> list[int]:
    """The type an ensure_* call made (to remove with the run), or nothing when it was already there."""
    if data.get("action") in TYPE_FOUND_ACTIONS or data.get("type_id") is None:
        return []
    return [data["type_id"]]


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
        case FoundationSlabOp():
            return [data["element_id"]]
        case RoomSeparatorOp():
            return list(data["separator_ids"])
        case WallTypeOp() | FloorTypeOp() | OpeningTypeOp():
            return created_type_ids(data)
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
