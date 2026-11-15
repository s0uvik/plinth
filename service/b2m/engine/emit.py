"""Turn validated floors into a build plan (PRD: Data contracts). Keys are stable and derived only from
the layout, so the same spec always produces the same plan."""

from __future__ import annotations

from string import ascii_uppercase

from b2m.engine.floors import BuiltFloor
from b2m.engine.templates import PlateLayout
from b2m.plan import BuildPlan

GRID_OVERRUN = 1500
SHAFT_BASE_OFFSET, SHAFT_TOP_OFFSET = 1000, 500  # cuts the slabs of floors 1..top, not ground or roof
EXT_HALF = 115


def level_names(upper_floors: int, prefix: str) -> list[str]:
    return [f"{prefix}Ground Floor"] + [f"{prefix}Floor {i}" for i in range(1, upper_floors + 1)] + [f"{prefix}Roof"]


def _rect_points(r) -> list[tuple[int, int]]:
    return [(r.x0, r.y0), (r.x1, r.y0), (r.x1, r.y1), (r.x0, r.y1)]


def emit(plate: PlateLayout, floors: list[BuiltFloor], floor_to_floor: int, wall_types: tuple[str, str], slab: str,
         prefix: str, spec_hash: str) -> BuildPlan:
    levels = level_names(len(floors) - 1, prefix)
    ops: list[dict] = [{"op": "level", "key": f"L{i}", "name": name, "elevation_mm": i * floor_to_floor}
                       for i, name in enumerate(levels)]

    p, core = plate.plate, plate.core
    xs = sorted({p.x0, core.x0, core.x1, p.x1})
    ys = sorted({p.y0, p.y1} | {p.y0 + d for _, _, row, _, _, d in plate.slots if row == "front"})
    for i, x in enumerate(xs, 1):
        ops.append({"op": "grid", "key": f"G{i}", "name": f"{prefix}{i}",
                    "start": (x, p.y0 - GRID_OVERRUN), "end": (x, p.y1 + GRID_OVERRUN)})
    for letter, y in zip(ascii_uppercase, ys):
        ops.append({"op": "grid", "key": f"G{letter}", "name": f"{prefix}{letter}",
                    "start": (p.x0 - GRID_OVERRUN, y), "end": (p.x1 + GRID_OVERRUN, y)})

    for floor in floors:
        i = floor.level
        base, top = levels[i], levels[i + 1]
        wall_keys = []
        for k, w in enumerate(floor.walls, 1):
            key = f"F{i}-W{k:03d}"
            wall_keys.append(key)
            ops.append({"op": "wall", "key": key, "level": base, "top_level": top, "type": w.type,
                        "start": w.start, "end": w.end})
        ops.append({"op": "floor", "key": f"F{i}-SLAB", "level": base, "type": slab, "points": _rect_points(p)})
        for k, d in enumerate(floor.doors, 1):
            ops.append({"op": "door", "key": f"F{i}-D{k:03d}", "host_key": wall_keys[d.wall], "type": d.type.name,
                        "offset_mm": d.offset})
        for k, wdw in enumerate(floor.windows, 1):
            ops.append({"op": "window", "key": f"F{i}-N{k:03d}", "host_key": wall_keys[wdw.wall], "type": wdw.type.name,
                        "offset_mm": wdw.offset, "sill_mm": wdw.type.sill})
        rooms = [s for s in floor.spaces if s.room]
        for k, s in enumerate(rooms, 1):
            room = {"op": "room", "key": f"F{i}-R{k:03d}", "level": base, "point": s.cells[s.anchor].center,
                    "name": s.name, "number": s.number}
            if s.flat:
                room["flat"] = s.flat
            ops.append(room)
    ops.append({"op": "floor", "key": "ROOF-SLAB", "level": levels[-1], "type": slab, "points": _rect_points(p)})

    top_floor = levels[-2]
    stair = core.stair
    ops.append({"op": "shaft", "key": "SHAFT-STAIR", "base_level": levels[0], "top_level": top_floor,
                "base_offset_mm": SHAFT_BASE_OFFSET, "top_offset_mm": SHAFT_TOP_OFFSET,
                "points": [(stair.x0 + EXT_HALF, stair.y0 + EXT_HALF), (stair.x1 - EXT_HALF, stair.y0 + EXT_HALF),
                           (stair.x1 - EXT_HALF, stair.y1), (stair.x0 + EXT_HALF, stair.y1)]})
    if core.lift:
        lift = core.lift
        ops.append({"op": "shaft", "key": "SHAFT-LIFT", "base_level": levels[0], "top_level": top_floor,
                    "base_offset_mm": SHAFT_BASE_OFFSET, "top_offset_mm": SHAFT_TOP_OFFSET,
                    "points": _rect_points(type(lift)(lift.x0 + EXT_HALF, lift.y0 + EXT_HALF, lift.x1 - EXT_HALF, lift.y1 - EXT_HALF))})
    return BuildPlan.model_validate({"spec_hash": spec_hash, "ops": ops})
