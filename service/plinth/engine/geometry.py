"""Plan geometry: rectangular cells grouped into spaces, and the walls derived from them.

A floor is a set of spaces (living room, bedroom, lobby...), each made of axis-aligned rectangular cells
in integer millimetres. Walls are never drawn by hand: every boundary between two different spaces, or
between a space and the outside, becomes wall, and nothing else does. That makes duplicate and
overlapping walls impossible, which is the main source of Revit warnings. Collinear wall pieces of the
same type merge into one wall; walls are split only where another wall crosses them, so every junction
is an L, a T or an end-to-end join.
"""

from __future__ import annotations

from collections import defaultdict
from dataclasses import dataclass, field
from itertools import pairwise
from typing import Callable, Iterable, NamedTuple


class Rect(NamedTuple):
    x0: int
    y0: int
    x1: int
    y1: int

    @property
    def width(self) -> int:
        return self.x1 - self.x0

    @property
    def height(self) -> int:
        return self.y1 - self.y0

    @property
    def area(self) -> int:
        return self.width * self.height

    @property
    def center(self) -> tuple[int, int]:
        return ((self.x0 + self.x1) // 2, (self.y0 + self.y1) // 2)

    def overlaps(self, other: "Rect") -> bool:
        return self.x0 < other.x1 and other.x0 < self.x1 and self.y0 < other.y1 and other.y0 < self.y1

    @staticmethod
    def of(xa: int, ya: int, xb: int, yb: int) -> "Rect":
        return Rect(min(xa, xb), min(ya, yb), max(xa, xb), max(ya, yb))


@dataclass(frozen=True)
class Space:
    id: str
    kind: str  # living, bedroom_master, bedroom, kitchen, toilet, lobby, lift
    name: str
    zone: str  # flat letter, or "core"
    cells: tuple[Rect, ...]
    room: bool = True  # place a Revit room
    flat: str | None = None
    number: str | None = None
    anchor: int = 0  # cell holding the room point
    door_cells: tuple[int, ...] | None = None  # cells that may host doors; None means all


@dataclass(frozen=True)
class Edge:
    """A piece of boundary on one grid line. axis "x": a vertical line at x = c spanning y in [a, b];
    axis "y": a horizontal line at y = c spanning x in [a, b]. low/high: (space id, cell index) on the
    west/south and east/north sides, None for outside."""

    axis: str
    c: int
    a: int
    b: int
    low: tuple[str, int] | None
    high: tuple[str, int] | None

    @property
    def length(self) -> int:
        return self.b - self.a

    def side_of(self, space_id: str) -> str | None:
        if self.low and self.low[0] == space_id:
            return "low"
        if self.high and self.high[0] == space_id:
            return "high"
        return None

    def other(self, space_id: str) -> tuple[str, int] | None:
        return self.high if self.low and self.low[0] == space_id else self.low


@dataclass
class Wall:
    axis: str
    c: int
    a: int
    b: int
    type: str
    thickness: int
    junctions: list[int] = field(default_factory=list)  # positions along the line where other walls meet it

    @property
    def start(self) -> tuple[int, int]:
        return (self.c, self.a) if self.axis == "x" else (self.a, self.c)

    @property
    def end(self) -> tuple[int, int]:
        return (self.c, self.b) if self.axis == "x" else (self.b, self.c)

    @property
    def length(self) -> int:
        return self.b - self.a


class GeometryError(Exception):
    pass


def atomic_edges(spaces: Iterable[Space]) -> list[Edge]:
    """Split every cell boundary at every breakpoint on its line; each piece knows the cell on each side."""
    lines: dict[tuple[str, int], list[tuple[int, int, str, tuple[str, int]]]] = defaultdict(list)
    for space in spaces:
        for i, r in enumerate(space.cells):
            if r.width <= 0 or r.height <= 0:
                raise GeometryError(f"{space.id}: empty cell {r}")
            ref = (space.id, i)
            lines[("x", r.x0)].append((r.y0, r.y1, "high", ref))
            lines[("x", r.x1)].append((r.y0, r.y1, "low", ref))
            lines[("y", r.y0)].append((r.x0, r.x1, "high", ref))
            lines[("y", r.y1)].append((r.x0, r.x1, "low", ref))

    edges = []
    for (axis, c), items in sorted(lines.items()):
        points = sorted({p for a, b, _, _ in items for p in (a, b)})
        for p, q in pairwise(points):
            low = [ref for a, b, side, ref in items if side == "low" and a <= p and q <= b]
            high = [ref for a, b, side, ref in items if side == "high" and a <= p and q <= b]
            if len(low) > 1 or len(high) > 1:
                raise GeometryError(f"cells overlap along {axis}={c} between {p} and {q}: {low + high}")
            lo, hi = (low[0] if low else None), (high[0] if high else None)
            if lo is None and hi is None:
                continue
            edges.append(Edge(axis, c, p, q, lo, hi))
    return edges


def check_no_overlaps(spaces: list[Space]) -> None:
    cells = [(s.id, r) for s in spaces for r in s.cells]
    for i, (sa, ra) in enumerate(cells):
        for sb, rb in cells[i + 1:]:
            if ra.overlaps(rb):
                raise GeometryError(f"cells of {sa} and {sb} overlap: {ra} / {rb}")


def is_wall(edge: Edge) -> bool:
    lo = edge.low[0] if edge.low else None
    hi = edge.high[0] if edge.high else None
    return lo != hi


Classifier = Callable[[str | None, str | None], str]  # (space id or None, space id or None) -> wall type


def derive_walls(edges: list[Edge], classify: Classifier, thickness: dict[str, int]) -> list[Wall]:
    """Merge wall edges into the longest walls possible, split where another wall crosses."""
    wall_edges = [e for e in edges if is_wall(e)]
    typed = [(e, classify(e.low[0] if e.low else None, e.high[0] if e.high else None)) for e in wall_edges]

    # Which positions on each line are touched by perpendicular walls, from which side.
    by_line: dict[tuple[str, int], list[tuple[Edge, str]]] = defaultdict(list)
    for e, t in typed:
        by_line[(e.axis, e.c)].append((e, t))

    def perpendicular_sides(axis: str, c: int, p: int) -> tuple[bool, bool]:
        """Does a wall on the perpendicular line through p reach the point (c along this line) from below/above?"""
        other = "y" if axis == "x" else "x"
        below = above = False
        for e, _ in by_line.get((other, p), ()):
            if e.a < c <= e.b:
                below = True
            if e.a <= c < e.b:
                above = True
        return below, above

    walls: list[Wall] = []
    for (axis, c), items in sorted(by_line.items()):
        items.sort(key=lambda it: it[0].a)
        run: list[tuple[Edge, str]] = []

        def flush() -> None:
            if run:
                walls.append(Wall(axis, c, run[0][0].a, run[-1][0].b, run[0][1], thickness[run[0][1]]))
                run.clear()

        for e, t in items:
            if run and (run[-1][0].b != e.a or run[-1][1] != t):
                flush()
            elif run:
                below, above = perpendicular_sides(axis, c, e.a)
                if below and above:  # A wall crosses here: split so the junction is end-to-end.
                    flush()
            run.append((e, t))
        flush()

    # Junction positions: where any other wall touches or ends on this wall's line within its span.
    for w in walls:
        other = "y" if w.axis == "x" else "x"
        points = set()
        for v in walls:
            if v.axis != other:
                continue
            if v.a <= w.c <= v.b and w.a <= v.c <= w.b:
                points.add(v.c)
        w.junctions = sorted(points | {w.a, w.b})
    walls.sort(key=lambda w: (w.axis, w.c, w.a))
    return walls


def wall_thickness_on(edges: list[Edge], walls: list[Wall]) -> dict[int, int]:
    """Thickness of the wall covering each wall edge (by edge index)."""
    index = defaultdict(list)
    for w in walls:
        index[(w.axis, w.c)].append(w)
    result = {}
    for i, e in enumerate(edges):
        if not is_wall(e):
            continue
        for w in index[(e.axis, e.c)]:
            if w.a <= e.a and e.b <= w.b:
                result[i] = w.thickness
                break
    return result


def net_area_mm2(space: Space, edges: list[Edge], thickness_of_edge: dict[int, int]) -> int:
    """Cell area minus the half-wall strip along every walled edge of the space."""
    area = sum(r.area for r in space.cells)
    for i, e in enumerate(edges):
        if i in thickness_of_edge and e.side_of(space.id):
            area -= e.length * thickness_of_edge[i] // 2
    return area


def net_rect(space: Space, cell: int, edges: list[Edge], thickness_of_edge: dict[int, int]) -> Rect:
    """A cell shrunk by half the thickest wall on each of its sides (open sides are not shrunk)."""
    r = space.cells[cell]
    ref = (space.id, cell)
    inset = {"w": 0, "e": 0, "s": 0, "n": 0}
    for i, e in enumerate(edges):
        if i not in thickness_of_edge or ref not in (e.low, e.high):
            continue
        half = thickness_of_edge[i] / 2
        if e.axis == "x":
            side = "w" if e.c == r.x0 else "e"
        else:
            side = "s" if e.c == r.y0 else "n"
        inset[side] = max(inset[side], half)
    return Rect(round(r.x0 + inset["w"]), round(r.y0 + inset["s"]), round(r.x1 - inset["e"]), round(r.y1 - inset["n"]))


def merged_spans(edges: Iterable[Edge]) -> list[tuple[str, int, int, int]]:
    """Join contiguous edges on the same line into (axis, c, a, b) spans."""
    spans: list[list] = []
    for e in sorted(edges, key=lambda e: (e.axis, e.c, e.a)):
        if spans and spans[-1][0] == e.axis and spans[-1][1] == e.c and spans[-1][3] == e.a:
            spans[-1][3] = e.b
        else:
            spans.append([e.axis, e.c, e.a, e.b])
    return [tuple(s) for s in spans]
