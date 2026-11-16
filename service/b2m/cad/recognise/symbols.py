"""Drawn symbols found by their geometry: door swings (in blocks or loose), stair flights, arrows and
section cut lines. A block's name is recorded but never needed: a door is a swing arc with a leaf from its centre.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

from b2m.cad.recognise.context import Arc, Ctx, Fill, Insert
from b2m.cad.recognise.geometry import (Point, Seg, angle_diff, axis_angle, dot, frame, group_by_direction,
                                        point_seg_distance, sub)

DOOR_SWEEP = (20.0, 120.0)  # a drawn swing shows the door open between these angles (degrees)


@dataclass
class Swing:
    """One leaf's swing: hinge (arc centre), radius (leaf length) and the arc's two end points."""

    arc: Arc
    leaf: Seg | None
    open_end: Point | None  # the arc end the drawn leaf points at (None when no leaf is drawn)

    @property
    def hinge(self) -> Point:
        return self.arc.center

    @property
    def radius(self) -> float:
        return self.arc.radius

    def closed_end(self) -> Point | None:
        """The arc end that is not where the leaf is: the door's closed position (on the wall line)."""
        if self.open_end is None:
            return None
        a, b = self.arc.p_start, self.arc.p_end
        return b if math.dist(a, self.open_end) < math.dist(b, self.open_end) else a


@dataclass
class DoorSymbol:
    swings: list[Swing]
    handles: list[str]  # evidence (insert handle, or the loose arc and leaf handles)
    block: str | None = None
    insert: Insert | None = None
    jambs: list[Seg] = field(default_factory=list)

    @property
    def leaves(self) -> int:
        return len(self.swings)


def _leaf_for(arc: Arc, segs: list[Seg], tol: float) -> tuple[Seg, Point] | None:
    """A leaf: a straight piece from the arc centre, about one radius long, pointing at one of the arc's ends."""
    best = None
    slack = max(4 * tol, 0.06 * arc.radius)
    ends = (arc.p_start, arc.p_end)
    for s in segs:
        for p, q in ((s.a, s.b), (s.b, s.a)):
            if math.dist(p, arc.center) > slack:
                continue
            for end in ends:
                # the leaf runs from the hinge to the arc end (or alongside it, for a leaf drawn as a thin rectangle)
                if abs(math.dist(q, arc.center) - arc.radius) > slack:
                    continue
                if point_seg_distance(end, p, q) > slack and math.dist(end, q) > slack:
                    continue
                d = math.dist(end, q)
                if best is None or d < best[0]:
                    best = (d, s, end)
    return (best[1], best[2]) if best else None


def door_swings(arcs: list[Arc], segs: list[Seg], tol: float) -> list[Swing]:
    out = []
    for arc in arcs:
        if not (DOOR_SWEEP[0] <= arc.sweep <= DOOR_SWEEP[1]) or arc.radius < 10 * tol:
            continue
        leaf = _leaf_for(arc, segs, tol)
        if leaf is None:
            continue  # an arc alone is not a door (fixtures, curved edges); a swing shows its leaf
        out.append(Swing(arc, leaf[0], leaf[1]))
    return out


def _pair_double(swings: list[Swing], tol: float) -> list[list[Swing]]:
    """Group swings into doors: two swings whose closed ends meet between their hinges are one double door."""
    used: set[int] = set()
    groups: list[list[Swing]] = []
    for i, a in enumerate(swings):
        if i in used:
            continue
        partner = None
        for j in range(i + 1, len(swings)):
            if j in used:
                continue
            b = swings[j]
            span = math.dist(a.hinge, b.hinge)
            if abs(span - (a.radius + b.radius)) > max(4 * tol, 0.03 * span):
                continue
            ca, cb = a.closed_end(), b.closed_end()
            if ca is not None and cb is not None and math.dist(ca, cb) <= max(4 * tol, 0.03 * span):
                partner = j
                break
        if partner is not None:
            used.update({i, partner})
            groups.append([a, swings[partner]])
        else:
            used.add(i)
            groups.append([a])
    return groups


def find_doors(ctx: Ctx) -> list[DoorSymbol]:
    """Doors in block references (any block whose visible content holds a swing) and drawn loose."""
    tol = ctx.params.tol_mm
    doors: list[DoorSymbol] = []
    for ins in ctx.inserts:
        swings = door_swings(ins.arcs, ins.segs, tol)
        if not swings:
            continue
        for group in _pair_double(swings, tol):
            doors.append(DoorSymbol(group, [ins.entity.handle], ins.name, ins, _jambs(group, ins.segs, tol)))
        ctx.claim([ins.entity.handle], "door")
    loose_segs = [s for s in ctx.segs if s.linetype == "continuous"]
    swings = door_swings([a for a in ctx.arcs if not ctx.claimed(a.handle)], loose_segs, tol)
    for group in _pair_double(swings, tol):
        handles = []
        for sw in group:
            handles.append(sw.arc.handle)
            if sw.leaf:
                handles.extend(sw.leaf.handles)
        doors.append(DoorSymbol(group, handles, jambs=_jambs(group, loose_segs, tol)))
        ctx.claim(handles, "door")
    return doors


def _jambs(group: list[Swing], segs: list[Seg], tol: float) -> list[Seg]:
    """Short lines square to the door's span from its hinge or closed end: the frame/jamb marks of the symbol."""
    ends = []
    for sw in group:
        ends.append(sw.hinge)
        if sw.closed_end() is not None:
            ends.append(sw.closed_end())
    span_ang = axis_angle(group[0].hinge, group[0].closed_end()) if group[0].closed_end() else None
    out = []
    for s in segs:
        if s.length > group[0].radius / 2 or span_ang is None or angle_diff(s.angle, span_ang) < 80:
            continue
        if any(min(math.dist(s.a, e), math.dist(s.b, e)) <= 2 * tol for e in ends):
            out.append(s)
    return out


# ------------------------------------------------------------------ stairs


@dataclass
class Flight:
    angle: float  # direction of the tread lines
    lines: list[Seg]  # tread (riser) lines, ordered along the walking direction
    spacing: float  # tread depth
    width: float  # tread line length

    @property
    def corners(self) -> list[Point]:
        first, last = self.lines[0], self.lines[-1]
        return [first.a, first.b, last.b, last.a] if dot(sub(first.b, first.a), sub(last.b, last.a)) > 0 else \
            [first.a, first.b, last.a, last.b]

    @property
    def handles(self) -> list[str]:
        return [h for s in self.lines for h in s.handles]

    @property
    def center(self) -> Point:
        xs = [p[0] for s in self.lines for p in (s.a, s.b)]
        ys = [p[1] for s in self.lines for p in (s.a, s.b)]
        return (sum(xs) / len(xs), sum(ys) / len(ys))


def find_flights(segs: list[Seg], tol: float, min_lines: int = 4) -> list[Flight]:
    """Runs of at least min_lines parallel lines of equal length, ends aligned, evenly spaced: stair treads.
    Walls never draw like that (a wall is two lines; a window three or four lines inside one wall)."""
    flights: list[Flight] = []
    for ang, members in group_by_direction(segs, 0.5):
        u, n = frame(ang)
        rows = []
        for s in members:
            ta, tb = sorted((dot(s.a, u), dot(s.b, u)))
            rows.append((round((dot(s.a, n) + dot(s.b, n)) / 2, 3), ta, tb, s))
        # bucket by (start, end) along the line within tol
        rows.sort(key=lambda r: (round(r[1] / (2 * tol)), round(r[2] / (2 * tol)), r[0]))
        buckets: list[list] = []
        for r in rows:
            for b in buckets:
                if abs(b[0][1] - r[1]) <= 2 * tol and abs(b[0][2] - r[2]) <= 2 * tol:
                    b.append(r)
                    break
            else:
                buckets.append([r])
        for b in buckets:
            if len(b) < min_lines:
                continue
            b.sort(key=lambda r: r[0])
            # split into evenly spaced runs
            run = [b[0]]
            for r in b[1:]:
                gap = r[0] - run[-1][0]
                if gap <= tol:
                    continue  # duplicate line
                if len(run) >= 2:
                    step = run[1][0] - run[0][0]
                    if abs(gap - step) > max(2 * tol, 0.02 * step):
                        _close_run(run, ang, flights, min_lines, tol)
                        run = [run[-1], r] if gap < 3 * step else [r]
                        continue
                run.append(r)
            _close_run(run, ang, flights, min_lines, tol)
    return flights


def _close_run(run: list, ang: float, flights: list[Flight], min_lines: int, tol: float) -> None:
    if len(run) < min_lines:
        return
    width = run[0][2] - run[0][1]
    step = (run[-1][0] - run[0][0]) / (len(run) - 1)
    if step <= 10 * tol or step > width * 1.5:  # treads are shallower than the flight is wide
        return
    flights.append(Flight(ang, [r[3] for r in run], step, width))


# ------------------------------------------------------------------ arrows and section cut lines


@dataclass
class Arrow:
    tip: Point
    tail: Point
    fill: Fill | None
    handles: list[str]


def triangle_tip(fill: Fill) -> Point | None:
    """The sharpest corner of a 3-corner fill (an arrowhead), None for other shapes."""
    pts = list(fill.polygon)
    if len(pts) == 4 and math.dist(pts[0], pts[3]) < 1e-6:
        pts = pts[:3]
    if len(pts) == 4:  # SOLID with a repeated corner
        uniq = []
        for p in pts:
            if all(math.dist(p, q) > 1e-6 for q in uniq):
                uniq.append(p)
        pts = uniq
    if len(pts) != 3:
        return None
    best, best_angle = None, 999.0
    for i in range(3):
        p, a, b = pts[i], pts[(i + 1) % 3], pts[(i + 2) % 3]
        va, vb = sub(a, p), sub(b, p)
        la, lb = math.hypot(*va), math.hypot(*vb)
        if la == 0 or lb == 0:
            return None
        ang = math.degrees(math.acos(max(-1.0, min(1.0, dot(va, vb) / (la * lb)))))
        if ang < best_angle:
            best, best_angle = p, ang
    return best if best_angle < 50 else None


def triangle_base_mid(fill: Fill, tip: Point) -> Point:
    others = [p for p in fill.polygon if math.dist(p, tip) > 1e-6]
    xs = [p[0] for p in others]
    ys = [p[1] for p in others]
    return (sum(xs) / len(xs), sum(ys) / len(ys))


def arrows(ctx: Ctx, segs: list[Seg], fills: list[Fill]) -> list[Arrow]:
    """A shaft (line or polyline chain) with a triangular head at one end."""
    tol = ctx.params.tol_mm
    out = []
    used: set[int] = set()
    for f in fills:
        tip = triangle_tip(f)
        if tip is None:
            continue
        base = triangle_base_mid(f, tip)
        head_len = math.dist(tip, base)
        best = None
        for i, s in enumerate(segs):
            if i in used:
                continue
            for p, q in ((s.a, s.b), (s.b, s.a)):
                # the shaft ends at the tip or at the base of the head and runs away from the tip
                if min(math.dist(p, tip), math.dist(p, base)) > max(head_len * 0.6, 4 * tol):
                    continue
                if angle_diff(axis_angle(p, q), axis_angle(base, tip)) > 10:
                    continue
                if math.dist(q, tip) < math.dist(p, tip):
                    continue
                if best is None or s.length > best[1].length:
                    best = (i, s, q)
        if best:
            used.add(best[0])
            out.append(Arrow(tip, best[2], f, [f.handle] + best[1].handles))
    return out
