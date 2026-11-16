"""A perfect read-back for the self-check tests: what read_back_elements would return for a model built exactly as
planned (ids assigned in plan order). Room areas come from the drawing (the plan carries none)."""

from __future__ import annotations

from typing import Any

from shapely.geometry import Polygon

from b2m.cad.building import RecognisedBuilding
from b2m.imported.to_plan import ImportPlan
from b2m.plan import KEY_SEPARATOR, TYPE_OPS
from b2m.revit.commands import CATEGORY, RAILINGS

MERGED_ROOM_AREA_M2 = 30.0  # a room merged from several labels has no drawn boundary; any enclosed area will do


def _p(point, z: float = 0.0) -> dict[str, float]:
    return {"x": point[0], "y": point[1], "z": z}


def _loop(points) -> list[dict[str, float]]:
    return [{"x": q[0], "y": q[1]} for q in points]


def perfect_readback(result: ImportPlan, building: RecognisedBuilding, first_id: int = 1000
                     ) -> tuple[dict[str, int], dict[int, dict[str, Any]]]:
    plan = result.plan
    by_key = plan.by_key()
    created: dict[str, int] = {}
    readback: dict[int, dict[str, Any]] = {}
    next_id = first_id
    elevation = {op.name: op.elevation_mm for op in plan.ops if op.op == "level"}
    wall_types = {op.name: op.thickness_mm for op in plan.ops if op.op == "wall_type"}
    floor_types = {op.name: op.thickness_mm for op in plan.ops if op.op == "floor_type"}
    opening_types = {(op.category, op.name): op for op in plan.ops if op.op == "opening_type"}
    frames = {f.name: (f.b_mm, f.h_mm) for f in result.standards.columns + result.standards.beams}
    rooms = {(p.sheet, r.id): r for p in building.plans for r in p.rooms}

    def new(key: str, record: dict[str, Any]) -> int:
        nonlocal next_id
        element_id = next_id
        next_id += 1
        created[key] = element_id
        readback[element_id] = {"id": element_id, "exists": True, **record}
        return element_id

    for op in plan.ordered():
        if op.op in TYPE_OPS:
            created[op.key] = next_id  # a type the pre-step made: recorded, never read back
            next_id += 1
            continue
        base = {"category": CATEGORY[op.op]}
        match op.op:
            case "level":
                new(op.key, {**base, "level_element": {"name": op.name, "elevation_mm": op.elevation_mm}})
            case "grid":
                new(op.key, {**base, "grid": {"name": op.name, "start": _p(op.start), "end": _p(op.end)}})
            case "wall":
                if op.top_level:
                    height = elevation[op.top_level] + op.top_offset_mm - elevation[op.level] - op.base_offset_mm
                    top = {"top_level": op.top_level, "top_offset_mm": op.top_offset_mm}
                else:
                    height, top = op.height_mm, {}
                new(op.key, {**base, "level": op.level, "location": {"start": _p(op.start), "end": _p(op.end)},
                             "wall": {"width_mm": wall_types[op.type], "base_level": op.level,
                                      "base_offset_mm": op.base_offset_mm, "height_mm": height, **top}})
            case "floor" | "foundation_slab":
                new(op.key, {**base, "level": op.level,
                             "floor": {"thickness_mm": floor_types[op.type], "height_offset_mm": op.offset_mm,
                                       "outline": [_loop(op.points)]}})
            case "column":
                b, h = frames[op.type]
                new(op.key, {**base, "level": op.level, "location": {"point": _p(op.point), "rotation_deg": op.rotation_deg},
                             "frame": {"b_mm": b, "h_mm": h, "base_level": op.level, "top_level": op.top_level,
                                       "base_offset_mm": op.base_offset_mm, "top_offset_mm": op.top_offset_mm}})
            case "beam":
                b, h = frames[op.type]
                new(op.key, {**base, "location": {"start": _p(op.start), "end": _p(op.end)},
                             "frame": {"b_mm": b, "h_mm": h, "reference_level": op.level}})
            case "door" | "window":
                t = opening_types[(op.op, op.type)]
                opening = {"host_id": created[op.host_key], "host_offset_mm": op.offset_mm, "width_mm": t.width_mm,
                           "height_mm": t.height_mm, "facing_flipped": op.flip_facing, "hand_flipped": op.flip_hand}
                if op.op == "window":
                    opening["sill_mm"] = op.sill_mm
                record = {**base, "level": by_key[op.host_key].level, "opening": opening}
                if t.type_mark:
                    record["type_mark"] = t.type_mark
                new(op.key, record)
            case "room":
                source = result.sources[op.key]
                r = rooms.get((source.sheet, source.element))
                area = r.area_m2 if r is not None and r.area_m2 is not None else (
                    Polygon(r.boundary).area / 1e6 if r is not None and r.boundary else MERGED_ROOM_AREA_M2)
                room = {"name": op.name, "number": op.number, "area_m2": round(area, 4), "level": op.level}
                if op.flat:
                    room["department"] = op.flat
                new(op.key, {**base, "level": op.level, "location": {"point": _p(op.point)}, "room": room})
            case "stair":
                risers = sum(r.risers for r in op.runs)
                riser = (elevation[op.top_level] - elevation[op.base_level] - op.base_offset_mm) / risers
                new(op.key, {**base, "stair": {"base_level": op.base_level, "top_level": op.top_level,
                                               "base_offset_mm": op.base_offset_mm, "risers": risers,
                                               "riser_height_mm": round(riser, 3), "tread_mm": op.tread_mm,
                                               "run_widths_mm": [op.width_mm] * len(op.runs)}})
            case "railing":
                new(op.key, {"category": RAILINGS, "railing": {"host_id": created[op.stair_key]}})
            case "shaft":
                new(op.key, {**base, "shaft": {"outline": [_loop(op.points)], "base_level": op.base_level,
                                               "top_level": op.top_level, "base_offset_mm": op.base_offset_mm,
                                               "top_offset_mm": op.top_offset_mm}})
            case "room_separator":
                points = list(op.points) + ([op.points[0]] if op.close_loop else [])
                for i, (a, b) in enumerate(zip(points, points[1:])):
                    key = op.key if i == 0 else f"{op.key}{KEY_SEPARATOR}{i + 1}"
                    new(key, {**base, "level": op.level, "curve": {"start": _p(a), "end": _p(b)}})
    return created, readback
