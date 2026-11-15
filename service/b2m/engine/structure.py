"""The RCC frame, chajjas and roof parapet: the parts every Kolkata RCC apartment block has around the walls
and rooms the floor plans give.

Frame. Frame lines are the engine's grid lines (outer walls, the two core walls, the walls between front
and rear flats) plus the lift wall. Columns stand at grid intersections on walls, at the lift corners, and
between them where a stretch of wall is longer than MAX_SPAN; every column sits in a wall (never in a room)
with its long side along that wall, flush with the outer faces at the building's edge, and clear of every
door and window on every storey. Beams run along frame lines between columns and line crossings, only where
the whole stretch is wall on a typical floor, so a beam never crosses a room: they sit on top of the walls,
tops flush with the slab. A landing beam carries the slab edge where the stair arrives in the lobby.

The same frame serves every storey: upper floors share one plan, and the ground storey's walls (stilt
parking: only the core) are a subset of its lines.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from string import ascii_uppercase

from b2m.engine import catalog
from b2m.engine.floors import BuiltFloor
from b2m.engine.geometry import Rect
from b2m.engine.templates import PlateLayout

COLUMN, BEAM = "RCC Column 230x450", "RCC Beam 230x450"
COLUMN_B, COLUMN_D = 230, 450  # column width (across its wall) and depth (along it)
BEAM_DEPTH = 450
STAIR, RAILING = "RCC Stair", "Railing 900"
CHAJJA = "Chajja 100"
CHAJJA_SIDE = 150  # past each side of the opening
LINTEL = 150  # the chajja's top is the lintel's top
PARAPET = "Brick 115"
PARAPET_INSET = 60  # parapet centreline inside the roof slab edge: its 115 mm sits just inside the edge
MAX_SPAN = 4500  # longest beam between columns before another column goes in, where openings allow
LONG_SPAN = 6000  # beams longer than this are reported
MIN_BAY = 1000  # shortest beam between two columns
COLUMN_GAP = 300  # clear gap between two columns
JUNCTION_BONUS = 600  # a column may move this much further from its ideal spot to stand where walls meet
PARTITION_CLEAR = 60  # a column not at a junction keeps this clear of walls meeting its line
OPENING_MARGIN = 50  # clear gap between a column and a door or window
EXT_HALF = catalog.WALL_THICKNESS["Brick 230"] // 2


def grid_lines(plate: PlateLayout) -> tuple[list[int], list[int]]:
    """x positions of the numbered grids and y positions of the lettered grids."""
    p, core = plate.plate, plate.core
    xs = sorted({p.x0, core.x0, core.x1, p.x1})
    ys = sorted({p.y0, p.y1} | {p.y0 + d for _, _, row, _, _, d in plate.slots if row == "front"})
    return xs, ys


@dataclass(frozen=True)
class Column:
    node: tuple[int, int]  # the frame point it carries (beams end here)
    point: tuple[int, int]  # where it stands: the node, moved in to keep it inside the building's outer faces
    along: str  # "y": long side along y (rotation 0); "x": along x (rotation 90)

    @property
    def rotation(self) -> int:
        return 0 if self.along == "y" else 90

    @property
    def rect(self) -> Rect:
        hx, hy = (COLUMN_B / 2, COLUMN_D / 2) if self.along == "y" else (COLUMN_D / 2, COLUMN_B / 2)
        x, y = self.point
        return Rect(x - hx, y - hy, x + hx, y + hy)


@dataclass
class Frame:
    columns: list[Column] = field(default_factory=list)
    beams: list[tuple[tuple[int, int], tuple[int, int]]] = field(default_factory=list)
    landing_beam: tuple[tuple[int, int], tuple[int, int]] | None = None
    notes: list[str] = field(default_factory=list)


class _Walls:
    """Wall cover and openings per line, from a typical floor (cover) and every floor (openings). Only walls
    as thick as a column count as cover, so columns and beams never stand out of a 115 mm partition."""

    def __init__(self, typical: BuiltFloor, floors: list[BuiltFloor]):
        self.cover: dict[tuple[str, int], list[tuple[int, int]]] = {}
        self.junctions: dict[tuple[str, int], set[int]] = {}
        for w in typical.walls:
            self.junctions.setdefault((w.axis, w.c), set()).update(w.junctions)
            if w.thickness >= COLUMN_B:
                self.cover.setdefault((w.axis, w.c), []).append((w.a, w.b))
        openings: dict[tuple[str, int], set[tuple[float, float]]] = {}
        for f in floors:
            for o in f.doors + f.windows:
                w = f.walls[o.wall]
                openings.setdefault((w.axis, w.c), set()).add((o.center - o.type.width / 2, o.center + o.type.width / 2))
        self.openings = {line: sorted(spans) for line, spans in openings.items()}

    def covered(self, axis: str, c: int, a: float, b: float) -> bool:
        """Is [a, b] on this line wall all the way along? (a == b: is the point on a wall?)"""
        spans = sorted(self.cover.get((axis, c), []))
        if a == b:
            return any(s <= a <= e for s, e in spans)
        pos = a
        for s, e in spans:
            if s > pos:
                return False
            pos = max(pos, e)
            if pos >= b:
                return True
        return False

    def clear(self, r: Rect) -> bool:
        """No door or window within OPENING_MARGIN of a column rectangle, on any wall through it."""
        for (axis, c), spans in self.openings.items():
            if axis == "x":
                if not (r.x0 < c + EXT_HALF and c - EXT_HALF < r.x1):
                    continue
                lo_r, hi_r = r.y0 - OPENING_MARGIN, r.y1 + OPENING_MARGIN
            else:
                if not (r.y0 < c + EXT_HALF and c - EXT_HALF < r.y1):
                    continue
                lo_r, hi_r = r.x0 - OPENING_MARGIN, r.x1 + OPENING_MARGIN
            if any(lo < hi_r and lo_r < hi for lo, hi in spans):
                return False
        return True


def _inside(p: Rect, node: tuple[int, int], along: str) -> tuple[int, int]:
    """The column's centre moved just enough to keep it inside the outer faces of the outer walls."""
    hx, hy = (COLUMN_B // 2, COLUMN_D // 2) if along == "y" else (COLUMN_D // 2, COLUMN_B // 2)
    x = min(max(node[0], p.x0 - EXT_HALF + hx), p.x1 + EXT_HALF - hx)
    y = min(max(node[1], p.y0 - EXT_HALF + hy), p.y1 + EXT_HALF - hy)
    return (x, y)


def frame(plate: PlateLayout, typical: BuiltFloor, floors: list[BuiltFloor]) -> Frame:
    p, core = plate.plate, plate.core
    walls = _Walls(typical, floors)
    xs, grid_ys = grid_lines(plate)
    lift_ys = [core.lift.y0] if core.lift else []
    result = Frame()
    taken: list[Rect] = []

    def fits(node: tuple[int, int], along: str) -> Column | None:
        column = Column(node, _inside(p, node, along), along)
        r = column.rect
        if not walls.clear(r):
            return None
        if any(r.x0 < t.x1 + COLUMN_GAP and t.x0 < r.x1 + COLUMN_GAP and r.y0 < t.y1 + COLUMN_GAP and t.y0 < r.y1 + COLUMN_GAP
               for t in taken):
            return None
        return column

    def place(column: Column) -> None:
        taken.append(column.rect)
        result.columns.append(column)

    def at_node(node: tuple[int, int]) -> Column | None:
        """A column at a crossing of an x-line wall: along that wall, else (where a y-line wall meets it too)
        along that one."""
        x, y = node
        turns = ("y", "x") if walls.covered("y", y, x, x) else ("y",)
        for along in turns:
            column = fits(node, along)
            if column is not None:
                place(column)
                return column
        return None

    # Grid intersections on walls, then the lift corners on the core walls.
    for x in xs:
        for y in grid_ys:
            if walls.covered("x", x, y, y) and at_node((x, y)) is None:
                result.notes.append(f"No column at grid {xs.index(x) + 1}/{ascii_uppercase[grid_ys.index(y)]}: "
                                    "a door or window is in the way.")
        if x in (core.x0, core.x1):
            for y in lift_ys:
                at_node((x, y))

    def fill(axis: str, c: int, a: int, b: int) -> None:
        """Columns between a and b on a frame line, so no beam on it is longer than MAX_SPAN where doors and
        windows allow. Each next column goes near an even split of what is left, at a wall junction if one is
        close, never more than MAX_SPAN on unless nothing nearer is clear of openings."""
        along = "y" if axis == "x" else "x"
        junctions = walls.junctions.get((axis, c), set())
        pos = a
        while b - pos > MAX_SPAN:
            target = pos + (b - pos) / math.ceil((b - pos) / MAX_SPAN)
            best = None
            for cand in range(pos + MIN_BAY, b - MIN_BAY + 1, catalog.GRID):
                over = cand > pos + MAX_SPAN
                if best is not None and over:
                    break  # Past the span, the first clear spot is the best; nothing later beats what we have.
                if cand not in junctions and any(abs(cand - j) < COLUMN_D / 2 + PARTITION_CLEAR for j in junctions):
                    continue  # A wall meeting this one would end part-way along the column's face.
                score = (int(over), (cand - pos) if over else abs(cand - target) - (JUNCTION_BONUS if cand in junctions else 0), cand)
                if best is not None and score >= best[0]:
                    continue
                column = fits((c, cand) if axis == "x" else (cand, c), along)
                if column is not None:
                    best = (score, column, cand)
            if best is None:
                return
            place(best[1])
            pos = best[2]

    def line_points(y: int) -> list[int]:
        """Frame points on a lettered line: its columns and where numbered lines cross it. The lift wall is
        framed between the core walls only."""
        points = set(nodes_on("y", y)) | set(xs)
        if y in lift_ys and y not in grid_ys:
            points = {x for x in points if core.x0 <= x <= core.x1}
        return sorted(points)

    def nodes_on(axis: str, c: int) -> list[int]:
        return sorted({(col.node[1] if axis == "x" else col.node[0]) for col in result.columns
                       if (col.node[0] if axis == "x" else col.node[1]) == c})

    # Long stretches of wall: along the numbered lines first (they run the full depth), then the lettered ones.
    for x in xs:
        points = nodes_on("x", x)
        for a, b in zip(points, points[1:]):
            if b - a > MAX_SPAN and walls.covered("x", x, a, b):
                fill("x", x, a, b)
    for y in grid_ys + lift_ys:
        points = line_points(y)
        for a, b in zip(points, points[1:]):
            if b - a > MAX_SPAN and walls.covered("y", y, a, b):
                fill("y", y, a, b)

    # Beams: along each line between consecutive frame points, where the stretch is wall.
    for x in xs:
        points = nodes_on("x", x)
        result.beams += [((x, a), (x, b)) for a, b in zip(points, points[1:]) if walls.covered("x", x, a, b)]
    for y in grid_ys + lift_ys:
        points = line_points(y)
        result.beams += [((a, y), (b, y)) for a, b in zip(points, points[1:]) if walls.covered("y", y, a, b)]
    long = [b for b in result.beams if abs(b[1][0] - b[0][0]) + abs(b[1][1] - b[0][1]) > LONG_SPAN]
    if long:
        longest = max(abs(b[1][0] - b[0][0]) + abs(b[1][1] - b[0][1]) for b in long)
        result.notes.append(f"{len(long)} beam span(s) per floor exceed {LONG_SPAN} mm (longest {longest} mm): doors "
                            "and windows leave no place for another column. Check the beam size with the structural engineer.")

    # The slab edge where the stair arrives: a beam whose face is flush with the stair opening.
    y = core.stair.y1 + EXT_HALF
    result.landing_beam = ((core.x0, y), (core.x1, y))
    result.columns.sort(key=lambda col: (col.node[0], col.node[1]))
    result.beams.sort()
    return result




def opening_heads(floors: list[BuiltFloor]) -> int:
    return max((o.type.sill + o.type.height for f in floors for o in f.doors + f.windows), default=0)


@dataclass(frozen=True)
class Chajja:
    points: tuple[tuple[int, int], ...]
    offset: int  # top above the storey's level


def chajjas(floor: BuiltFloor, depth: int) -> list[Chajja]:
    """A sunshade over every door and window in an outside wall: as wide as the opening plus CHAJJA_SIDE each
    side, `depth` out from the wall's outer face, its top at the lintel top. Sunshades that would touch at the
    same height merge into one."""
    if depth <= 0:
        return []
    groups: dict[tuple[str, int, int, int, int], list[tuple[float, float]]] = {}
    for o in floor.doors + floor.windows:
        if o.spaces[1] is not None:
            continue
        w = floor.walls[o.wall]
        side = _outside(floor, w.axis, w.c, o.center)
        half = o.type.width / 2 + CHAJJA_SIDE
        top = o.type.sill + o.type.height + LINTEL
        groups.setdefault((w.axis, w.c, side, w.thickness // 2, top), []).append((o.center - half, o.center + half))
    result = []
    for (axis, c, side, half_wall, top), spans in sorted(groups.items()):
        merged: list[list[float]] = []
        for a, b in sorted(spans):
            if merged and a <= merged[-1][1]:
                merged[-1][1] = max(merged[-1][1], b)
            else:
                merged.append([a, b])
        near, far = c + side * half_wall, c + side * (half_wall + depth)
        for a, b in merged:
            if axis == "x":
                pts = ((near, a), (far, a), (far, b), (near, b))
            else:
                pts = ((a, near), (b, near), (b, far), (a, far))
            result.append(Chajja(tuple((int(x), int(y)) for x, y in pts), top))
    return result


def _outside(floor: BuiltFloor, axis: str, c: int, at: int) -> int:
    """-1 if the outside is on the low (west/south) side of the wall at this point, +1 if on the high side."""
    for e in floor.edges:
        if e.axis == axis and e.c == c and e.a <= at <= e.b and (e.low is None) != (e.high is None):
            return -1 if e.low is None else 1
    raise ValueError(f"no outside edge on {axis}={c} at {at}")


def parapet(p: Rect, height: int) -> list[tuple[tuple[int, int], tuple[int, int]]]:
    """The four parapet walls around the roof slab, just inside its edge, as one closed loop."""
    if height <= 0:
        return []
    i = PARAPET_INSET
    corners = [(p.x0 + i, p.y0 + i), (p.x1 - i, p.y0 + i), (p.x1 - i, p.y1 - i), (p.x0 + i, p.y1 - i)]
    return [(corners[k], corners[(k + 1) % 4]) for k in range(4)]
