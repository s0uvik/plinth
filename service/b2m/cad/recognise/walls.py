"""Walls from parallel line pairs (PRD Step 9, stage 3).

1. Loose continuous linework is grouped by direction and merged into face lines (collinear pieces that touch).
2. Infill: a line whose both ends sit on short cross lines (jambs), strictly between their ends, is drawn inside a
   wall (window glazing, sill or sliding-door lines), not a wall face. Infill is set aside for the openings.
3. Pairing: along each direction the faces are swept interval by interval; the spacing of neighbouring faces,
   weighted by the length they run together, gives a histogram whose peaks are the wall thickness classes found
   on the sheet. In every interval the neighbouring faces are then paired (each face at most once) so that the
   paired length, weighted by how common each class is, is largest. A neighbour spacing that is no class is not a
   wall.
4. Pieces of the same pair are joined into walls; collinear walls of the same thickness are joined across a gap
   when one face runs on across it (a junction), a column or a crossing wall fills it, or it is short enough to be
   an opening (door, window or plain), which is then recorded on the wall.
5. Ends are cleaned so centrelines meet: an end that stops at (or inside) another wall, or at an L corner, is moved
   to the other wall's centreline.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

from shapely.geometry import Polygon, box as sbox
from shapely.ops import unary_union

from b2m.cad.recognise.context import Ctx
from b2m.cad.recognise.geometry import (Line1D, Point, Seg, angle_diff, dot, frame, from_frame, group_by_direction,
                                        histogram_peaks, intervals_subtract, intervals_union, merge_collinear,
                                        point_seg_distance, to_frame)


@dataclass
class Group:
    angle: float
    u: Point
    n: Point
    faces: list[Line1D]
    infill: list[Line1D]
    cross: list[Seg]  # short segments across this direction (jambs, caps)


@dataclass
class Piece:
    g: int
    lo: int  # face index (lower offset)
    hi: int
    t0: float
    t1: float


@dataclass
class Gap:
    t0: float
    t1: float
    infill: list[Line1D] = field(default_factory=list)
    jambs: list[Seg] = field(default_factory=list)
    capped: bool = False


@dataclass
class WallLine:
    """A recognised wall in its direction group's frame."""

    id: str
    g: int
    angle: float
    u: Point
    n: Point
    center: float  # offset of the centreline along n
    thickness: float
    t0: float
    t1: float
    faces: set[int] = field(default_factory=set)
    handles: list[str] = field(default_factory=list)
    gaps: list[Gap] = field(default_factory=list)  # openings across the wall (bridged gaps, infill spans)
    kind: str = "unknown"
    labelled: str | None = None  # a text inside the strip: a labelled linear feature, not a wall

    @property
    def start(self) -> Point:
        return from_frame(self.center, self.t0, self.u, self.n)

    @property
    def end(self) -> Point:
        return from_frame(self.center, self.t1, self.u, self.n)

    @property
    def length(self) -> float:
        return self.t1 - self.t0

    def body(self, t0: float | None = None, t1: float | None = None) -> Polygon:
        a = self.t0 if t0 is None else t0
        b = self.t1 if t1 is None else t1
        h = self.thickness / 2
        return Polygon([from_frame(self.center - h, a, self.u, self.n), from_frame(self.center - h, b, self.u, self.n),
                        from_frame(self.center + h, b, self.u, self.n), from_frame(self.center + h, a, self.u, self.n)])

    def local(self, p: Point) -> tuple[float, float]:
        """(t along the axis, signed distance from the centreline)."""
        return dot(p, self.u), dot(p, self.n) - self.center


@dataclass
class WallResult:
    walls: list[WallLine]
    groups: list[Group]
    classes: list[tuple[float, float]]  # (thickness, share of paired length)
    spacing_histogram: list[tuple[float, float]]  # (spacing, paired length) of every neighbour pair in range
    rejected: list[tuple[float, float, list[str]]]  # neighbour spacings in range that are no class: (spacing, len, handles)


def find_walls(ctx: Ctx, column_polys: list[Polygon], exclude: set[str],
               door_spans: list[tuple[Point, Point]] | None = None) -> WallResult:
    p = ctx.params
    tol = p.tol_mm
    segs = [s for s in ctx.segs if s.linetype == "continuous" and not (set(s.handles) & exclude)
            and s.length > 2 * tol]
    groups: list[Group] = []
    dir_groups = group_by_direction(segs, p.ang_tol_deg)
    for ang, members in dir_groups:
        u, n = frame(ang)
        lines = merge_collinear([to_frame(s, u, n) for s in members], tol)
        groups.append(Group(ang, u, n, lines, [], []))
    # cross segments for each group: short segments roughly perpendicular to it
    for g in groups:
        g.cross = [s for s in segs if angle_diff(s.angle, g.angle) >= 90 - 2 and s.length <= p.wall_max_mm + 2 * tol]
    for g in groups:
        keep = []
        for ln in g.faces:
            (g.infill if _is_infill(ln, g, p) else keep).append(ln)
        g.faces = keep

    # pass 1: spacing histogram of neighbouring faces
    hist: list[tuple[float, float]] = []
    sweeps = []
    for gi, g in enumerate(groups):
        intervals = _sweep(g, tol)
        sweeps.append(intervals)
        for ta, tb, active in intervals:
            for i in range(len(active) - 1):
                d = g.faces[active[i + 1]].offset - g.faces[active[i]].offset
                if p.wall_min_mm - tol <= d <= p.wall_max_mm + tol:
                    hist.append((d, tb - ta))
    bin_w = max(5 * tol, 5.0)
    classes = histogram_peaks(hist, bin_w, p.class_min_share)

    def share(d: float) -> float:
        best = 0.0
        for c, s in classes:
            if abs(d - c) <= bin_w:
                best = max(best, s)
        return best

    # pass 2: pairing per interval
    pieces: list[Piece] = []
    rejected: dict[tuple[int, int, int], list] = {}
    for gi, g in enumerate(groups):
        open_pairs: dict[tuple[int, int], Piece] = {}
        for ta, tb, active in sweeps[gi]:
            k = len(active)
            score = [0.0] * (k + 1)
            choice = [None] * (k + 1)
            for j in range(2, k + 1):
                score[j], choice[j] = score[j - 1], None
                d = g.faces[active[j - 1]].offset - g.faces[active[j - 2]].offset
                s = share(d) if p.wall_min_mm - tol <= d <= p.wall_max_mm + tol else 0.0
                if s > 0 and score[j - 2] + s * (tb - ta) > score[j]:
                    score[j], choice[j] = score[j - 2] + s * (tb - ta), (active[j - 2], active[j - 1])
            chosen = set()
            j = k
            while j >= 2:
                if choice[j] is not None:
                    chosen.add(choice[j])
                    j -= 2
                else:
                    j -= 1
            for i in range(k - 1):
                pair = (active[i], active[i + 1])
                d = g.faces[pair[1]].offset - g.faces[pair[0]].offset
                if pair not in chosen and p.wall_min_mm - tol <= d <= p.wall_max_mm + tol and share(d) == 0:
                    r = rejected.setdefault((gi,) + pair, [d, 0.0, g.faces[pair[0]].handles + g.faces[pair[1]].handles])
                    r[1] += tb - ta
            for pair in list(open_pairs):
                if pair not in chosen or abs(open_pairs[pair].t1 - ta) > tol:
                    pieces.append(open_pairs.pop(pair))
            for pair in chosen:
                if pair in open_pairs:
                    open_pairs[pair].t1 = tb
                else:
                    open_pairs[pair] = Piece(gi, pair[0], pair[1], ta, tb)
        pieces.extend(open_pairs.values())

    if column_polys:  # a "wall" that is only a column drawn in outline is the column
        cols_all = unary_union(column_polys).buffer(2 * tol)
        pieces = [pc for pc in pieces if not cols_all.contains(_piece_body(groups[pc.g], pc))]
    walls = _join(ctx, groups, pieces, column_polys, door_spans or [])
    _clean_junctions(ctx, walls)
    return WallResult(walls, groups, classes, hist, [tuple(v) for v in rejected.values()])


def _piece_body(g: Group, pc: Piece) -> Polygon:
    lo, hi = g.faces[pc.lo].offset, g.faces[pc.hi].offset
    return Polygon([from_frame(lo, pc.t0, g.u, g.n), from_frame(lo, pc.t1, g.u, g.n),
                    from_frame(hi, pc.t1, g.u, g.n), from_frame(hi, pc.t0, g.u, g.n)])


def _is_infill(ln: Line1D, g: Group, p) -> bool:
    """Both ends on a cross line, strictly inside it (a jamb), and not longer than an opening."""
    tol = p.tol_mm
    if ln.length > p.opening_max_mm:
        return False
    for t in (ln.t0, ln.t1):
        end = from_frame(ln.offset, t, g.u, g.n)
        ok = False
        for s in g.cross:
            if point_seg_distance(end, s.a, s.b) <= tol and math.dist(end, s.a) > 2 * tol and math.dist(end, s.b) > 2 * tol:
                ok = True
                break
        if not ok:
            return False
    return True


def _sweep(g: Group, tol: float) -> list[tuple[float, float, list[int]]]:
    """Elementary intervals along the group's axis with the faces covering each, ordered by offset."""
    ts = sorted({round(t / tol) * tol for f in g.faces for t in (f.t0, f.t1)})
    order = sorted(range(len(g.faces)), key=lambda i: g.faces[i].offset)
    out = []
    for ta, tb in zip(ts, ts[1:]):
        if tb - ta <= tol:
            continue
        active = [i for i in order if g.faces[i].t0 <= ta + tol and g.faces[i].t1 >= tb - tol]
        out.append((ta, tb, active))
    return out


# ------------------------------------------------------------------ joining pieces into walls


def _join(ctx: Ctx, groups: list[Group], pieces: list[Piece], column_polys: list[Polygon],
          door_spans: list[tuple[Point, Point]]) -> list[WallLine]:
    p = ctx.params
    tol = p.tol_mm
    cols = unary_union(column_polys) if column_polys else None
    drawn = [math.dist(a, b) for a, b in door_spans] + [f.length for g in groups for f in g.infill]
    plain_max = (max(drawn) + 2 * tol) if drawn else p.opening_max_mm
    # first: pieces of the same pair that touch
    lines: list[WallLine] = []
    for pc in sorted(pieces, key=lambda q: (q.g, q.lo, q.hi, q.t0)):
        g = groups[pc.g]
        lo, hi = g.faces[pc.lo], g.faces[pc.hi]
        c = (lo.offset + hi.offset) / 2
        th = hi.offset - lo.offset
        last = lines[-1] if lines else None
        if (last and last.g == pc.g and abs(last.center - c) <= tol and abs(last.thickness - th) <= tol
                and pc.t0 <= last.t1 + tol):
            last.t1 = max(last.t1, pc.t1)
            last.faces |= {pc.lo, pc.hi}
            continue
        lines.append(WallLine("", pc.g, g.angle, g.u, g.n, c, th, pc.t0, pc.t1, {pc.lo, pc.hi}))
    # bodies of all pieces, for crossing-wall checks
    bodies = [(w.g, w.body()) for w in lines]
    merged: list[WallLine] = []
    for gi, g in enumerate(groups):
        mine = sorted([w for w in lines if w.g == gi], key=lambda w: (round(w.center / tol), w.t0))
        # cluster by centre and thickness
        clusters: list[list[WallLine]] = []
        for w in sorted(mine, key=lambda w: (w.center, w.thickness, w.t0)):
            for cl in clusters:
                if abs(cl[0].center - w.center) <= 2 * tol and abs(cl[0].thickness - w.thickness) <= 2 * tol:
                    cl.append(w)
                    break
            else:
                clusters.append([w])
        for cl in clusters:
            cl.sort(key=lambda w: w.t0)
            cur = cl[0]
            for nxt in cl[1:]:
                gap = _bridge(ctx, g, cur, nxt, cols, [b for (bg, b) in bodies if bg != gi], door_spans, plain_max)
                if gap is None:
                    merged.append(cur)
                    cur = nxt
                    continue
                cur.gaps.extend(gap)
                cur.t1 = max(cur.t1, nxt.t1)
                cur.faces |= nxt.faces
                cur.gaps.extend(nxt.gaps)
            merged.append(cur)
    # evidence and ids; infill spans inside a wall become its window-like gaps
    out = []
    for w in sorted(merged, key=lambda w: (-w.length)):
        g = groups[w.g]
        handles = []
        for fi in w.faces:
            handles.extend(g.faces[fi].handles)
        w.handles = list(dict.fromkeys(handles))
        _infill_gaps(ctx, g, w)
        _caps(ctx, g, w)
        out.append(w)
    out.sort(key=lambda w: (round(-w.start[1]), round(w.start[0])))
    for w in out:
        w.id = ctx.new_id("wall")
    return out


def _bridge(ctx: Ctx, g: Group, a: WallLine, b: WallLine, cols, other_bodies: list[Polygon],
            door_spans: list[tuple[Point, Point]], plain_max: float) -> list[Gap] | None:
    """Can a run on into b across the gap between them? Returns the openings in the gap ([] for a junction)."""
    p = ctx.params
    tol = p.tol_mm
    t0, t1 = a.t1, b.t0
    if t1 - t0 <= tol:
        return []
    lo, hi = a.center - a.thickness / 2, a.center + a.thickness / 2
    # a face runs on across the gap: the other face is broken by a junction
    for f in g.faces:
        if (abs(f.offset - lo) <= tol or abs(f.offset - hi) <= tol) and f.t0 <= t0 + tol and f.t1 >= t1 - tol:
            return []
    gap_body = a.body(t0, t1)
    covered = []
    blockers = ([cols] if cols is not None else []) + [b_ for b_ in other_bodies if b_.intersects(gap_body)]
    for blk in blockers:
        inter = blk.intersection(gap_body)
        if inter.is_empty or inter.area <= 0:
            continue
        ts = [dot(c, a.u) for c in _coords(inter)]
        # only counts where it spans the wall's full thickness
        width = inter.area / max(max(ts) - min(ts), 1e-9)
        if width >= a.thickness - 2 * tol:
            covered.append((min(ts), max(ts)))
    rest = intervals_subtract((t0, t1), intervals_union(covered, tol))
    rest = [(x, y) for x, y in rest if y - x > 2 * tol]
    if not rest:
        return []
    if any(y - x > p.opening_max_mm for x, y in rest):
        return None
    gaps = []
    for x, y in rest:
        gap = Gap(x, y)
        gap.infill = [f for f in g.infill if lo - tol <= f.offset <= hi + tol and f.t0 >= x - tol and f.t1 <= y + tol]
        gap.capped = _capped(g, a, x) and _capped(g, a, y)
        door = any(x - tol <= dot(((s[0][0] + s[1][0]) / 2, (s[0][1] + s[1][1]) / 2), a.u) <= y + tol
                   and abs(dot(((s[0][0] + s[1][0]) / 2, (s[0][1] + s[1][1]) / 2), a.n) - a.center)
                   <= a.thickness / 2 + tol for s in door_spans)
        # an opening shows itself: a door in it, lines drawn in it, or both wall ends closed by caps and no wider
        # than the widest door or window drawn on the sheet (a wider capped gap is where a wall simply ends)
        if not (door or gap.infill or (gap.capped and y - x <= plain_max)):
            return None
        gaps.append(gap)
    return gaps


def _coords(geom) -> list[Point]:
    if hasattr(geom, "geoms"):
        out = []
        for gg in geom.geoms:
            out.extend(_coords(gg))
        return out
    if geom.geom_type == "Polygon":
        return list(geom.exterior.coords)
    return list(geom.coords)


def _capped(g: Group, w: WallLine, t: float) -> bool:
    """A cross line closes the wall at t (an end cap or jamb)."""
    lo, hi = w.center - w.thickness / 2, w.center + w.thickness / 2
    for s in g.cross:
        ta, tb = dot(s.a, g.u), dot(s.b, g.u)
        if abs(ta - t) > 1.5 or abs(tb - t) > 1.5:
            continue
        oa, ob = sorted((dot(s.a, g.n), dot(s.b, g.n)))
        if oa <= lo + 1.5 and ob >= hi - 1.5:
            return True
    return False


def _infill_gaps(ctx: Ctx, g: Group, w: WallLine) -> None:
    """Infill lines inside the wall's body between jambs: openings in a wall whose faces run on (windows)."""
    tol = ctx.params.tol_mm
    lo, hi = w.center - w.thickness / 2, w.center + w.thickness / 2
    mine = [f for f in g.infill if lo + tol < f.offset < hi - tol and f.t0 >= w.t0 - tol and f.t1 <= w.t1 + tol]
    for f in mine:
        if any(f in gp.infill for gp in w.gaps):
            continue
        host = None
        for gp in w.gaps:
            if gp.t0 - tol <= f.t0 and f.t1 <= gp.t1 + tol:
                host = gp
                break
            if abs(gp.t0 - f.t0) <= tol and abs(gp.t1 - f.t1) <= tol:
                host = gp
                break
        if host is None:
            host = Gap(f.t0, f.t1, capped=True)
            w.gaps.append(host)
        host.infill.append(f)
    for gp in w.gaps:
        gp.jambs = [s for s in g.cross if _on_cross(s, g, w, gp.t0) or _on_cross(s, g, w, gp.t1)]
    w.gaps.sort(key=lambda gp: gp.t0)


def _on_cross(s: Seg, g: Group, w: WallLine, t: float) -> bool:
    ta, tb = dot(s.a, g.u), dot(s.b, g.u)
    if abs(ta - t) > 1.5 or abs(tb - t) > 1.5:
        return False
    oa, ob = sorted((dot(s.a, g.n), dot(s.b, g.n)))
    return oa >= w.center - w.thickness / 2 - 1.5 and ob <= w.center + w.thickness / 2 + 1.5


def _caps(ctx: Ctx, g: Group, w: WallLine) -> None:
    """End caps (cross lines closing the wall at its ends) are part of the wall's evidence."""
    for s in g.cross:
        if _on_cross(s, g, w, w.t0) or _on_cross(s, g, w, w.t1):
            w.handles.extend(h for h in s.handles if h not in w.handles)


# ------------------------------------------------------------------ junctions


def _clean_junctions(ctx: Ctx, walls: list[WallLine]) -> None:
    """Move wall ends onto the centreline of the wall they stop at (T), meet (L) or run into (overlap)."""
    tol = ctx.params.tol_mm
    for w in walls:
        for end in (0, 1):
            t_end = w.t0 if end == 0 else w.t1
            out_dir = -1.0 if end == 0 else 1.0
            best = None
            for o in walls:
                if o is w or angle_diff(o.angle, w.angle) < 10:
                    continue
                hit = _axis_hit(w, o)
                if hit is None:
                    continue
                t_hit, s_on_o = hit
                ahead = (t_hit - t_end) * out_dir  # how far the other centreline lies beyond this end
                sin = abs(math.sin(math.radians(angle_diff(o.angle, w.angle))))
                reach = o.thickness / 2 / sin + tol * 2
                if not (-reach <= ahead <= reach):
                    continue
                # the meeting point must be on the other wall (or within this wall's half thickness of its end)
                slack = w.thickness / 2 / sin + 2 * tol
                if not (o.t0 - slack <= s_on_o <= o.t1 + slack):
                    continue
                if best is None or abs(ahead) < abs(best[0]):
                    best = (ahead, t_hit)
            if best is not None:
                if end == 0:
                    w.t0 = min(best[1], w.t1 - tol)
                else:
                    w.t1 = max(best[1], w.t0 + tol)


def _axis_hit(w: WallLine, o: WallLine) -> tuple[float, float] | None:
    """Where o's centreline crosses w's: (t along w, t along o)."""
    from b2m.cad.recognise.geometry import line_intersection
    p = line_intersection(w.start, from_frame(w.center, w.t0 + 1000.0, w.u, w.n), o.start,
                          from_frame(o.center, o.t0 + 1000.0, o.u, o.n))
    if p is None:
        return None
    return dot(p, w.u), dot(p, o.u)


def extend_to(w: WallLine, t: float) -> None:
    if t < w.t0:
        w.t0 = t
    elif t > w.t1:
        w.t1 = t
