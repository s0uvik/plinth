"""Types and minimum sizes the layout engine designs to.

Type names are logical: the Revit client maps them to the project's own types (PRD F6.2). Room minimums
come from b2m/rules/nbc2016.yaml, the file the compliance checker uses.
"""

from __future__ import annotations

from dataclasses import dataclass

from b2m.rules import rule_set

WALL_THICKNESS = {"Brick 230": 230, "Brick 115": 115}
SLAB_THICKNESS = {"RCC 150": 150}


@dataclass(frozen=True)
class OpeningType:
    name: str
    width: int
    height: int
    sill: int = 0


DOORS = {
    "entry": OpeningType("Door 1000x2100", 1000, 2100),
    "room": OpeningType("Door 900x2100", 900, 2100),
    "toilet": OpeningType("Door 750x2100", 750, 2100),
    "building": OpeningType("Door 1200x2100", 1200, 2100),
}

# Window sizes per room kind, smallest first. The engine uses the smallest that meets the room's
# ventilation ratio, a larger one if not, and more than one window only when no single size is enough.
WINDOW_OPTIONS = {
    "living": [OpeningType("Window 1500x1200", 1500, 1200, 900), OpeningType("Window 1800x1500", 1800, 1500, 900),
               OpeningType("Window 2400x1500", 2400, 1500, 900)],
    "room": [OpeningType("Window 1200x1200", 1200, 1200, 900), OpeningType("Window 1500x1200", 1500, 1200, 900),
             OpeningType("Window 1800x1500", 1800, 1500, 900)],
    "toilet": [OpeningType("Window 600x600", 600, 600, 1500)],
}
WINDOWS = {kind: options[0] for kind, options in WINDOW_OPTIONS.items()}


@dataclass(frozen=True)
class RoomMin:
    width: int  # net, mm
    area_m2: float  # net


# Design minimums come from the same rule file the compliance checker tests, so the two cannot drift.
ROOM_MIN = {kind: RoomMin(int(v["width_mm"]), float(v["area_m2"])) for kind, v in rule_set("nbc2016")["room_minimums"].items()}

# Clear distance kept between an opening's edge and a wall end or a wall junction.
OPENING_CLEARANCE = 300
# Shortest wall the engine emits.
MIN_WALL_LENGTH = 300
# Every coordinate is a multiple of this (mm), so walls meet exactly.
GRID = 10
