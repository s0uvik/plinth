"""Types and minimum sizes the layout engine designs to.

Type names are logical: the Revit client maps them to the project's own types (PRD F6.2). Room minimums
are design targets so the engine never emits an obviously undersized room; they follow the NBC 2016
Part 3 values commonly cited for dwellings, but the compliance rule set (Step 4) is the authority and is
checked against the official text, not these numbers.
"""

from __future__ import annotations

from dataclasses import dataclass

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

WINDOWS = {
    "living": OpeningType("Window 1500x1200", 1500, 1200, 900),
    "room": OpeningType("Window 1200x1200", 1200, 1200, 900),
    "toilet": OpeningType("Window 600x600", 600, 600, 1500),
}


@dataclass(frozen=True)
class RoomMin:
    width: int  # net, mm
    area_m2: float  # net


ROOM_MIN = {
    "living": RoomMin(2400, 9.5),
    "bedroom_master": RoomMin(2400, 9.5),
    "bedroom": RoomMin(2100, 7.5),
    "kitchen": RoomMin(1800, 5.0),
    "toilet": RoomMin(1200, 2.8),
}

# Clear distance kept between an opening's edge and a wall end or a wall junction.
OPENING_CLEARANCE = 300
# Shortest wall the engine emits.
MIN_WALL_LENGTH = 300
# Every coordinate is a multiple of this (mm), so walls meet exactly.
GRID = 10
