"""Parametric flat plans (1BHK, 2BHK, 3BHK) and their sizing.

Every flat is a rectangle beside the stair-and-lobby column ("side slot"). In flat-local coordinates,
u runs from the wall shared with the core (u = 0) to the outer side wall (u = w), and v runs from the
flat's outer end (v = 0, front or rear of the building) inwards (v = d).

    v=0  +--------------------------+   outer end: living room window
         |  Living / Dining         |   entry door from the lobby on the u=0 side
    v=L  +-------+------------------+
         |       |  Kitchen         |   rooms in one row along the outer side wall (u = w),
         |  pas- |  Toilet          |   each with a window, each opening onto the passage,
         |  sage |  Bedroom 2       |   which is part of the living space (no wall between)
         |       |  Bedroom 1       |
         |       |  Toilet (att.)   |   attached toilets open into the bedroom before them
    v=d  +-------+------------------+
        u=0     u=p                u=w
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from b2m.engine import catalog
from b2m.engine.geometry import Rect, Space, atomic_edges, derive_walls, net_area_mm2, wall_thickness_on

PASSAGE = 1200  # centreline width of the passage; about 1.03 m clear between 230 and 115 walls
ENTRY_SPAN = catalog.DOORS["entry"].width + 2 * catalog.OPENING_CLEARANCE
EXT_HALF, INT_HALF = 115, 57.5
SIDE_DEDUCT = EXT_HALF + INT_HALF  # a room in the row: partition one side, outer wall the other
MAX_FLAT_WIDTH = 6000
WEIGHT = {"living": 3, "bedroom_master": 2, "bedroom": 2, "kitchen": 1, "toilet": 0}


@dataclass(frozen=True)
class RoomDef:
    kind: str
    name: str
    attached: bool = False  # a toilet whose door opens into the room before it


def _r(kind: str, name: str, attached: bool = False) -> RoomDef:
    return RoomDef(kind, name, attached)


K, MASTER = _r("kitchen", "Kitchen"), _r("bedroom_master", "Bedroom 1")

# Room order along the outer wall after the living room; variants in preference order.
SEQUENCES: dict[str, list[tuple[RoomDef, ...]]] = {
    "1BHK": [(K, MASTER, _r("toilet", "Toilet 1"))],
    "2BHK": [
        (K, _r("toilet", "Toilet 2"), _r("bedroom", "Bedroom 2"), MASTER, _r("toilet", "Toilet 1", True)),
        (K, _r("toilet", "Toilet 1"), _r("bedroom", "Bedroom 2"), MASTER),
    ],
    "3BHK": [
        (K, _r("toilet", "Toilet 3"), _r("bedroom", "Bedroom 3"), _r("bedroom", "Bedroom 2"),
         _r("toilet", "Toilet 2", True), MASTER, _r("toilet", "Toilet 1", True)),
        (K, _r("toilet", "Toilet 2"), _r("bedroom", "Bedroom 3"), _r("bedroom", "Bedroom 2"), MASTER,
         _r("toilet", "Toilet 1", True)),
    ],
}


def sequence(utype: str, variant: int) -> tuple[RoomDef, ...]:
    """The room order for a variant; types with fewer variants keep their last one."""
    options = SEQUENCES[utype]
    return options[min(variant, len(options) - 1)]


class FlatError(Exception):
    """This flat cannot be laid out in the given slot; the message says why."""


@dataclass(frozen=True)
class Frame:
    """Maps flat-local (u, v) to plan (x, y): x = x_core + x_dir * u, y = y_end + y_dir * v."""

    x_core: int
    x_dir: int
    y_end: int
    y_dir: int

    def rect(self, u0: int, u1: int, v0: int, v1: int) -> Rect:
        return Rect.of(self.x_core + self.x_dir * u0, self.y_end + self.y_dir * v0,
                       self.x_core + self.x_dir * u1, self.y_end + self.y_dir * v1)


LOCAL = Frame(0, 1, 0, 1)


@dataclass(frozen=True)
class DoorSpec:
    a: str
    b: str | None  # None: to the outside
    kind: str  # catalog.DOORS key
    b_cell: int | None = None  # only this cell of space b may take the door (e.g. a flat's entry into the living room)


@dataclass(frozen=True)
class WindowSpec:
    space: str
    kind: str  # catalog.WINDOWS key


@dataclass(frozen=True)
class Flat:
    letter: str
    type: str
    variant: int
    w: int
    d: int
    living_length: int
    lengths: tuple[int, ...]
    spaces: tuple[Space, ...]
    doors: tuple[DoorSpec, ...]
    windows: tuple[WindowSpec, ...]

    @property
    def living_id(self) -> str:
        return f"{self.letter}.living"


def _up(value: float) -> int:
    return int(math.ceil(value / catalog.GRID) * catalog.GRID)


def room_net_width(w: int) -> float:
    return (w - PASSAGE) - SIDE_DEDUCT


def min_lengths(utype: str, variant: int, w: int, entry_from: int) -> tuple[int, list[int]]:
    """Smallest living length and room lengths (centreline, along v) for this flat width."""
    seq = sequence(utype, variant)
    net_w = room_net_width(w)
    widest_need = max(catalog.ROOM_MIN[r.kind].width for r in seq)
    if net_w < widest_need:
        raise FlatError(f"{utype}: rooms beside the passage would be {net_w / 1000:.2f} m wide; "
                        f"they need {widest_need / 1000:.2f} m, so the flat needs to be at least "
                        f"{(widest_need + SIDE_DEDUCT + PASSAGE) / 1000:.2f} m wide")
    rooms = []
    r = w - PASSAGE  # centreline width of the row of rooms
    for room in seq:
        need = catalog.ROOM_MIN[room.kind]
        # Net length must give the minimum width, and the net area (cell minus half-wall strips, ends
        # counted as the thicker outer wall) must reach the minimum area.
        length = max(need.width + SIDE_DEDUCT, (need.area_m2 * 1e6 + r * SIDE_DEDUCT) / net_w)
        if not room.attached:  # Its door is in the passage wall along its length, clear of both ends.
            door = catalog.DOORS["toilet" if room.kind == "toilet" else "room"]
            length = max(length, door.width + 2 * catalog.OPENING_CLEARANCE + catalog.GRID)
        rooms.append(_up(length))
    living_net_w = w - 2 * EXT_HALF
    need = catalog.ROOM_MIN["living"]
    living = _up(max(entry_from + ENTRY_SPAN + catalog.GRID,
                     need.width + SIDE_DEDUCT,
                     (need.area_m2 * 1e6 + w * SIDE_DEDUCT) / living_net_w))
    return living, rooms


def lay_out(letter: str, flat_id: str, utype: str, variant: int, w: int, d: int, entry_from: int,
            frame: Frame = LOCAL) -> Flat:
    """The flat's spaces, doors and windows at width w and depth d."""
    seq = sequence(utype, variant)
    living_min, room_min = min_lengths(utype, variant, w, entry_from)
    spare = d - living_min - sum(room_min)
    if spare < 0:
        raise FlatError(f"{utype} needs {(living_min + sum(room_min)) / 1000:.2f} m of depth; the slot has {d / 1000:.2f} m")

    # Share the spare depth by weight in 10 mm steps; the living room takes the remainder.
    steps = spare // catalog.GRID
    weights = [WEIGHT[r.kind] for r in seq]
    total = WEIGHT["living"] + sum(weights)
    extra = [steps * wt // total * catalog.GRID for wt in weights]
    living = living_min + spare - sum(extra)
    lengths = [m + e for m, e in zip(room_min, extra)]

    living_space = Space(
        id=f"{letter}.living", kind="living", name="Living/Dining", zone=letter, flat=flat_id,
        number=f"{flat_id}-01", cells=(frame.rect(0, w, 0, living), frame.rect(0, PASSAGE, living, d)))
    spaces = [living_space]
    doors: list[DoorSpec] = []
    windows = [WindowSpec(living_space.id, "living")]
    v = living
    previous = None
    for i, (room, length) in enumerate(zip(seq, lengths)):
        space = Space(id=f"{letter}.r{i + 1}", kind=room.kind, name=room.name, zone=letter, flat=flat_id,
                      number=f"{flat_id}-{i + 2:02d}", cells=(frame.rect(PASSAGE, w, v, v + length),))
        spaces.append(space)
        door_kind = "toilet" if room.kind == "toilet" else "room"
        doors.append(DoorSpec(space.id, previous.id if room.attached else living_space.id, door_kind))
        windows.append(WindowSpec(space.id, "toilet" if room.kind == "toilet" else "room"))
        previous = space
        v += length
    return Flat(letter, utype, variant, w, d, living, tuple(lengths), tuple(spaces), tuple(doors), tuple(windows))


def carpet_m2(flat: Flat) -> float:
    """Sum of net room areas, with the flat's boundary treated as 230 mm walls and partitions as 115 mm."""
    edges = atomic_edges(flat.spaces)
    walls = derive_walls(edges, lambda a, b: "Brick 230" if a is None or b is None else "Brick 115", catalog.WALL_THICKNESS)
    thickness = wall_thickness_on(edges, walls)
    return sum(net_area_mm2(s, edges, thickness) for s in flat.spaces) / 1e6


def size_depth(utype: str, variant: int, target_m2: float, w: int, entry_from: int, d_max: int) -> int:
    """The shortest depth (10 mm steps) whose carpet area reaches the target, but never below the
    flat's minimum. Above d_max the flat cannot reach its target in this slot."""
    living_min, room_min = min_lengths(utype, variant, w, entry_from)
    lo = living_min + sum(room_min)
    if lo > d_max:
        raise FlatError(f"{utype} needs {lo / 1000:.2f} m of depth; at most {d_max / 1000:.2f} m is available")
    if carpet_m2(lay_out("X", "X", utype, variant, w, lo, entry_from)) >= target_m2:
        return lo
    if carpet_m2(lay_out("X", "X", utype, variant, w, d_max, entry_from)) < target_m2:
        raise FlatError(f"{utype} of {target_m2:.0f} m² carpet does not fit: at most "
                        f"{carpet_m2(lay_out('X', 'X', utype, variant, w, d_max, entry_from)):.1f} m² in the depth available")
    hi = d_max
    while hi - lo > catalog.GRID:
        mid = _up((lo + hi) / 2)
        if mid >= hi:
            mid = hi - catalog.GRID
        if carpet_m2(lay_out("X", "X", utype, variant, w, mid, entry_from)) >= target_m2:
            hi = mid
        else:
            lo = mid
    return hi
