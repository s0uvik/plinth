"""One storey turned into geometry: walls derived from its spaces, then doors and windows placed."""

from __future__ import annotations

from dataclasses import dataclass, field

from b2m.engine import catalog
from b2m.engine.geometry import (Edge, GeometryError, Space, Wall, atomic_edges, check_no_overlaps, derive_walls,
                                 wall_thickness_on)
from b2m.engine.openings import PlacedOpening, Placer
from b2m.engine.templates import FloorLayout, PlateLayout

EXTERNAL, INTERNAL = "Brick 230", "Brick 115"


@dataclass
class BuiltFloor:
    level: int
    layout: FloorLayout
    edges: list[Edge]
    walls: list[Wall]
    thickness: dict[int, int]
    doors: list[PlacedOpening] = field(default_factory=list)
    windows: list[PlacedOpening] = field(default_factory=list)
    problems: list[str] = field(default_factory=list)

    @property
    def spaces(self) -> list[Space]:
        return self.layout.spaces


def classifier(spaces: list[Space]):
    """Walls between flats, around the core, the lift and the outside are 230 mm; inside a flat, 115 mm."""
    zone = {s.id: s.zone for s in spaces}

    def classify(a: str | None, b: str | None) -> str:
        if a is None or b is None or zone[a] != zone[b] or zone[a] in ("core", "lift"):
            return EXTERNAL
        return INTERNAL

    return classify


def build_floor(plate: PlateLayout, level: int) -> BuiltFloor:
    layout = plate.floor(level)
    try:
        check_no_overlaps(layout.spaces)
        edges = atomic_edges(layout.spaces)
    except GeometryError as e:
        return BuiltFloor(level, layout, [], [], {}, problems=[f"floor {level}: {e}"])
    walls = derive_walls(edges, classifier(layout.spaces), catalog.WALL_THICKNESS)
    floor = BuiltFloor(level, layout, edges, walls, wall_thickness_on(edges, walls))

    names = {s.id: f"{s.flat + ' ' if s.flat else ''}{s.name}" for s in layout.spaces}
    placer = Placer(layout.spaces, edges, walls)
    for spec in layout.doors:
        placed = placer.door(spec)
        if placed is None:
            other = names[spec.b] if spec.b else "outside"
            floor.problems.append(f"floor {level}: no room for a {spec.kind} door between {names[spec.a]} and {other}")
        else:
            floor.doors.append(placed)
    for spec in layout.windows:
        placed = placer.window(spec)
        if placed is None:
            floor.problems.append(f"floor {level}: no outside wall long enough for a window in {names[spec.space]}")
        else:
            floor.windows.append(placed)
    return floor
