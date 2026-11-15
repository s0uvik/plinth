"""Dog-leg stair core sizing. The stair itself is not modelled (PRD non-goal); the engine sizes the
enclosure and the shaft cut through the upper slabs from the floor height and stair width."""

from __future__ import annotations

import math
from dataclasses import dataclass

MAX_RISER = 190  # mm, design target for residential stairs; compliance checks the real rule
TREAD = 250  # mm
WELL = 150  # gap between the two flights


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
