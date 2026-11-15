"""Door and window placement on derived walls.

An opening goes on a wall the two spaces actually share (or the space and the outside), centred as close
as possible to the middle of the shared stretch, and never within OPENING_CLEARANCE of a wall end, a
wall junction, the end of the shared stretch, or another opening on the same wall (either face). That
keeps openings out of wall joins, which is where Revit raises "cannot cut" and overlap warnings.
"""

from __future__ import annotations

from dataclasses import dataclass

from b2m.engine import catalog
from b2m.engine.catalog import OpeningType
from b2m.engine.geometry import Edge, Space, Wall, is_wall, merged_spans
from b2m.engine.units import DoorSpec, WindowSpec


@dataclass(frozen=True)
class PlacedOpening:
    kind: str  # "door" or "window"
    type: OpeningType
    wall: int  # index into the floor's wall list
    center: int  # position along the wall's line
    offset: int  # distance from the wall's start point to the opening's centre
    spaces: tuple[str, str | None]


def _allowed(lo: int, hi: int, forbidden: list[tuple[float, float]]) -> list[tuple[int, int]]:
    """Closed integer intervals inside [lo, hi] avoiding every open interval in forbidden."""
    intervals = [(lo, hi)] if lo <= hi else []
    for f0, f1 in forbidden:
        nxt = []
        for a, b in intervals:
            if f1 <= a or f0 >= b:
                nxt.append((a, b))
                continue
            if a < f0:
                nxt.append((a, int(f0)))
            if f1 < b:
                nxt.append((int(-(-f1 // 1)), b))
        intervals = nxt
    return [(a, b) for a, b in intervals if a <= b]


def _grid_point(intervals: list[tuple[int, int]], target: float) -> int | None:
    """The grid point inside the intervals nearest the target (ties go to the lower point)."""
    best = None
    g = catalog.GRID
    for a, b in intervals:
        a_g, b_g = -(-a // g) * g, b // g * g
        if a_g > b_g:
            continue
        p = min(max(round(target / g) * g, a_g), b_g)
        if best is None or abs(p - target) < abs(best - target) or (abs(p - target) == abs(best - target) and p < best):
            best = p
    return best


class Placer:
    def __init__(self, spaces: list[Space], edges: list[Edge], walls: list[Wall]):
        self.spaces = {s.id: s for s in spaces}
        self.edges = [e for e in edges if is_wall(e)]
        self.walls = walls
        self.taken: dict[int, list[tuple[int, int]]] = {}
        self.placed: list[PlacedOpening] = []

    def _door_ok(self, ref: tuple[str, int] | None) -> bool:
        if ref is None:
            return True
        allowed = self.spaces[ref[0]].door_cells
        return allowed is None or ref[1] in allowed

    def _spans(self, a: str, b: str | None, door: bool, b_cell: int | None = None) -> list[tuple[str, int, int, int]]:
        chosen = []
        for e in self.edges:
            sides = {e.low[0] if e.low else None, e.high[0] if e.high else None}
            if sides != {a, b}:
                continue
            if door and not (self._door_ok(e.low) and self._door_ok(e.high)):
                continue
            if b_cell is not None and e.other(a) != (b, b_cell):
                continue
            chosen.append(e)
        return merged_spans(chosen)

    def _place_on(self, spans: list[tuple[str, int, int, int]], otype: OpeningType, kind: str,
                  pair: tuple[str, str | None]) -> PlacedOpening | None:
        clear = catalog.OPENING_CLEARANCE
        half = otype.width / 2
        best = None
        # Longest stretch first; on equal length, the earlier one (sorted) wins.
        for axis, c, a, b in sorted(spans, key=lambda s: (-(s[3] - s[2]), s)):
            for index, wall in enumerate(self.walls):
                if wall.axis != axis or wall.c != c or wall.b <= a or wall.a >= b:
                    continue
                lo, hi = max(a, wall.a), min(b, wall.b)
                forbidden = [(j - half - clear, j + half + clear) for j in set(wall.junctions) | {lo, hi}]
                forbidden += [(t0 - half - clear, t1 + half + clear) for t0, t1 in self.taken.get(index, [])]
                point = _grid_point(_allowed(lo, hi, forbidden), (lo + hi) / 2)
                if point is not None:
                    candidate = (abs(point - (lo + hi) / 2), index, point)
                    if best is None or candidate < best:
                        best = candidate
            if best is not None:
                break
        if best is None:
            return None
        _, index, point = best
        wall = self.walls[index]
        self.taken.setdefault(index, []).append((int(point - half), int(point + half)))
        opening = PlacedOpening(kind, otype, index, point, point - wall.a, pair)
        self.placed.append(opening)
        return opening

    def door(self, spec: DoorSpec) -> PlacedOpening | None:
        return self._place_on(self._spans(spec.a, spec.b, door=True, b_cell=spec.b_cell), catalog.DOORS[spec.kind], "door",
                              (spec.a, spec.b))

    def window(self, spec: WindowSpec) -> PlacedOpening | None:
        return self._place_on(self._spans(spec.space, None, door=False), catalog.WINDOWS[spec.kind], "window", (spec.space, None))
