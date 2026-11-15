"""Buildable envelope (PRD F4.1): the plot minus the setbacks the city rules give for the building height.

Plot frame: x runs along the road frontage from the plot's left corner, y runs from the road boundary
(y = 0) into the plot. All values in mm.
"""

from __future__ import annotations

from dataclasses import dataclass
from b2m.rules import rule_set
from b2m.spec import BuildingSpec


class LayoutError(Exception):
    """The spec cannot be laid out; the message says why in terms a user can act on."""


@dataclass(frozen=True)
class Setbacks:
    front: int
    rear: int
    side_left: int
    side_right: int
    verified: bool
    source: str


@dataclass(frozen=True)
class Envelope:
    x0: int
    y0: int
    x1: int
    y1: int
    height: int
    setbacks: Setbacks

    @property
    def width(self) -> int:
        return self.x1 - self.x0

    @property
    def depth(self) -> int:
        return self.y1 - self.y0


def _table(city: str) -> dict:
    return rule_set(city)


def building_height(spec: BuildingSpec) -> int:
    """Ground to roof level: the ground storey (stilt or flats) plus the upper floors."""
    return (1 + spec.building.upper_floors) * spec.building.floor_to_floor_mm


def setbacks(spec: BuildingSpec) -> Setbacks:
    table = _table(spec.site.city)
    height = building_height(spec)
    for row in table["setbacks"]:
        if height <= row["max_height"]:
            return Setbacks(row["front"], row["rear"], row["side_left"], row["side_right"],
                            bool(table.get("verified")), str(table.get("source", "")))
    tallest = table["setbacks"][-1]["max_height"]
    raise LayoutError(f"Building height {height / 1000:.1f} m is above the {tallest / 1000:.1f} m the {spec.site.city} "
                      "setback table covers; lower the floor height or the number of floors.")


def envelope(spec: BuildingSpec) -> Envelope:
    width, depth = spec.site.plot_mm
    s = setbacks(spec)
    env = Envelope(s.side_left, s.front, width - s.side_right, depth - s.rear, building_height(spec), s)
    if env.width <= 0 or env.depth <= 0:
        raise LayoutError(f"Setbacks leave no buildable area on a {width / 1000:.1f} x {depth / 1000:.1f} m plot.")
    return env
