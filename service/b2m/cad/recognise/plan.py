"""recognise_plan: one floor-plan or roof-plan sheet -> PlanRecognition (PRD Step 9, stage 3).

Order matters only through claims: symbols that are unmistakable by shape go first (section cut lines, door
swings, columns, stair flights) so their linework cannot be mistaken for walls; then walls, openings, the wall
solid and its spaces (outline, rooms, shafts, units), marks, projections, grids, roof falls and site items.
Whatever no recogniser used is listed as unclaimed, with what it looks like.
"""

from __future__ import annotations

import math
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

from shapely.geometry import LineString, Point as SPoint, Polygon
from shapely.ops import unary_union

from b2m.cad.building import Evidence, Grid, PlanRecognition, RoofFall, SectionMark, Stair, Wall
from b2m.cad.classify import Classification
from b2m.cad.recognise import columns as cols_mod
from b2m.cad.recognise import openings as op_mod
from b2m.cad.recognise import spaces as sp_mod
from b2m.cad.recognise import symbols as sym
from b2m.cad.recognise import walls as walls_mod
from b2m.cad.recognise.context import Ctx, Params, build
from b2m.cad.recognise.geometry import (Line1D, Point, Seg, angle_diff, dot, frame, from_frame, group_by_direction,
                                        merge_collinear, point_seg_distance, polygon_area, to_frame)
from b2m.cad.recognise.text import LIFT_RE, MARK_RE, SLOPE_RE, is_name, normalise_mark
from b2m.cad.sheet import Sheet


@dataclass
class Report:
    """What the recogniser found besides the contract: for the CLI and tests."""

    thickness_classes: list[tuple[float, float]] = field(default_factory=list)  # (mm, share of paired length)
    rejected_spacings: list[tuple[float, float]] = field(default_factory=list)  # (mm, length) not a class
    marks: dict = field(default_factory=dict)
    units: int = 0
    unclaimed_by_note: dict[str, int] = field(default_factory=dict)


def recognise_plan(sheet: Sheet, classification: Classification, params: Params | None = None) -> PlanRecognition:
    return recognise_plan_detailed(sheet, classification, params)[0]


def recognise_plan_detailed(sheet: Sheet, classification: Classification,
                            params: Params | None = None) -> tuple[PlanRecognition, Report]:
    role = classification.role if classification.role in ("floor_plan", "roof_plan") else None
    if role is None:
        raise ValueError(f"recognise_plan needs a floor or roof plan, not {classification.role}")
    name = Path(sheet.cad.source).name
    ctx = build(name, sheet.drawing_entities, sheet.cad.layer_linetypes, params)
    p = ctx.params
    tol = p.tol_mm
    report = Report()

    marks = [SectionMark(name=n, points=[(round(x, 2), round(y, 2)) for x, y in pts], evidence=ctx.ev(h))
             for n, pts, h in sym.section_marks(ctx)]
    doors = sym.find_doors(ctx)
    column_shapes = cols_mod.find_columns(ctx)
    stair_drafts = _flights(ctx)

    spans = [s for s in (op_mod.door_span(d, [], tol) for d in doors) if s is not None]
    wres = walls_mod.find_walls(ctx, [c.polygon for c in column_shapes], set(ctx.claims), spans)
    report.thickness_classes = [(round(c, 1), round(s, 3)) for c, s in wres.classes]
    report.rejected_spacings = sorted((round(d, 1), round(l, 0)) for d, l, _ in wres.rejected)
    walls = _drop_labelled(ctx, wres.walls)

    drafts = op_mod.find_openings(ctx, walls, doors, [])
    _boundary(ctx, walls, column_shapes)
    sol = sp_mod.solid(walls, [c.polygon for c in column_shapes], drafts, tol)
    out_poly = sp_mod.outline(sol, ctx, tol) if any(w.kind != "boundary" for w in walls) else None
    exterior_side = _classify_walls(ctx, walls, sol, role)
    stairs, stair_polys = _stairs(ctx, stair_drafts, sol.union)

    free_texts = [t for t in ctx.texts if not ctx.claimed(t.handle)]
    shaft_list, shaft_faces = sp_mod.shafts(ctx, sol, free_texts, [s for s in ctx.segs if not ctx.claimed(s.handles[0])])
    lift_faces = [f for f, s in zip(shaft_faces, shaft_list) if s.kind == "lift"]
    _lift_doors(drafts, lift_faces)

    mark_texts = [t for t in ctx.texts if not ctx.claimed(t.handle) and MARK_RE.match(t.flat) and len(t.flat) <= 5]
    op_mod.assign_marks(ctx, drafts, mark_texts)
    openings = op_mod.to_openings(ctx, drafts, exterior_side)
    report.marks = op_mod.mark_report(ctx, openings, mark_texts)

    labels = sp_mod.room_labels(ctx, [t for t in ctx.texts if not ctx.claimed(t.handle)])
    sp_mod.word_order_check(ctx, labels)
    room_list = sp_mod.rooms(ctx, labels, sol, set())
    public = [h for h in sol.holes if any(h.contains(SPoint(c)) for c in (sp.centroid.coords[0] for sp in stair_polys))]
    for d in drafts:
        if d.note and d.note.startswith("lift landing door") and d.wall is not None:
            for s in (-1, 1):
                off = d.wall.thickness / 2 + 50 * tol
                f = sp_mod.face_of(sol, (d.center[0] + d.wall.n[0] * off * s, d.center[1] + d.wall.n[1] * off * s))
                if f is not None and f not in lift_faces:
                    public.append(f)
    report.units = sp_mod.units(ctx, room_list, sol, drafts, public)

    projections = sp_mod.projections(ctx, out_poly, ctx.segs, drafts, set(), sol)
    grids = _grids(ctx)
    falls = _falls(ctx) if role == "roof_plan" else []
    wall_models = _wall_models(ctx, walls)
    cols_mod.claim_outlines(ctx, column_shapes)
    _thin_pairs(ctx, wres)

    rec = PlanRecognition(
        sheet=name, role=role, storeys=list(classification.storeys), grids=grids, walls=wall_models,
        columns=[c.column for c in column_shapes], openings=openings, rooms=room_list, stairs=stairs,
        shafts=shaft_list, projections=projections,
        outline=[(round(x, 2), round(y, 2)) for x, y in _ccw(list(out_poly.exterior.coords)[:-1])] if out_poly else None,
        section_marks=marks, falls=falls, findings=[], unclaimed=[])
    _site_items(ctx)
    rec.unclaimed = _unclaimed(ctx, report)
    rec.findings = ctx.findings
    return rec, report


def _ccw(ring: list[Point]) -> list[Point]:
    return ring if polygon_area(ring) >= 0 else list(reversed(ring))


# ------------------------------------------------------------------ walls


def _drop_labelled(ctx: Ctx, walls: list[walls_mod.WallLine]) -> list[walls_mod.WallLine]:
    """A line pair with a word written inside it is a named linear feature (drain, road, ...), not a wall."""
    keep = []
    for w in walls:
        body = w.body()
        inside = [t for t in ctx.texts if not ctx.claimed(t.handle) and is_name(t.flat) and body.contains(SPoint(t.center))]
        if inside:
            note = f"line pair labelled \"{inside[0].flat}\" ({w.thickness:.0f} apart)"
            for h in w.handles + [t.handle for t in inside]:
                ctx.notes.setdefault(h, note)
            continue
        keep.append(w)
    return keep


def _thin_pairs(ctx: Ctx, wres: walls_mod.WallResult) -> None:
    """Neighbouring parallel lines at a wall-like spacing that is no thickness class: report the long ones."""
    for d, length, handles in wres.rejected:
        if length < max(2 * d, 500.0) or any(ctx.claimed(h) for h in handles):
            continue
        ctx.finding("wall_pair_unclassified", f"Two parallel lines {d:.0f} mm apart run together for {length:.0f} mm "
                    "but that spacing is none of the wall thicknesses found on this sheet. A thin wall, or "
                    "something else (sill, ledge, frame)?", handles[:6], severity="info")


def _boundary(ctx: Ctx, walls: list[walls_mod.WallLine], column_shapes) -> None:
    """A group of connected walls, joined to nothing else, around everything else: a boundary (compound) wall."""
    if len(walls) < 2:
        return
    tol = ctx.params.tol_mm
    bodies = [w.body().buffer(2 * tol) for w in walls]
    parent = list(range(len(walls)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i in range(len(walls)):
        for j in range(i + 1, len(walls)):
            if bodies[i].intersects(bodies[j]):
                parent[find(i)] = find(j)
    comps: dict[int, list[int]] = {}
    for i in range(len(walls)):
        comps.setdefault(find(i), []).append(i)
    if len(comps) < 2:
        return
    items = [(k, unary_union([bodies[i] for i in v])) for k, v in comps.items()]
    col_pts = [SPoint(c.column.center) for c in column_shapes]
    for k, geom in items:
        hull = geom.convex_hull
        inside = [g for kk, g in items if kk != k and hull.contains(g)]
        inner_cols = [c for c in col_pts if hull.contains(c) and not geom.buffer(tol).contains(c)]
        others_area = sum(g.area for kk, g in items if kk != k)
        if len(inside) + len(inner_cols) >= 2 and len(inside) == len(items) - 1 and hull.area > 4 * others_area:
            for i in comps[k]:
                walls[i].kind = "boundary"


def _classify_walls(ctx: Ctx, walls: list[walls_mod.WallLine], sol: sp_mod.Solid, role: str) -> dict[str, float]:
    """Exterior when one side of the wall is outside every enclosed space (the outside of the solid); on a roof plan
    an exterior wall around open terrace is a parapet. Returns the outside side (+1/-1 along n) per exterior wall."""
    tol = ctx.params.tol_mm
    holes = sol.holes
    out: dict[str, float] = {}
    for w in walls:
        if w.kind == "boundary":
            continue
        sides = []
        for s in (-1.0, 1.0):
            outside = 0
            samples = 0
            for frac in (0.25, 0.5, 0.75):
                t = w.t0 + (w.t1 - w.t0) * frac
                p = from_frame(w.center + s * (w.thickness / 2 + 20 * tol), t, w.u, w.n)
                pt = SPoint(p)
                if sol.union.contains(pt):
                    continue
                samples += 1
                if not any(h.contains(pt) for h in holes):
                    outside += 1
            sides.append((outside, samples))
        ext = [s for s, (o, n) in zip((-1.0, 1.0), sides) if n and o * 2 > n]
        if ext:
            w.kind = "exterior"
            out[w.id] = ext[0]
        else:
            w.kind = "interior"
    if role == "roof_plan":
        _roof_kinds(ctx, walls, sol)
    return out


def _roof_kinds(ctx: Ctx, walls: list[walls_mod.WallLine], sol: sp_mod.Solid) -> None:
    """On a roof plan, walls enclosing an open terrace (its holes are uncovered) are parapets; walls of covered
    rooms on the roof (stair room, lift machine room: spaces you enter by a door) keep exterior/interior."""
    tol = ctx.params.tol_mm
    roofed = []
    for h in sol.holes:
        texts = [t for t in ctx.texts if h.contains(SPoint(t.center)) and is_name(t.flat)]
        names = " ".join(t.flat.upper() for t in texts)
        if "TERRACE" in names or "ROOF" in names.split() or not texts:
            continue
        roofed.append(h)
    for w in walls:
        if w.kind == "boundary":
            continue
        body = w.body().buffer(3 * tol)
        if any(body.intersects(h.exterior) for h in roofed):
            continue
        w.kind = "parapet"


def _wall_models(ctx: Ctx, walls: list[walls_mod.WallLine]) -> list[Wall]:
    out = []
    for w in walls:
        s, e = w.start, w.end
        out.append(Wall(id=w.id, start=(round(s[0], 2), round(s[1], 2)), end=(round(e[0], 2), round(e[1], 2)),
                        thickness_mm=round(w.thickness * 2) / 2, kind=w.kind, evidence=ctx.ev(w.handles)))
        ctx.claim(w.handles, w.id)
    return out


def _lift_doors(drafts: list[op_mod.OpeningDraft], lift_faces: list[Polygon]) -> None:
    for d in drafts:
        if d.kind != "window" or d.wall is None:
            continue
        if any(op_mod._bounds_shaft(d.wall, f) and f.buffer(d.wall.thickness + 2).contains(SPoint(d.center))
               for f in lift_faces):
            d.kind, d.swing = "door", "sliding"
            d.note = "lift landing door (sliding-door lines in a lift shaft wall)"


# ------------------------------------------------------------------ stairs


@dataclass
class _StairDraft:
    flights: list
    u: Point
    n: Point
    box: tuple[float, float, float, float]  # t0, t1 (along the treads), o0, o1 (along the walking line)
    handles: list[str]

    def rect(self) -> Polygon:
        t0, t1, o0, o1 = self.box
        return Polygon([from_frame(o, t, self.u, self.n) for t, o in ((t0, o0), (t1, o0), (t1, o1), (t0, o1))])


def _flights(ctx: Ctx) -> list[_StairDraft]:
    """Stair flights grouped into stairs (flights within one flight width of each other); their tread lines are
    claimed now so no wall can be made of them. The outline is finished once the walls are known."""
    tol = ctx.params.tol_mm
    segs = [s for s in ctx.segs if s.linetype == "continuous" and not ctx.claimed(s.handles[0])]
    flights = sym.find_flights(segs, tol)
    if not flights:
        return []
    polys = [Polygon(f.corners).convex_hull for f in flights]
    parent = list(range(len(flights)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for i in range(len(flights)):
        for j in range(i + 1, len(flights)):
            if polys[i].distance(polys[j]) <= max(flights[i].width, flights[j].width):
                parent[find(i)] = find(j)
    groups: dict[int, list[int]] = {}
    for i in range(len(flights)):
        groups.setdefault(find(i), []).append(i)
    drafts = []
    for idx in groups.values():
        fl = [flights[i] for i in idx]
        u, n = frame(fl[0].angle)
        pts = [q for f in fl for s in f.lines for q in (s.a, s.b)]
        ts = [dot(q, u) for q in pts]
        os_ = [dot(q, n) for q in pts]
        handles = [h for f in fl for h in f.handles]
        ctx.claim(handles, "stair")
        drafts.append(_StairDraft(fl, u, n, (min(ts), max(ts), min(os_), max(os_)), handles))
    return drafts


def _stairs(ctx: Ctx, drafts: list[_StairDraft], solid_geom) -> tuple[list[Stair], list[Polygon]]:
    """Each stair's outline runs on from its flights to the walls around it (landings included, up to one flight
    width away); linework inside it (well, landing edges, walking line) and its up arrow are the stair's."""
    tol = ctx.params.tol_mm
    stairs, outlines = [], []
    for d in drafts:
        fl = d.flights
        width = max(f.width for f in fl)
        tread = max(f.spacing for f in fl)
        t0, t1, o0, o1 = d.box
        if solid_geom is not None and not solid_geom.is_empty:
            o0 -= _reach(solid_geom, d, "o", -1, width, tol)
            o1 += _reach(solid_geom, d, "o", 1, width, tol)
            d.box = (t0, t1, o0, o1)
            t0 -= _reach(solid_geom, d, "t", -1, 2 * tread, tol)
            t1 += _reach(solid_geom, d, "t", 1, 2 * tread, tol)
            d.box = (t0, t1, o0, o1)
        rect = d.rect()
        zone = rect.buffer(tol)
        inner = [s for s in ctx.segs if not ctx.claimed(s.handles[0]) and s.linetype == "continuous"
                 and zone.contains(LineString([s.a, s.b]))]
        fills = [f for f in ctx.fills if not ctx.claimed(f.handle) and zone.contains(SPoint(f.polygon[0]))]
        arrow_list = sym.arrows(ctx, [s for s in ctx.segs if not ctx.claimed(s.handles[0])], fills)
        up = None
        handles = list(d.handles) + [h for s in inner for h in s.handles]
        for a in arrow_list:
            if zone.contains(SPoint(a.tip)):
                up = (round(a.tip[0], 2), round(a.tip[1], 2))
                handles += a.handles
                break
        if len(fl) == 1:
            kind = "straight"
        elif len(fl) == 2:
            kind = "dog_leg" if angle_diff(fl[0].angle, fl[1].angle) < 2 else "l_shaped"
        elif len(fl) == 3:
            kind = "u_shaped"
        else:
            kind = "unknown"
        widths = sorted({round(f.width) for f in fl})
        treads = sorted({round(f.spacing) for f in fl})
        sid = ctx.new_id("stair")
        stair = Stair(id=sid, outline=[(round(x, 2), round(y, 2)) for x, y in _ccw(list(rect.exterior.coords)[:-1])],
                      kind=kind, width_mm=float(widths[0]) if len(widths) == 1 else None,
                      tread_mm=float(treads[0]) if len(treads) == 1 else None,
                      risers_per_flight=[len(f.lines) for f in sorted(fl, key=lambda f: dot(f.center, d.u))],
                      up_arrow=up, evidence=ctx.ev(handles, "risers counted as the tread lines drawn per flight"))
        if len(widths) > 1 or len(treads) > 1:
            ctx.finding("stair_flights_differ", f"Stair {sid}: flights are drawn with different widths {widths} or "
                        f"tread depths {treads} mm. Which is right?", handles[:8])
        if up is None:
            ctx.finding("stair_direction", f"Stair {sid} has no up arrow drawn; which way is up?", d.handles[:4],
                        severity="info")
        ctx.claim(handles, sid)
        stairs.append(stair)
        outlines.append(rect)
    return stairs, outlines


def _reach(solid_geom, d: _StairDraft, axis: str, sign: int, limit: float, tol: float) -> float:
    """How far the stair's box can grow on one side before it meets the wall solid (0 when nothing is within
    limit)."""
    t0, t1, o0, o1 = d.box
    if axis == "o":
        edge = o1 if sign > 0 else o0
        box = (t0 + tol, t1 - tol, min(edge, edge + sign * limit), max(edge, edge + sign * limit))
        coord = d.n
    else:
        edge = t1 if sign > 0 else t0
        box = (min(edge, edge + sign * limit), max(edge, edge + sign * limit), o0 + tol, o1 - tol)
        coord = d.u
    strip = Polygon([from_frame(o, t, d.u, d.n) for t, o in ((box[0], box[2]), (box[1], box[2]),
                                                               (box[1], box[3]), (box[0], box[3]))])
    hit = strip.intersection(solid_geom)
    if hit.is_empty or hit.area <= 0:
        return 0.0
    dists = [(dot(c, coord) - edge) * sign for c in walls_mod._coords(hit)]
    return max(0.0, min(dists))


# ------------------------------------------------------------------ grids, falls, site items


def _grids(ctx: Ctx) -> list[Grid]:
    tol = ctx.params.tol_mm
    segs = [s for s in ctx.segs if s.linetype == "center" and not ctx.claimed(s.handles[0])]
    grids = []
    for ang, members in group_by_direction(segs, ctx.params.ang_tol_deg):
        u, n = frame(ang)
        merged = merge_collinear([to_frame(s, u, n) for s in members], tol, gap=1e12)
        for ln in sorted(merged, key=lambda l: l.offset):
            a, b = from_frame(ln.offset, ln.t0, u, n), from_frame(ln.offset, ln.t1, u, n)
            handles = ln.handles
            name = None
            for c in ctx.circles:
                if ctx.claimed(c.handle):
                    continue
                if min(math.dist(c.center, a), math.dist(c.center, b)) <= c.radius * 1.5 + tol:
                    inside = [t for t in ctx.texts if not ctx.claimed(t.handle) and math.dist(t.center, c.center) <= c.radius]
                    if inside:
                        name = inside[0].flat
                        handles = handles + [c.handle, inside[0].handle]
            for h, q in ctx.points:
                if not ctx.claimed(h) and point_seg_distance(q, a, b) <= tol:
                    handles.append(h)
            gid = ctx.new_id("grid")
            grids.append(Grid(id=gid, name=name, start=(round(a[0], 2), round(a[1], 2)),
                              end=(round(b[0], 2), round(b[1], 2)), evidence=ctx.ev(handles)))
            ctx.claim(handles, gid)
    return grids


def _falls(ctx: Ctx) -> list[RoofFall]:
    """Slope arrows: an arrow with a slope word written along it."""
    segs = [s for s in ctx.segs if not ctx.claimed(s.handles[0])]
    fills = [f for f in ctx.fills if not ctx.claimed(f.handle)]
    labels = [t for t in ctx.texts if not ctx.claimed(t.handle) and SLOPE_RE.search(t.flat)]
    out = []
    used_labels: set[str] = set()
    for a in sym.arrows(ctx, segs, fills):
        length = math.dist(a.tip, a.tail)
        near = sorted(((point_seg_distance(t.center, a.tail, a.tip), t) for t in labels), key=lambda x: x[0])
        if not near or near[0][0] > max(length * 0.6, 3 * near[0][1].height):
            continue
        t = near[0][1]
        fid = ctx.new_id("fall")
        handles = a.handles + ([t.handle] if t.handle not in used_labels else [])
        used_labels.add(t.handle)
        out.append(RoofFall(id=fid, start=(round(a.tail[0], 2), round(a.tail[1], 2)),
                            end=(round(a.tip[0], 2), round(a.tip[1], 2)), label=t.flat, evidence=ctx.ev(handles)))
        ctx.claim(handles, fid)
    for t in labels:
        if t.handle not in used_labels:
            ctx.finding("slope_without_arrow", f"\"{t.flat}\" is written with no arrow beside it. Which way does the "
                        "roof fall here?", [t.handle], severity="info")
    return out


def _site_items(ctx: Ctx) -> None:
    """Name what is left so the unclaimed list says what it is (blocks by name, circles, labelled strips)."""
    for ins in ctx.inserts:
        h = ins.entity.handle
        if not ctx.claimed(h):
            ctx.notes.setdefault(h, f"block \"{ins.name}\"")
    for c in ctx.circles:
        if not ctx.claimed(c.handle):
            ctx.notes.setdefault(c.handle, f"circle r={c.radius:.0f}")


def _unclaimed(ctx: Ctx, report: Report) -> list[Evidence]:
    out = []
    counts: Counter = Counter()
    for e in ctx.entities:
        if e.invisible or e.kind == "dimension":
            continue
        if ctx.claimed(e.handle) or any(ctx.claimed(c.handle) for c in e.walk()):
            continue
        note = ctx.notes.get(e.handle)
        if note is None:
            if e.kind == "text":
                note = f"text \"{' '.join((e.text or '').split())}\""
            elif e.kind in ("line", "polyline"):
                lt = ctx.linetype_class(e)
                note = f"{e.kind}" + (f" ({lt})" if lt != "continuous" else "")
            elif e.kind == "hatch":
                note = "solid fill" if e.solid else f"hatch {e.pattern or ''}".strip()
            else:
                note = e.kind
        out.append(ctx.ev([e.handle], note))
        key = note.split("\"")[0].strip() + (f" \"{note.split(chr(34))[1]}\"" if note.startswith("block") else "")
        counts[key] += 1
    report.unclaimed_by_note = dict(counts.most_common())
    return out
