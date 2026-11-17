"""Splitting a shared space: where one room ends and the next begins when no wall divides them (PRD Step 10).

Decided with the user on 2026-11-17: a kitchen in an open living/dining space is its own room, closed off by a room
separation line, so it gets its own fittings and exhaust. The answer to a ``rooms_share_space`` question says which
labels become rooms of their own; this module finds where their lines go, from the plan's geometry only:

    split = split_space.plan_split(members, ["KITCHEN"], walls)
    split.lines           # [(room, (a, b))]: each separated room with its separation line (sheet coordinates)
    split.together        # the rooms left as one room (their labels share what is left of the space)
    split.unresolved      # [(room, Mouth)]: no clear line; Mouth.candidates are the lines to ask about

The line is the *mouth* of the room's alcove: the shortest chord across the space (the face recognition found, walls,
columns and door leaves around it) that starts at a corner standing into the space (a wall end, a column face: a
reflex corner of the space) and ends at another such corner or square on the opposite face, and that leaves the
room's label on one side and every other label on the other, each at least ``LABEL_CLEARANCE_MM`` from the line (a
line through a label belongs to neither side). A mouth lies first of all between two things standing into the space,
so a chord square onto a plain face counts ``FACE_WEIGHT`` times its length against corner-to-corner ones. Chords
whose ends lie within ``SAME_MOUTH_MM`` of each other close the same mouth (the two corners of one wall end): the
shortest stands for them. The mouth is clear when it is the only one, or clearly the shortest (at most
``CLEAR_RATIO`` of the next); otherwise nothing is chosen and the candidates are asked about. Nothing is guessed.

The line is drawn on into the walls it ends at (``into_walls``), so the wall bodies and the line close the room with
no slit, whether the corner is a wall's or a column's.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from typing import Iterable, Sequence

from shapely.geometry import LineString, Point as SPoint, Polygon
from shapely.geometry.polygon import orient
from shapely.ops import split as split_polygon

from plinth.cad.building import Room, Wall

Point = tuple[float, float]
Line = tuple[Point, Point]

LABEL_CLEARANCE_MM = 450.0  # a label lies at least this far from a separation line (a two-line label's half height)
SAME_MOUTH_MM = 400.0  # chords whose ends are this close close the same mouth (a wall end's two corners)
CLEAR_RATIO = 0.75  # the mouth is clear when it is at most this long against the next distinct one
FACE_WEIGHT = 4 / 3  # a chord square onto a plain face counts this much longer than one from corner to corner
MIN_CHORD_MM = 300.0  # a narrower gap is no mouth
REFLEX_EPS = 1e-6  # |sin| of the turn under which a corner is straight
EDGE_MIN_MM = 2.0  # edges shorter than this are rounding steps, not corners
EXTEND_STEP_MM = 5.0
EXTEND_MAX_MM = 400.0  # how far a line is drawn on to find the wall at its end
INTO_WALL_MM = 25.0  # how far into that wall's body it ends (Revit and the fit-out close slits under 50 mm)
MAX_CANDIDATES = 3


@dataclass
class Mouth:
    line: Line | None = None  # the clear mouth, or None
    candidates: list[Line] = field(default_factory=list)  # distinct valid chords, shortest first
    reason: str = ""  # why none was chosen


@dataclass
class Split:
    lines: list[tuple[Room, list[Point]]] = field(default_factory=list)  # each separated room, its line (polyline)
    together: list[Room] = field(default_factory=list)
    unresolved: list[tuple[Room, Mouth]] = field(default_factory=list)
    problems: list[str] = field(default_factory=list)  # names that match no label or several

    @property
    def ok(self) -> bool:
        return not self.unresolved and not self.problems


# ------------------------------------------------------------------------------------------------ which labels

def _words(text: str) -> set[str]:
    return set(re.findall(r"[a-z0-9]+", text.lower()))


def pick(members: Sequence[Room], wanted: Iterable[str]) -> tuple[list[Room], list[Room], list[str]]:
    """(rooms to separate, in the order asked; the rest; problems). A wanted entry names one label: its room id, or
    words all found in its name ("KITCHEN" names "OPEN KITCHEN" and "KITCHEN OPEN")."""
    out: list[Room] = []
    problems: list[str] = []
    for w in wanted:
        hits = [r for r in members if r.id == w] or [r for r in members if _words(w) and _words(w) <= _words(r.name)]
        if len(hits) != 1:
            names = ", ".join(f'"{r.name}"' for r in members)
            problems.append(f'"{w}" names {"none" if not hits else "more than one"} of the labels {names}')
        elif hits[0] not in out:
            out.append(hits[0])
    rest = [r for r in members if r not in out]
    if out and not rest:
        problems.append("every label would be separated: at least one must stay")
    return out, rest, problems


def split_members(members: Sequence[Room], answer: object) -> tuple[list[Room], list[Room]]:
    """The labels an answer separates and those it keeps as one room (one_room or skip: none separated)."""
    if isinstance(answer, dict) and answer.get("mode") == "separate" and answer.get("rooms"):
        sep, rest, problems = pick(members, answer["rooms"])
        if not problems:
            return sep, rest
    return [], list(members)


# ------------------------------------------------------------------------------------------------ geometry

def _ring(space: Sequence[Point]) -> Polygon:
    poly = Polygon(space)
    if not poly.is_valid:
        poly = poly.buffer(0)
    return orient(poly, 1.0) if poly.geom_type == "Polygon" else poly


def reflex_corners(poly: Polygon) -> list[Point]:
    """Corners standing into the space (interior angle over 180 degrees): wall ends and column faces."""
    pts = [p for p in list(poly.exterior.coords)[:-1]]
    # drop rounding steps: a vertex closer than EDGE_MIN_MM to the previous kept one
    kept: list[Point] = []
    for p in pts:
        if not kept or math.dist(p, kept[-1]) >= EDGE_MIN_MM:
            kept.append(p)
    if len(kept) > 2 and math.dist(kept[0], kept[-1]) < EDGE_MIN_MM:
        kept.pop()
    out = []
    n = len(kept)
    for i, v in enumerate(kept):
        a, b = kept[i - 1], kept[(i + 1) % n]
        u = (v[0] - a[0], v[1] - a[1])
        w = (b[0] - v[0], b[1] - v[1])
        cross = u[0] * w[1] - u[1] * w[0]
        if cross < -REFLEX_EPS * math.hypot(*u) * math.hypot(*w):  # a right turn on an anticlockwise ring
            out.append((float(v[0]), float(v[1])))
    return out


def _feet(p: Point, poly: Polygon) -> list[Point]:
    """Square feet from ``p`` on the space's edges (within each edge)."""
    out = []
    coords = list(poly.exterior.coords)
    for a, b in zip(coords, coords[1:]):
        dx, dy = b[0] - a[0], b[1] - a[1]
        L2 = dx * dx + dy * dy
        if L2 < EDGE_MIN_MM ** 2:
            continue
        t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / L2
        if 0.0 < t < 1.0:
            out.append((a[0] + t * dx, a[1] + t * dy))
    return out


def _inside(poly: Polygon, a: Point, b: Point) -> bool:
    """The chord runs through the open space, touching its boundary only at its ends."""
    L = math.dist(a, b)
    if L < MIN_CHORD_MM:
        return False
    k = 2.0 / L
    inner = LineString([(a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k),
                        (b[0] - (b[0] - a[0]) * k, b[1] - (b[1] - a[1]) * k)])
    return inner.within(poly) and poly.exterior.distance(inner) > 0.5


def _side(a: Point, b: Point, p: Point) -> float:
    """Signed distance of p from the line a -> b (left positive)."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    return (dx * (p[1] - a[1]) - dy * (p[0] - a[0])) / (math.hypot(dx, dy) or 1.0)


def _pieces(poly: Polygon, a: Point, b: Point) -> list[Polygon]:
    L = math.dist(a, b)
    k = 5.0 / L
    cut = LineString([(a[0] - (b[0] - a[0]) * k, a[1] - (b[1] - a[1]) * k),
                      (b[0] + (b[0] - a[0]) * k, b[1] + (b[1] - a[1]) * k)])
    return [g for g in split_polygon(poly, cut).geoms if g.geom_type == "Polygon" and g.area > 1.0]


def _separates(poly: Polygon, a: Point, b: Point, inside: Sequence[Point], outside: Sequence[Point]) -> Polygon | None:
    """The piece left for ``outside`` when the chord parts the inside labels from the outside ones, else None."""
    seg = LineString([a, b])
    if any(seg.distance(SPoint(p)) < LABEL_CLEARANCE_MM for p in (*inside, *outside)):
        return None
    pieces = _pieces(poly, a, b)
    if len(pieces) != 2:
        return None
    where = lambda p: next((i for i, g in enumerate(pieces) if g.contains(SPoint(p))), None)  # noqa: E731
    ins, outs = {where(p) for p in inside}, {where(p) for p in outside}
    if len(ins) != 1 or len(outs) != 1 or None in ins or None in outs or ins == outs:
        return None
    return pieces[outs.pop()]


def _same(c: Line, d: Line) -> bool:
    (a, b), (p, q) = c, d
    return (math.dist(a, p) <= SAME_MOUTH_MM and math.dist(b, q) <= SAME_MOUTH_MM) or \
        (math.dist(a, q) <= SAME_MOUTH_MM and math.dist(b, p) <= SAME_MOUTH_MM)


def find_mouth(space: Sequence[Point] | Polygon, inside: Sequence[Point], outside: Sequence[Point]) -> Mouth:
    """The clear mouth parting ``inside`` labels from ``outside`` ones in the space, or the candidates (see above)."""
    poly = space if isinstance(space, Polygon) else _ring(space)
    if poly.geom_type != "Polygon" or poly.is_empty:
        return Mouth(reason="the shared space has no usable outline")
    poly = orient(poly, 1.0)  # anticlockwise: a right turn is a corner standing into the space
    corners = reflex_corners(poly)
    chords: list[tuple[Line, float]] = []  # (chord, weight)
    for i, c in enumerate(corners):
        for d in corners[i + 1:]:
            chords.append(((c, d), 1.0))
        for f in _feet(c, poly):
            chords.append(((c, f), FACE_WEIGHT))
    valid = []
    for (a, b), weight in chords:
        if _inside(poly, a, b) and _separates(poly, a, b, inside, outside) is not None:
            ln = ((round(a[0], 2), round(a[1], 2)), (round(b[0], 2), round(b[1], 2)))
            valid.append((round(math.dist(a, b) * weight, 3), ln))
    valid.sort()
    distinct: list[tuple[float, Line]] = []
    for score, ln in valid:
        if not any(_same(ln, d) for _, d in distinct):
            distinct.append((score, ln))
    lines = [ln for _, ln in distinct[:MAX_CANDIDATES]]
    if not distinct:
        return Mouth(reason="no wall end or column face gives a line that parts this label from the others")
    if len(distinct) == 1 or distinct[0][0] <= CLEAR_RATIO * distinct[1][0]:
        return Mouth(line=distinct[0][1], candidates=lines)
    return Mouth(candidates=lines,
                 reason=f"{len(distinct)} lines part this label from the others, none clearly the shortest")


def into_walls(line: Line, walls: Iterable[Wall]) -> Line:
    """The line drawn on at each end into the body of the wall it ends at (INTO_WALL_MM past that wall's face), so the
    walls and the line close the room with no slit; an end at no wall within EXTEND_MAX_MM stays where it is."""
    bodies = [LineString([w.start, w.end]).buffer(w.thickness_mm / 2, cap_style="flat")
              for w in walls if w.kind != "boundary" and math.dist(w.start, w.end) > 0]
    a, b = line

    def extend(p: Point, q: Point) -> Point:  # from p away from q
        L = math.dist(p, q) or 1.0
        ux, uy = (p[0] - q[0]) / L, (p[1] - q[1]) / L
        s = 0.0
        while s <= EXTEND_MAX_MM:
            x = SPoint(p[0] + ux * s, p[1] + uy * s)
            hit = next((g for g in bodies if g.contains(x)), None)
            if hit is not None:
                lo, hi = max(0.0, s - EXTEND_STEP_MM), s  # the face lies between: find it to 0.1 mm
                while hi - lo > 0.1:
                    mid = (lo + hi) / 2
                    lo, hi = (lo, mid) if hit.contains(SPoint(p[0] + ux * mid, p[1] + uy * mid)) else (mid, hi)
                t = round(lo, 0) + INTO_WALL_MM
                while t > hi and not hit.contains(SPoint(p[0] + ux * t, p[1] + uy * t)):
                    t -= 1.0  # a wall thinner than INTO_WALL_MM past its face: stay inside it
                return (round(p[0] + ux * t, 2), round(p[1] + uy * t, 2))
            s += EXTEND_STEP_MM
        return p

    return extend(a, b), extend(b, a)


# ------------------------------------------------------------------------------------------------ one group

def plan_split(members: Sequence[Room], wanted: Iterable[str], walls: Iterable[Wall] = (),
               lines: Sequence[Sequence[Point]] | None = None) -> Split:
    """Separate the ``wanted`` labels from the shared space their group's rooms stand in, one after the other (each
    from what is left). ``lines``, when given (the user's), are taken in the same order instead of found."""
    sep, rest, problems = pick(members, wanted)
    out = Split(together=rest, problems=problems)
    if problems:
        return out
    walls = list(walls)
    space = next((r.shared_space for r in members if r.shared_space), None)
    poly = _ring(space) if space else None
    for n, room in enumerate(sep):
        others = [r.point for r in members if r is not room and r not in [s for s, _ in out.lines]]
        if lines is not None:
            if n >= len(lines):
                out.unresolved.append((room, Mouth(reason="no line given for it")))
                continue
            out.lines.append((room, [tuple(q) for q in lines[n]]))
            continue
        if poly is None or poly.geom_type != "Polygon":
            out.unresolved.append((room, Mouth(reason="the recognition kept no outline of the shared space")))
            continue
        mouth = find_mouth(poly, [room.point], others)
        if mouth.line is None:
            out.unresolved.append((room, mouth))
            continue
        left = _separates(poly, *mouth.line, [room.point], others)
        out.lines.append((room, list(into_walls(mouth.line, walls))))
        poly = left
    return out
