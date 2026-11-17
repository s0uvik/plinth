"""Turn validated floors into a build plan (PRD: Data contracts). Keys are stable and derived only from
the layout, so the same spec always produces the same plan.

Ops for the frame, stairs, chajjas and parapet come after the walls, slabs, openings, rooms and shafts in the
op list (the runner orders by phase anyway), so a plan diff shows them as additions."""

from __future__ import annotations

from dataclasses import dataclass
from string import ascii_uppercase

from plinth.engine import structure
from plinth.engine.floors import BuiltFloor
from plinth.engine.stair import StairDesign
from plinth.engine.templates import PlateLayout
from plinth.plan import BuildPlan

GRID_OVERRUN = 1500
SHAFT_BASE_OFFSET, SHAFT_TOP_OFFSET = 1000, 500  # cuts the slabs of floors 1..top, not ground or roof
EXT_HALF = 115


def level_names(upper_floors: int, prefix: str) -> list[str]:
    return [f"{prefix}Ground Floor"] + [f"{prefix}Floor {i}" for i in range(1, upper_floors + 1)] + [f"{prefix}Roof"]


def _rect_points(r) -> list[tuple[int, int]]:
    return [(r.x0, r.y0), (r.x1, r.y0), (r.x1, r.y1), (r.x0, r.y1)]


@dataclass(frozen=True)
class Extras:
    """What goes around the floor plans: the RCC frame (None for load-bearing walls), the stair of each storey,
    sunshade depth and parapet height (0: none)."""

    frame: structure.Frame | None
    stair: StairDesign
    chajja_mm: int
    parapet_mm: int


def emit(plate: PlateLayout, floors: list[BuiltFloor], floor_to_floor: int, wall_types: tuple[str, str], slab: str,
         prefix: str, spec_hash: str, extras: Extras | None = None) -> BuildPlan:
    levels = level_names(len(floors) - 1, prefix)
    ops: list[dict] = [{"op": "level", "key": f"L{i}", "name": name, "elevation_mm": i * floor_to_floor}
                       for i, name in enumerate(levels)]

    p, core = plate.plate, plate.core
    xs, ys = structure.grid_lines(plate)
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
    if extras is not None:
        ops += _extras(extras, p, floors, levels)
    return BuildPlan.model_validate({"spec_hash": spec_hash, "ops": ops})


def _extras(extras: Extras, plate_rect, floors: list[BuiltFloor], levels: list[str]) -> list[dict]:
    ops: list[dict] = []
    top_storey = len(floors) - 1
    tag = [f"F{i}" for i in range(len(levels) - 1)] + ["ROOF"]  # beams at a level share its slab's prefix
    if extras.frame is not None:
        for i in range(len(floors)):
            for k, col in enumerate(extras.frame.columns, 1):
                column = {"op": "column", "key": f"F{i}-C{k:03d}", "level": levels[i], "top_level": levels[i + 1],
                          "point": col.point, "type": structure.COLUMN}
                if col.rotation:
                    column["rotation_deg"] = col.rotation
                ops.append(column)
        for i in range(1, len(levels)):
            for k, (start, end) in enumerate(extras.frame.beams, 1):
                ops.append({"op": "beam", "key": f"{tag[i]}-B{k:03d}", "level": levels[i], "start": start, "end": end,
                            "type": structure.BEAM})
            if i <= top_storey and extras.frame.landing_beam:  # The roof slab has no stair opening.
                start, end = extras.frame.landing_beam
                ops.append({"op": "beam", "key": f"{tag[i]}-BL", "level": levels[i], "start": start, "end": end,
                            "type": structure.BEAM})
    # The stair climbs every storey up to the top floor; the shaft stops there and the roof is not reached.
    for i in range(top_storey):
        d = extras.stair
        ops.append({"op": "stair", "key": f"F{i}-STAIR", "base_level": levels[i], "top_level": levels[i + 1],
                    "type": structure.STAIR, "width_mm": d.width, "tread_mm": d.tread,
                    "runs": [{"start": f.start, "end": f.end, "risers": f.risers} for f in d.flights]})
        ops.append({"op": "railing", "key": f"F{i}-RAIL", "type": structure.RAILING, "stair_key": f"F{i}-STAIR"})
    for floor in floors:
        for k, c in enumerate(structure.chajjas(floor, extras.chajja_mm), 1):
            ops.append({"op": "floor", "key": f"F{floor.level}-H{k:03d}", "level": levels[floor.level],
                        "type": structure.CHAJJA, "points": list(c.points), "offset_mm": c.offset})
    for k, (start, end) in enumerate(structure.parapet(plate_rect, extras.parapet_mm), 1):
        ops.append({"op": "wall", "key": f"ROOF-P{k}", "level": levels[-1], "type": structure.PARAPET,
                    "start": start, "end": end, "height_mm": extras.parapet_mm})
    return ops
