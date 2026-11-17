"""One storey turned into geometry: walls derived from its spaces, then doors and windows placed."""

from __future__ import annotations

from dataclasses import dataclass, field

from plinth.engine import catalog
from plinth.engine.geometry import (Edge, GeometryError, Space, Wall, atomic_edges, check_no_overlaps, derive_walls,
                                 net_rect, wall_thickness_on)
from plinth.engine.openings import PlacedOpening, Placer
from plinth.engine.templates import FloorLayout, PlateLayout
from plinth.rules import rule_set

EXTERNAL, INTERNAL = "Brick 230", "Brick 115"
# Window area as a share of net floor area, by room kind, from the NBC rule file.
WINDOW_RATIO = {kind: float(rule["min_ratio"]) for rule in rule_set("nbc2016")["rules"] if rule["type"] == "window_ratio"
                for kind in rule["kinds"]}


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


def ventilated_area_mm2(space: Space, floor: "BuiltFloor") -> int:
    """Floor area the window rule applies to: the room itself, not a passage that opens off it."""
    return net_rect(space, space.anchor, floor.edges, floor.thickness).area


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
    spaces = {s.id: s for s in layout.spaces}
    for spec in layout.windows:
        # Keep adding windows until the room reaches the ventilation ratio the rule file asks for.
        needed = WINDOW_RATIO.get(spaces[spec.space].kind, 0) * ventilated_area_mm2(spaces[spec.space], floor)
        glazed = 0
        options = catalog.WINDOW_OPTIONS[spec.kind]
        while True:
            remaining = needed - glazed
            # Smallest size that covers what is still needed, else the largest that fits.
            order = [o for o in options if o.width * o.height >= remaining] +                     [o for o in reversed(options) if o.width * o.height < remaining]
            placed = next((p for p in (placer.window(spec, o) for o in order) if p is not None), None)
            if placed is None:
                break
            floor.windows.append(placed)
            glazed += placed.type.width * placed.type.height
            if glazed >= needed:
                break
        if glazed == 0:
            floor.problems.append(f"floor {level}: no outside wall long enough for a window in {names[spec.space]}")
        elif glazed < needed:
            floor.problems.append(f"floor {level}: {names[spec.space]} has {glazed / 1e6:.2f} m² of window; "
                                  f"it needs {needed / 1e6:.2f} m²")
    return floor
