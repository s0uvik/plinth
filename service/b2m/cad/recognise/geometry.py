"""Plane geometry for plan recognition: segments, direction groups, collinear merging, snapping, intersections and
small polygon helpers. Coordinates are millimetres. Polygon set operations (union, faces, containment) use shapely.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Iterable, Sequence

Point = tuple[float, float]


@dataclass
class Seg:
    """A straight piece of drawing linework (a LINE, or one edge of a polyline/hatch), with where it came from."""

    a: Point
    b: Point
    handles: list[str] = field(default_factory=list)
    linetype: str = "CONTINUOUS"
    layer: str = "0"
    tag: str = ""  # what claimed it, if anything (set by recognisers)

    @property
    def length(self) -> float:
        return math.dist(self.a, self.b)

    @property
    def dir(self) -> Point:
        n = self.length or 1.0
        return ((self.b[0] - self.a[0]) / n, (self.b[1] - self.a[1]) / n)

    @property
    def angle(self) -> float:
        """Direction in degrees, folded into [0, 180)."""
        return axis_angle(self.a, self.b)

    @property
    def mid(self) -> Point:
        return ((self.a[0] + self.b[0]) / 2, (self.a[1] + self.b[1]) / 2)


def axis_angle(a: Point, b: Point) -> float:
    ang = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0])) % 180.0
    return 0.0 if ang > 180.0 - 1e-9 else ang


def angle_diff(a1: float, a2: float) -> float:
    """Difference of two axis angles (degrees, period 180), in [0, 90]."""
    d = abs(a1 - a2) % 180.0
    return min(d, 180.0 - d)


def unit(angle_deg: float) -> Point:
    r = math.radians(angle_deg)
    return (math.cos(r), math.sin(r))


def frame(angle_deg: float) -> tuple[Point, Point]:
    """(u, n): the unit vector along the axis angle and its left normal."""
    u = unit(angle_deg)
    return u, (-u[1], u[0])


def dot(p: Point, q: Point) -> float:
    return p[0] * q[0] + p[1] * q[1]


def sub(p: Point, q: Point) -> Point:
    return (p[0] - q[0], p[1] - q[1])


def add(p: Point, q: Point) -> Point:
    return (p[0] + q[0], p[1] + q[1])


def mul(p: Point, k: float) -> Point:
    return (p[0] * k, p[1] * k)


def lerp(p: Point, q: Point, t: float) -> Point:
    return (p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t)


def point_seg_distance(p: Point, a: Point, b: Point) -> float:
    ab = sub(b, a)
    L2 = dot(ab, ab)
    if L2 == 0:
        return math.dist(p, a)
    t = max(0.0, min(1.0, dot(sub(p, a), ab) / L2))
    return math.dist(p, (a[0] + ab[0] * t, a[1] + ab[1] * t))


def line_intersection(a1: Point, a2: Point, b1: Point, b2: Point) -> Point | None:
    """Intersection of the infinite lines a1a2 and b1b2; None when parallel."""
    d1, d2 = sub(a2, a1), sub(b2, b1)
    den = d1[0] * d2[1] - d1[1] * d2[0]
    if abs(den) < 1e-12:
        return None
    t = ((b1[0] - a1[0]) * d2[1] - (b1[1] - a1[1]) * d2[0]) / den
    return (a1[0] + d1[0] * t, a1[1] + d1[1] * t)


def seg_intersection(a1: Point, a2: Point, b1: Point, b2: Point, tol: float = 0.0) -> Point | None:
    """Intersection of two segments (ends included, with a tolerance in mm along each); None if they miss."""
    p = line_intersection(a1, a2, b1, b2)
    if p is None:
        return None
    if point_seg_distance(p, a1, a2) <= tol and point_seg_distance(p, b1, b2) <= tol:
        return p
    return None


def polygon_area(points: Sequence[Point]) -> float:
    """Signed area (counter-clockwise positive)."""
    s = 0.0
    n = len(points)
    for i in range(n):
        x1, y1 = points[i]
        x2, y2 = points[(i + 1) % n]
        s += x1 * y2 - x2 * y1
    return s / 2.0


def bbox(points: Iterable[Point]) -> tuple[float, float, float, float]:
    pts = list(points)
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return (min(xs), min(ys), max(xs), max(ys))


def is_rectangle(points: Sequence[Point], tol: float) -> tuple[Point, float, float, float] | None:
    """(centre, width along the first edge, depth, rotation deg in [0, 180)) when the 4 points (closing point
    allowed) form a rectangle within tol; else None."""
    pts = list(points)
    if len(pts) == 5 and math.dist(pts[0], pts[-1]) <= tol:
        pts = pts[:4]
    if len(pts) != 4:
        return None
    d0, d1 = math.dist(pts[0], pts[2]), math.dist(pts[1], pts[3])
    if abs(d0 - d1) > 2 * tol:
        return None
    c = ((pts[0][0] + pts[2][0]) / 2, (pts[0][1] + pts[2][1]) / 2)
    c2 = ((pts[1][0] + pts[3][0]) / 2, (pts[1][1] + pts[3][1]) / 2)
    if math.dist(c, c2) > 2 * tol:
        return None
    w, h = math.dist(pts[0], pts[1]), math.dist(pts[1], pts[2])
    if w <= tol or h <= tol:
        return None
    rot = axis_angle(pts[0], pts[1])
    if rot >= 90.0 - 1e-6:  # report the edge closer to x as the width
        rot -= 90.0
        w, h = h, w
    return c, w, h, rot


def rect_corners(center: Point, width: float, depth: float, rotation_deg: float) -> list[Point]:
    u, n = frame(rotation_deg)
    hw, hd = width / 2, depth / 2
    return [add(add(center, mul(u, sx * hw)), mul(n, sy * hd)) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]


def snap(value: float, tol: float) -> float:
    return round(value / tol) * tol if tol > 0 else value


# ------------------------------------------------------------------ direction groups and collinear merging


def group_by_direction(segs: list[Seg], ang_tol: float) -> list[tuple[float, list[Seg]]]:
    """Segments grouped by axis angle (within ang_tol degrees), each group with its length-weighted mean angle."""
    groups: list[tuple[float, list[Seg]]] = []
    for s in sorted(segs, key=lambda s: -s.length):
        for i, (ang, members) in enumerate(groups):
            if angle_diff(ang, s.angle) <= ang_tol:
                members.append(s)
                break
        else:
            groups.append((s.angle, [s]))
    out = []
    for ang, members in groups:
        ref = ang
        wsum = sum(m.length for m in members)
        # circular mean on the 180-degree period, around the reference
        acc = 0.0
        for m in members:
            d = (m.angle - ref + 90.0) % 180.0 - 90.0
            acc += d * m.length
        out.append(((ref + acc / wsum) % 180.0 if wsum else ref, members))
    return out


@dataclass
class Line1D:
    """A segment expressed in a direction group's frame: offset along the normal, [t0, t1] along the axis."""

    offset: float
    t0: float
    t1: float
    segs: list[Seg]

    @property
    def length(self) -> float:
        return self.t1 - self.t0

    @property
    def handles(self) -> list[str]:
        out: list[str] = []
        for s in self.segs:
            for h in s.handles:
                if h not in out:
                    out.append(h)
        return out


def to_frame(seg: Seg, u: Point, n: Point) -> Line1D:
    ta, tb = dot(seg.a, u), dot(seg.b, u)
    off = (dot(seg.a, n) + dot(seg.b, n)) / 2
    return Line1D(off, min(ta, tb), max(ta, tb), [seg])


def from_frame(offset: float, t: float, u: Point, n: Point) -> Point:
    return (u[0] * t + n[0] * offset, u[1] * t + n[1] * offset)


def merge_collinear(lines: list[Line1D], tol: float, gap: float = 0.0) -> list[Line1D]:
    """Merge lines on the same offset (within tol) that overlap or whose ends are within gap+tol."""
    out: list[Line1D] = []
    # cluster by offset first
    by_off: list[list[Line1D]] = []
    for ln in sorted(lines, key=lambda l: l.offset):
        if by_off and abs(ln.offset - by_off[-1][-1].offset) <= tol:
            by_off[-1].append(ln)
        else:
            by_off.append([ln])
    for cluster in by_off:
        cluster.sort(key=lambda l: l.t0)
        cur: Line1D | None = None
        for ln in cluster:
            if cur is not None and ln.t0 <= cur.t1 + gap + tol:
                w0, w1 = cur.length or 1e-9, ln.length or 1e-9
                cur = Line1D((cur.offset * w0 + ln.offset * w1) / (w0 + w1), cur.t0, max(cur.t1, ln.t1),
                             cur.segs + ln.segs)
            else:
                if cur is not None:
                    out.append(cur)
                cur = Line1D(ln.offset, ln.t0, ln.t1, list(ln.segs))
        if cur is not None:
            out.append(cur)
    return out


def intervals_subtract(base: tuple[float, float], cuts: list[tuple[float, float]]) -> list[tuple[float, float]]:
    out = [base]
    for c0, c1 in cuts:
        nxt = []
        for a, b in out:
            if c1 <= a or c0 >= b:
                nxt.append((a, b))
                continue
            if c0 > a:
                nxt.append((a, c0))
            if c1 < b:
                nxt.append((c1, b))
        out = nxt
    return out


def intervals_union(ivs: list[tuple[float, float]], gap: float = 0.0) -> list[tuple[float, float]]:
    out: list[tuple[float, float]] = []
    for a, b in sorted(ivs):
        if out and a <= out[-1][1] + gap:
            out[-1] = (out[-1][0], max(out[-1][1], b))
        else:
            out.append((a, b))
    return out


def histogram_peaks(values: list[tuple[float, float]], bin_width: float, min_share: float) -> list[tuple[float, float]]:
    """(value, weight) pairs -> peaks [(centre, share)] of the weighted histogram, each a local maximum holding at
    least min_share of the total weight (its bin and both neighbours counted), centre = weighted mean."""
    if not values:
        return []
    total = sum(w for _, w in values) or 1.0
    bins: dict[int, float] = {}
    for v, w in values:
        bins[round(v / bin_width)] = bins.get(round(v / bin_width), 0.0) + w
    peaks = []
    for k, w in bins.items():
        if w < bins.get(k - 1, 0.0) or w < bins.get(k + 1, 0.0):
            continue
        if bins.get(k - 1, 0.0) == w and (k - 1) in bins:  # plateau: keep the left bin only
            continue
        share = (w + bins.get(k - 1, 0.0) + bins.get(k + 1, 0.0)) / total
        if share < min_share:
            continue
        near = [(v, wt) for v, wt in values if abs(round(v / bin_width) - k) <= 1]
        centre = sum(v * wt for v, wt in near) / (sum(wt for _, wt in near) or 1.0)
        peaks.append((centre, share))
    return sorted(peaks)
