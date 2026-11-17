"""Geometry validation (PRD Step 2): checks that make the build plan warning-free in Revit, plus room
minimums and access. Returns plain problems; an empty list means the floor may be built."""

from __future__ import annotations

from collections import deque

from plinth.engine import catalog
from plinth.engine.floors import BuiltFloor
from plinth.engine.geometry import net_area_mm2, net_rect
from plinth.engine.templates import PlateLayout

ANCHOR_CLEARANCE = 500  # room point to its cell's edges


def validate_floor(plate: PlateLayout, floor: BuiltFloor, floor_to_floor: int, slab: int) -> list[str]:
    problems = list(floor.problems)
    if not floor.walls:
        return problems or [f"floor {floor.level}: no walls"]
    level = floor.level
    full = bool(floor.layout.flats)

    # Every coordinate on the grid, so walls meet exactly.
    for s in floor.spaces:
        for r in s.cells:
            if any(v % catalog.GRID for v in r):
                problems.append(f"floor {level}: {s.id} has a corner off the {catalog.GRID} mm grid: {r}")

    # A full floor tiles the plate exactly: no gaps that would become unbounded voids.
    if full:
        covered = sum(r.area for s in floor.spaces for r in s.cells)
        if covered != plate.plate.area:
            problems.append(f"floor {level}: spaces cover {covered / 1e6:.2f} m² of a {plate.plate.area / 1e6:.2f} m² plate")

    for w in floor.walls:
        if w.length < catalog.MIN_WALL_LENGTH:
            problems.append(f"floor {level}: wall at {w.start}-{w.end} is shorter than {catalog.MIN_WALL_LENGTH} mm")

    # Room sizes, after half-wall insets.
    for s in floor.spaces:
        need = catalog.ROOM_MIN.get(s.kind)
        if need is None:
            continue
        r = net_rect(s, s.anchor, floor.edges, floor.thickness)
        if min(r.width, r.height) < need.width:
            problems.append(f"floor {level}: {s.flat} {s.name} is {min(r.width, r.height)} mm wide; needs {need.width} mm")
        area = net_area_mm2(s, floor.edges, floor.thickness) / 1e6
        if area < need.area_m2:
            problems.append(f"floor {level}: {s.flat} {s.name} is {area:.2f} m²; needs {need.area_m2} m²")

    # Room points well inside their room.
    for s in floor.spaces:
        if s.room:
            r = s.cells[s.anchor]
            if min(r.width, r.height) < 2 * ANCHOR_CLEARANCE:
                problems.append(f"floor {level}: {s.id} is too small to hold a room point")

    # Openings fit under the wall top (walls run floor to floor; the slab sits on top).
    head_room = floor_to_floor - slab - 100
    for o in floor.doors + floor.windows:
        if o.type.sill + o.type.height > head_room:
            problems.append(f"floor {level}: {o.type.name} is taller than the {head_room} mm available")

    # Habitable rooms and kitchens have a window.
    lit = {o.spaces[0] for o in floor.windows}
    for s in floor.spaces:
        if s.kind in ("living", "bedroom", "bedroom_master", "kitchen") and s.id not in lit:
            problems.append(f"floor {level}: {s.flat} {s.name} has no window")

    # Every room reachable: from the lobby on every floor; the lobby from outside on the ground floor.
    graph: dict[str | None, set[str | None]] = {}
    for o in floor.doors:
        a, b = o.spaces
        graph.setdefault(a, set()).add(b)
        graph.setdefault(b, set()).add(a)
    start = None if level == 0 else "core.lobby"
    seen, queue = {start}, deque([start])
    while queue:
        for nxt in graph.get(queue.popleft(), ()):
            if nxt not in seen:
                seen.add(nxt)
                queue.append(nxt)
    for s in floor.spaces:
        if s.room and s.id not in seen:
            problems.append(f"floor {level}: {s.flat or ''} {s.name} cannot be reached".replace("  ", " "))
    return problems
