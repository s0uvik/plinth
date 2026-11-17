"""Dog-leg stair: the core is sized from the floor height and stair width, then the stair itself is designed
inside the zone the core reserves.

Riser, tread and width limits come from the NBC rule file the compliance checker tests. Each storey has an
even number of risers, the fewest that keeps each riser within the limit, in two equal flights joined by a mid
landing as deep as the stair is wide. The floor landing is in the lobby.

Plan layout inside the stair zone (front of the building at the bottom):

    stair.y1 +------------+-----+------------+   <- lobby edge: flight 1 starts here, flight 2 arrives here
             |  flight 1  |     |  flight 2  |
             |   (down    |well |    (up     |
             |   the page)|     |  the page) |
             +------------+-----+------------+
             |          mid landing          |
    y0 + 115 +-------------------------------+   <- inner face of the front wall
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from plinth.engine.geometry import Rect
from plinth.engine.site import LayoutError
from plinth.rules import rule_set

_STAIR_RULES = {rule["field"]: rule for rule in rule_set("nbc2016")["rules"] if rule["type"] == "stair"}
MAX_RISER = int(_STAIR_RULES["riser_mm"]["max"])  # 190 mm
TREAD = int(_STAIR_RULES["tread_mm"]["min"])  # 250 mm: the design uses the minimum going, which keeps the core short
MIN_WIDTH = int(_STAIR_RULES["width_mm"]["min"])
WELL = 150  # gap between the two flights
WALL_HALF = 115  # the stair zone is enclosed by 230 mm walls on three sides and open to the lobby on the fourth


def _up(value: float, step: int = 10) -> int:
    return int(math.ceil(value / step) * step)


@dataclass(frozen=True)
class StairCore:
    width: int  # clear width of each flight
    risers: int
    riser: float
    risers_per_flight: int
    flight_length: int  # horizontal run of one flight
    inner_width: int  # two flights plus the well
    inner_depth: int  # one flight plus the mid landing; the floor landing is in the lobby


def stair_core(floor_to_floor: int, width: int) -> StairCore:
    risers = math.ceil(floor_to_floor / MAX_RISER)
    if risers % 2:
        risers += 1  # Two equal flights.
    per_flight = risers // 2
    flight = (per_flight - 1) * TREAD
    return StairCore(
        width=width,
        risers=risers,
        riser=floor_to_floor / risers,
        risers_per_flight=per_flight,
        flight_length=flight,
        inner_width=_up(2 * width + WELL),
        inner_depth=_up(flight + width),
    )


@dataclass(frozen=True)
class Flight:
    start: tuple[float, float]  # on the centreline, at the first riser
    end: tuple[float, float]  # on the centreline, at the last riser (the add-in uses it for direction)
    risers: int


@dataclass(frozen=True)
class StairDesign:
    width: int
    tread: int
    risers: int
    riser: float
    flights: tuple[Flight, Flight]
    landing_depth: int  # clear depth left for the mid landing


def design_stair(zone: Rect, floor_to_floor: int, width: int) -> StairDesign:
    """The flights of one storey inside the stair zone (the core's stair rectangle, wall centrelines). Raises
    LayoutError, saying what is needed, when a stair within the NBC limits does not fit."""
    core = stair_core(floor_to_floor, width)
    if width < MIN_WIDTH:
        raise LayoutError(f"Stair width {width} mm is below the {MIN_WIDTH} mm the NBC asks for a residential stair.")
    x0, x1 = zone.x0 + WALL_HALF, zone.x1 - WALL_HALF
    front, back = zone.y0 + WALL_HALF, zone.y1
    clear_w, clear_d = x1 - x0, back - front
    need_w, need_d = 2 * width + WELL, core.flight_length + width
    if clear_w < need_w or clear_d < need_d:
        raise LayoutError(
            f"The stair does not fit its core: two {width} mm flights of {core.risers_per_flight} risers "
            f"({core.riser:.1f} mm riser, {TREAD} mm tread) with a {WELL} mm well and a {width} mm mid landing need "
            f"{need_w} x {need_d} mm clear; the stair zone gives {clear_w} x {clear_d} mm. Use a narrower stair or a "
            "lower floor height.")
    going = core.flight_length
    left, right = x0 + width / 2, x1 - width / 2
    up = Flight((left, back), (left, back - going), core.risers_per_flight)
    down = Flight((right, back - going), (right, back), core.risers_per_flight)
    return StairDesign(width, TREAD, core.risers, core.riser, (up, down), clear_d - going)
