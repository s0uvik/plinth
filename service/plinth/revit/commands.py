"""Build-plan op to add-in command mapping (PRD F6.2).

The only module that knows add-in command names and parameter names. The add-in speaks millimetres,
so lengths pass through unchanged; type names pass through and the add-in resolves them, returning the
valid options on a miss.
"""

from __future__ import annotations

from typing import Any

from plinth.plan import (KEY_SEPARATOR, BeamOp, CircuitOp, ColumnOp, DoorOp, FamilyCreateOp, FamilyOp, FamilyTypeCopyOp,
                      FloorOp, FloorTypeOp, FoundationSlabOp, GridOp, LevelOp, MepRunOp, Op, OpeningTypeOp, RailingOp,
                      RoomOp, RoomSeparatorOp, ShaftOp, StairOp, TypesFromFileOp, WallOp, WallTypeOp, WindowOp)
from plinth.revit.types import TypeMap

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

# What a fit-out (Step 10, F14) needs on top: types from Revit's installed templates (list_types_in_file and
# copy_types_from_file open another file, so they run directly, never inside batch_execute), placed families, MEP runs
# with fittings and connections, and circuits. A plan that creates a box family (family_create, only when the user
# answered so) needs create_box_family too (BOX_FAMILY_COMMAND), checked only for such a plan.
FITOUT_COMMANDS = [
    "list_template_files", "list_types_in_file", "copy_types_from_file", "load_family_from_path",
    "create_lighting_fixture", "create_air_terminal", "create_mep_run", "create_electrical_circuit",
    "get_mep_element_connectors", "find_mep_disconnects", "list_mep_systems",
    "get_available_family_types", "get_type_parameters", "duplicate_family_type",
]

BOX_FAMILY_COMMAND = "create_box_family"

# Ops the runner calls directly, one call (and one Revit undo step) per op, never inside batch_execute:
# the add-in refuses create_stair in a batch, and a railing needs its stair's id and railings first; type pre-steps
# may open, save or duplicate types, which a batch rollback cannot undo, and their results rename what later ops use.
# Copying types from a template opens another file, and creating a box family builds and loads a family file, which
# the add-in refuses inside a batch. A circuit's panel assignment may be undone by Revit's failure handling on its own
# (reported for the whole request, success with rolled_back_by_failure): one call per circuit tells whose it was.
DIRECT_OPS = frozenset({"stair", "railing", "wall_type", "floor_type", "opening_type", "types_from_file",
                        "family_type_copy", "family_create", "circuit"})

# Placed families of these categories go through the MEP placement commands (snake_case parameters); the rest through
# create_point_based_element. All three place the same way (the add-in's FamilyPlacer).
FAMILY_COMMANDS = {"OST_LightingFixtures": "create_lighting_fixture", "OST_DuctTerminal": "create_air_terminal"}
# Reference planes: what a face-down placement adds beside the instance.
REFERENCE_PLANES = "OST_CLines"
MEP_CURVE = {"pipe": "OST_PipeCurves", "duct": "OST_DuctCurves", "conduit": "OST_Conduit", "cable_tray": "OST_CableTray"}
MEP_FITTING = {"pipe": "OST_PipeFitting", "duct": "OST_DuctFitting", "conduit": "OST_ConduitFitting",
               "cable_tray": "OST_CableTrayFitting"}
CIRCUITS = "OST_ElectricalCircuit"
# Run-store entry suffixes of an MEP run's elements: key, key#2... its segments; key#f1... its fittings (elbows, tees,
# transitions); key#p1... other pieces (the far part of a main broken for a tee).
FITTING_MARK, PIECE_MARK = "f", "p"

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


def fitout_commands(ops: list[Op]) -> list[str]:
    """What building these fit-out ops needs of the add-in on top of REQUIRED_COMMANDS, so an add-in without a command
    the plan does not use (create_box_family, the MEP ones) still builds it."""
    kinds = {op.op for op in ops}
    categories = {op.category for op in ops if isinstance(op, FamilyOp)}
    wanted = {"types_from_file": ["copy_types_from_file"],
              "family_type_copy": ["duplicate_family_type", "get_available_family_types"],
              "family_create": [BOX_FAMILY_COMMAND], "mep_run": ["create_mep_run"],
              "circuit": ["create_electrical_circuit"]}
    out = [c for kind, names in wanted.items() if kind in kinds for c in names]
    out += [c for category, c in FAMILY_COMMANDS.items() if category in categories]
    return list(dict.fromkeys(out))


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
        case TypesFromFileOp():
            return {"command": "copy_types_from_file", "params": {
                "path": op.path, "items": [item.model_dump(exclude_none=True) for item in op.items]}}
        case FamilyTypeCopyOp():
            # The source type's id is only known in the project (a copy from a template gets a new one): the runner
            # finds it and calls duplicate_family_type with source_type_id itself (see Runner._copy_type).
            return {"command": "duplicate_family_type", "params": {
                "new_type_name": op.name, "type_parameter_overrides": dict(op.values_mm)}}
        case FamilyCreateOp():
            params = {"family_name": op.family, "type_name": op.type, "category": op.category,
                      "width_mm": op.width_mm, "depth_mm": op.depth_mm, "height_mm": op.height_mm}
            if op.material:
                params["material_name"] = op.material
            if op.connectors:
                params["connectors"] = [c.model_dump(exclude_none=True) for c in op.connectors]
            return {"command": BOX_FAMILY_COMMAND, "params": params}
        case FamilyOp():
            return _family_command(op, ids, types)
        case MepRunOp():
            mapped = types.fitout_type(f"{op.kind}_type", op.type)
            params = {"kind": op.kind, "level": op.level, "points": [list(pt) for pt in op.points],
                      "type_name": mapped.type, "require_connections": op.require_connections}
            if op.system_type:
                params["system_type_name"] = types.fitout_type(f"{op.kind}_system_type", op.system_type).type
            for name, value in (("diameter", op.diameter_mm), ("width", op.width_mm), ("height", op.height_mm)):
                if value is not None:
                    params[name] = value
            for end, key in (("start_connect", op.start_key), ("end_connect", op.end_key)):
                if key:
                    params[end] = {"element_id": ids[key]}
            return {"command": "create_mep_run", "params": params}
        case CircuitOp():
            params = {"element_ids": [ids[k] for k in op.device_keys], "system_type": op.system_type}
            if op.panel_key:
                params["panel_id"] = ids[op.panel_key]
            if op.best_effort:
                params["best_effort"] = True
            return {"command": "create_electrical_circuit", "params": params}
    raise ValueError(f"unknown op {op!r}")


def _family_command(op: FamilyOp, ids: dict[str, int], types: TypeMap) -> dict[str, Any]:
    """create_point_based_element (camelCase) or, for lights and air terminals, the MEP placing commands (snake_case);
    options are only sent when set, so the add-in's placement rules see exactly what the plan asks for."""
    mapped = types.fitout_type("family_type", op.type, op.family)
    command = FAMILY_COMMANDS.get(op.category, "create_point_based_element")
    snake = command != "create_point_based_element"

    def name(camel: str, snake_name: str) -> str:
        return snake_name if snake else camel

    params: dict[str, Any] = {"level": op.level, "x": op.point[0], "y": op.point[1], name("typeName", "type_name"): mapped.type}
    if mapped.family:
        params[name("familyName", "family_name")] = mapped.family
    if not snake:
        params["builtInCategory"] = op.category
    if op.offset_mm:
        params["offset"] = op.offset_mm
    if op.rotation_deg:
        params["rotation"] = op.rotation_deg
    if op.host_key:
        params["host_id"] = ids[op.host_key]
    if op.wall_side:
        params[name("wallSide", "wall_side")] = op.wall_side
    if op.toward:
        params[name("towardX", "toward_x")], params[name("towardY", "toward_y")] = op.toward
    if op.face_down:
        params[name("faceDown", "face_down")] = True
    if op.flip_hand:
        params[name("flipHand", "flip_hand")] = True
    return {"command": command, "params": params}


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
        case TypesFromFileOp():
            # Everything the copy added (families, their types, materials and fitting families brought along): the
            # run removes exactly that. Types the project already had are not in it.
            return list(data.get("created_ids") or [])
        case FamilyTypeCopyOp():
            return [data["new_type_id"]] if data.get("new_type_id") is not None else []
        case FamilyCreateOp():
            # The family, its type and what the load brought along; nothing when the family was already there.
            return [] if data.get("action") == "already_there" else list(data.get("created_ids") or [])
        case FamilyOp():
            # The instance first, then a reference plane a face-down placement made.
            return list(data.get("created_ids") or [data.get("elementId", data.get("instance_id"))])
        case MepRunOp():
            return list(data["created_ids"])
        case CircuitOp():
            # best_effort: no circuit when none of its devices could go on it (reported by ``issues``)
            return [data["circuit_id"]] if data.get("circuit_id") is not None else []
    return [data["elementId"]]


def created_entries(op: Op, data: dict[str, Any]) -> dict[str, int]:
    """Run-store entries for what a command made for an op. An MEP run's entries say which are segments, fittings and
    other pieces (key, key#2... / key#f1... / key#p1...), so the read-back can count each in its category."""
    if not isinstance(op, MepRunOp):
        return created_keys(op, created_ids(op, data))
    segments = list(data.get("segment_ids") or [])
    fittings = [i for i in data.get("fitting_ids") or [] if i not in segments]
    others = [i for i in created_ids(op, data) if i not in segments and i not in fittings]
    entries = created_keys(op, segments)
    entries.update({f"{op.key}{KEY_SEPARATOR}{FITTING_MARK}{n}": i for n, i in enumerate(fittings, 1)})
    entries.update({f"{op.key}{KEY_SEPARATOR}{PIECE_MARK}{n}": i for n, i in enumerate(others, 1)})
    return entries


def issues(op: Op, data: dict[str, Any], response: dict[str, Any] | None = None) -> list[dict[str, Any]]:
    """What a command did not do of an op it otherwise built, each as {key, what, reason, ...}: a run's joins (and
    elbows) Revit refused (create_mep_run's failed_connections, with require_connections false), a circuit's devices
    left off it, a panel Revit refused (panel_error, or the panel assignment Revit's failure handling undid: the whole
    ``response`` says rolled_back_by_failure, the circuit stays) or no circuit at all (create_electrical_circuit's
    best_effort), a box family's connectors not made (a family of that name already in the project, or an add-in that
    does not make them)."""
    out: list[dict[str, Any]] = []
    match op:
        case MepRunOp():
            for row in data.get("failed_connections") or []:
                at = row.get("at")
                what = "elbow" if at == "bend" else "connection"
                extra = {k: row[k] for k in ("at", "element_id", "point_index") if row.get(k) is not None}
                out.append({"key": op.key, "what": what, "reason": row.get("reason") or "refused", **extra})
        case CircuitOp():
            for row in data.get("skipped") or []:
                out.append({"key": op.key, "what": "circuit_device", "element_id": row.get("element_id"),
                            "reason": row.get("reason") or "left off the circuit"})
            if data.get("panel_error"):
                out.append({"key": op.key, "what": "panel", "element_id": data.get("panel_id"),
                            "reason": data["panel_error"]})
            elif op.panel_key and data.get("circuit_id") is not None and (response or {}).get("rolled_back_by_failure"):
                said = "; ".join(str(f.get("message") or f) if isinstance(f, dict) else str(f)
                                 for f in (response or {}).get("failures") or [])
                out.append({"key": op.key, "what": "panel", "element_id": data.get("panel_id"),
                            "reason": "Revit undid the panel assignment" + (f": {said}" if said else "")})
            if data.get("circuit_id") is None:
                out.append({"key": op.key, "what": "circuit",
                            "reason": "not made: none of its devices has a free electrical connector"})
        case FamilyCreateOp() if op.connectors:
            made = data.get("connectors") or []
            if len(made) < len(op.connectors):
                why = ("the family is already in the project and is used as it is" if data.get("action") == "already_there"
                       else "the add-in made fewer (an add-in without box connectors?)")
                out.append({"key": op.key, "what": "connectors", "reason": f"{len(made)} of {len(op.connectors)} "
                            f"connectors made: {why}", "asked": [c.model_dump(exclude_none=True) for c in op.connectors]})
    return out


def category_of(key: str, op: Op) -> str:
    """Read-back category of one run-store entry: a stair's extra entries are its railings, a placed family's its
    reference plane, an MEP run's its segments, fittings and pieces."""
    if isinstance(op, StairOp) and key != op.key:
        return RAILINGS
    if isinstance(op, FamilyOp):
        return op.category if key == op.key else REFERENCE_PLANES
    if isinstance(op, MepRunOp):
        suffix = key.split(KEY_SEPARATOR, 1)[1] if KEY_SEPARATOR in key else ""
        return MEP_FITTING[op.kind] if suffix.startswith(FITTING_MARK) else MEP_CURVE[op.kind]
    if isinstance(op, CircuitOp):
        return CIRCUITS
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
    or host level is not something the read-back should depend on. Neither are placed families (a face-based one has
    no level of its own), MEP runs and circuits."""
    match op:
        case WallOp() | FloorOp() | RoomOp():
            return op.level
        case DoorOp() | WindowOp():
            return plan[op.host_key].level
    return None
