"""Spaces: the solid of walls, columns and door leaves; its outer ring (the outline) and its holes (enclosed spaces);
rooms by their name texts, shafts by their labels or crosses, units by doors between rooms, projections outside.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field

from shapely.geometry import LineString, MultiPoint, Point as SPoint, Polygon
from shapely.ops import polygonize, unary_union

from b2m.cad.building import Projection, Room, Shaft
from b2m.cad.recognise.context import Ctx, Text
from b2m.cad.recognise.geometry import Point, Seg, angle_diff, axis_angle, dot, polygon_area
from b2m.cad.recognise.openings import OpeningDraft
from b2m.cad.recognise.text import DUCT_RE, LIFT_RE, is_name, local, parse_size, stacked
from b2m.cad.recognise.walls import WallLine


@dataclass
class Solid:
    union: object  # shapely geometry of walls + columns + door leaves
    polygons: list[Polygon]
    holes: list[Polygon]  # enclosed spaces (holes of the solid)


def solid(walls: list[WallLine], columns: list[Polygon], drafts: list[OpeningDraft], tol: float) -> Solid:
    parts = [w.body().buffer(tol / 2, join_style=2) for w in walls if w.kind != "boundary"]
    parts += [c.buffer(tol / 2, join_style=2) for c in columns]
    thin = min((w.thickness for w in walls), default=50.0)
    for d in drafts:
        if d.wall is None and d.span is not None:  # a door closing a gap between walls closes the space too
            parts.append(LineString(d.span).buffer(thin / 2, cap_style=2))
            jamb_pts = [q for s in (d.door.jambs if d.door else []) for q in (s.a, s.b)]
            if jamb_pts:  # the door's frame marks reach the wall line the door is drawn off
                parts.append(MultiPoint(list(d.span) + jamb_pts).convex_hull.buffer(tol, join_style=2))
    u = unary_union(parts) if parts else Polygon()
    polys = [u] if u.geom_type == "Polygon" else list(getattr(u, "geoms", []))
    polys = [p for p in polys if p.geom_type == "Polygon" and not p.is_empty]
    holes = [Polygon(r) for p in polys for r in p.interiors]
    return Solid(u, polys, [h for h in holes if h.area > 0])


def outline(sol: Solid, ctx: Ctx, tol: float) -> Polygon | None:
    """The largest wall-solid's outer ring, when it clearly dominates (others are free-standing pieces)."""
    if not sol.polygons:
        return None
    polys = sorted(sol.polygons, key=lambda p: -Polygon(p.exterior).area)
    main = Polygon(polys[0].exterior)
    others = [Polygon(p.exterior) for p in polys[1:] if not main.contains(Polygon(p.exterior))]
    big = [p for p in others if p.area > 0.25 * main.area]
    if big:
        ctx.finding("outline_ambiguous", f"The walls form {1 + len(big)} separate blocks of similar size; the plan has "
                    "no single building outline. Is this one building or several?", [], severity="info")
        return None
    return main.simplify(tol)


@dataclass
class RoomLabel:
    name: str
    texts: list[Text]
    size: tuple[float, float] | None = None
    size_text: Text | None = None

    @property
    def point(self) -> Point:
        xs = [t.center[0] for t in self.texts]
        ys = [t.center[1] for t in self.texts]
        return (sum(xs) / len(xs), sum(ys) / len(ys))

    @property
    def handles(self) -> list[str]:
        return [t.handle for t in self.texts] + ([self.size_text.handle] if self.size_text else [])


def room_labels(ctx: Ctx, texts: list[Text]) -> list[RoomLabel]:
    """Name texts (stacked lines joined, read top to bottom) with the size text directly above or below."""
    names = [t for t in texts if is_name(t.flat)]
    sizes = [t for t in texts if parse_size(t.flat)]
    # join stacked name lines
    groups: list[list[Text]] = []
    for t in sorted(names, key=lambda t: -t.center[1]):
        for gr in groups:
            if any(stacked(o, t) is not None for o in gr):
                gr.append(t)
                break
        else:
            groups.append([t])
    labels = []
    for gr in groups:
        ref = gr[0]
        gr.sort(key=lambda t: -local(ref, t.center)[1])
        labels.append(RoomLabel(" ".join(t.flat for t in gr), gr))
    # sizes: nearest label stacked with it
    for s in sizes:
        best = []
        for lb in labels:
            for t in lb.texts:
                y = stacked(t, s)
                if y is not None:
                    best.append((abs(y), lb))
        best.sort(key=lambda b: b[0])
        if not best:
            continue
        if len(best) > 1 and best[1][1] is not best[0][1] and best[1][0] <= best[0][0] * 1.2:
            ctx.finding("room_size_ambiguous", f"Size \"{s.flat}\" sits between \"{best[0][1].name}\" and "
                        f"\"{best[1][1].name}\". Which room is it?", [s.handle] + best[0][1].handles + best[1][1].handles)
            continue
        lb = best[0][1]
        if lb.size_text is None:
            lb.size, lb.size_text = parse_size(s.flat), s
    return labels


def word_order_check(ctx: Ctx, labels: list[RoomLabel]) -> None:
    seen: dict[tuple, list[RoomLabel]] = {}
    for lb in labels:
        if len(lb.texts) > 1:
            seen.setdefault(tuple(sorted(lb.name.split())), []).append(lb)
    for words, group in seen.items():
        names = sorted({lb.name for lb in group})
        if len(names) > 1:
            ctx.finding("room_name_order", "Room names with the same words are written in different orders: "
                        + ", ".join(f"\"{n}\"" for n in names) + " (lines stacked differently, e.g. in mirrored "
                        "flats). Read top to bottom as written; what is the room called?",
                        [h for lb in group for h in lb.handles], severity="info", options=names)


def face_of(sol: Solid, p: Point) -> Polygon | None:
    pt = SPoint(p)
    for h in sol.holes:
        if h.contains(pt):
            return h
    return None


def rooms(ctx: Ctx, labels: list[RoomLabel], sol: Solid, shaft_faces: set[int]) -> list[Room]:
    out = []
    by_face: dict[int, list[RoomLabel]] = {}
    for lb in labels:
        f = face_of(sol, lb.point)
        if f is not None:
            by_face.setdefault(id(f), []).append(lb)
    tol = ctx.params.tol_mm
    for lb in sorted(labels, key=lambda l: (round(-l.point[1]), round(l.point[0]))):
        rid = ctx.new_id("room")
        f = face_of(sol, lb.point)
        boundary = area = None
        if f is not None and len(by_face[id(f)]) == 1:
            ring = list(f.simplify(tol).exterior.coords)[:-1]
            if polygon_area(ring) < 0:
                ring.reverse()
            boundary = [(round(x, 2), round(y, 2)) for x, y in ring]
            area = round(f.area / 1e6, 3)
        elif f is not None:
            others = [o.name for o in by_face[id(f)] if o is not lb]
            if lb is by_face[id(f)][0]:
                ctx.finding("rooms_share_space", "Rooms " + ", ".join(f"\"{o.name}\"" for o in by_face[id(f)])
                            + f" are in one enclosed space ({f.area / 1e6:.2f} m2) with no wall between them. "
                            "Where does one end and the next begin (room separation lines), or are they one room?",
                            [h for o in by_face[id(f)] for h in o.handles], severity="check")
        else:
            ctx.finding("room_not_enclosed", f"\"{lb.name}\" is not enclosed by walls, so its boundary and area "
                        "cannot be measured. Where are its limits?", lb.handles, severity="info")
        room = Room(id=rid, name=lb.name, point=(round(lb.point[0], 2), round(lb.point[1], 2)),
                    labelled_size_mm=lb.size, boundary=boundary, area_m2=area, evidence=ctx.ev(lb.handles))
        if boundary and lb.size:
            _size_check(ctx, room, f)
        ctx.claim(lb.handles, rid)
        out.append(room)
    return out


def _size_check(ctx: Ctx, room: Room, face: Polygon) -> None:
    """Labelled size against the enclosed space's extents along its own main axes."""
    rect = face.minimum_rotated_rectangle
    pts = list(rect.exterior.coords)
    a, b = math.dist(pts[0], pts[1]), math.dist(pts[1], pts[2])
    got = sorted((a, b))
    want = sorted(room.labelled_size_mm)
    if face.area < 0.9 * rect.area:  # not a rectangle: the label is nominal
        return
    diffs = [abs(g - w) for g, w in zip(got, want)]
    if max(diffs) > max(ctx.params.tol_mm * 5, 0.0):
        room_note = f"{got[0]:.0f} x {got[1]:.0f}"
        ctx.finding("room_size_differs", f"{room.name} is labelled {room.labelled_size_mm[0]:g} X "
                    f"{room.labelled_size_mm[1]:g} but its walls enclose {room_note} mm (clear). Which is right "
                    "(the label may be measured to wall centres)?", room.evidence.handles, severity="info",
                    note=f"enclosed {room_note}")


# ------------------------------------------------------------------ shafts


def shafts(ctx: Ctx, sol: Solid, texts: list[Text], segs: list[Seg]) -> tuple[list[Shaft], list[Polygon]]:
    """Enclosed spaces labelled as a lift (or duct), or crossed corner to corner by an X."""
    tol = ctx.params.tol_mm
    out: list[Shaft] = []
    faces: list[Polygon] = []
    for h in sol.holes:
        inside = [t for t in texts if h.contains(SPoint(t.center)) and not ctx.claimed(t.handle)]
        lift = [t for t in inside if LIFT_RE.search(t.flat)]
        duct = [t for t in inside if DUCT_RE.search(t.flat)]
        cross = _cross(h, segs, tol)
        if not (lift or duct or cross):
            continue
        others = [t for t in inside if t not in lift + duct and is_name(t.flat)]
        if (lift or duct) and others:
            continue  # a room that mentions a lift ("LIFT LOBBY", "LIFT ROOM"): named, not a shaft
        if lift and len(lift) > 1:
            ctx.finding("shaft_labels", f"One enclosed space holds {len(lift)} lift labels; is it one shaft or "
                        "several?", [t.handle for t in lift])
        kind = "lift" if lift else "duct"
        label = (lift or duct)[0].flat if (lift or duct) else None
        handles = [t.handle for t in lift + duct] + [hh for s in cross for hh in s.handles]
        ring = list(h.simplify(tol).exterior.coords)[:-1]
        sid = ctx.new_id("shaft")
        out.append(Shaft(id=sid, kind=kind, outline=[(round(x, 2), round(y, 2)) for x, y in ring], label=label,
                         evidence=ctx.ev(handles, "X across the space" if cross and not label else None)))
        ctx.claim(handles, sid)
        faces.append(h)
    return out, faces


def _cross(face: Polygon, segs: list[Seg], tol: float) -> list[Seg]:
    """Two lines from corner to corner of the space's bounding box, crossing."""
    x0, y0, x1, y1 = face.bounds
    corners = [(x0, y0), (x1, y1), (x0, y1), (x1, y0)]
    reach = 3 * tol + 0.02 * math.hypot(x1 - x0, y1 - y0)
    found = []
    for a, b in ((corners[0], corners[1]), (corners[2], corners[3])):
        for s in segs:
            if ((math.dist(s.a, a) <= reach and math.dist(s.b, b) <= reach) or
                    (math.dist(s.a, b) <= reach and math.dist(s.b, a) <= reach)):
                found.append(s)
                break
    return found if len(found) == 2 else []


# ------------------------------------------------------------------ units


def units(ctx: Ctx, room_list: list[Room], sol: Solid, drafts: list[OpeningDraft], public: list[Polygon]) -> int:
    """Rooms joined by doors and plain openings form units; spaces holding a stair or a lift (common areas) are
    left out. Set room.unit only when there are two or more units."""
    faces = sol.holes
    face_idx = {id(f): i for i, f in enumerate(faces)}
    public_idx = {face_idx[id(f)] for f in public if id(f) in face_idx}
    parent = list(range(len(faces)))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for d in drafts:
        if d.kind not in ("door", "opening") or d.wall is None:
            continue
        off = d.wall.thickness / 2 + 50 * ctx.params.tol_mm
        sides = []
        for s in (-1, 1):
            p = (d.center[0] + d.wall.n[0] * off * s, d.center[1] + d.wall.n[1] * off * s)
            f = face_of(sol, p)
            sides.append(face_idx[id(f)] if f is not None else None)
        if None in sides or sides[0] in public_idx or sides[1] in public_idx:
            continue
        parent[find(sides[0])] = find(sides[1])
    comps: dict[int, list[Room]] = {}
    for r in room_list:
        f = face_of(sol, r.point)
        if f is None or face_idx[id(f)] in public_idx:
            continue
        comps.setdefault(find(face_idx[id(f)]), []).append(r)
    groups = [g for g in comps.values() if len(g) >= 2]
    if len(groups) < 2:
        return len(groups)
    groups.sort(key=lambda g: (round(-sum(r.point[1] for r in g) / len(g) / 1000), sum(r.point[0] for r in g)))
    for i, g in enumerate(groups, 1):
        for r in g:
            r.unit = f"U{i}"
    return len(groups)


# ------------------------------------------------------------------ projections


def projections(ctx: Ctx, out_line: Polygon | None, segs: list[Seg], drafts: list[OpeningDraft],
                exclude: set[str], sol: Solid | None = None) -> list[Projection]:
    """Spaces closed by linework against the walls: outside the outline (balconies, sunshades, ledges) and, drawn
    hidden (overhead), inside an enclosed space (a sunshade over a window into a shaft or court)."""
    tol = ctx.params.tol_mm
    cand = [s for s in segs if not ctx.claimed(s.handles[0]) and not (set(s.handles) & exclude)
            and s.linetype in ("continuous", "hidden")]
    hosts = []
    if out_line is not None:
        inner = out_line.buffer(-tol)
        hosts.append((out_line.exterior, [s for s in cand if not inner.contains(LineString([s.a, s.b]))],
                      lambda f: not out_line.buffer(tol).contains(f.representative_point()), False))
    for h in (sol.holes if sol is not None else []):
        zone = h.buffer(10 * tol)  # lines may run a little into the wall they hang from
        hosts.append((h.exterior, [s for s in cand if s.linetype == "hidden" and zone.contains(LineString([s.a, s.b]))],
                      lambda f, h=h: h.contains(f.representative_point()), True))
    out: list[Projection] = []
    for ring, near, accept, hidden_only in hosts:
        near = [s for s in near if not ctx.claimed(s.handles[0])]
        if not near:
            continue
        faces = list(polygonize(unary_union([LineString([s.a, s.b]) for s in near] + [ring])))
        for f in faces:
            if f.area <= 0 or not accept(f):
                continue
            shared = f.exterior.intersection(ring.buffer(2 * tol)).length
            if shared < 4 * tol or shared > 0.5 * f.exterior.length:  # attached along one side, not wrapped
                continue
            mine = [s for s in near if f.exterior.buffer(2 * tol).contains(LineString([s.a, s.b]))]
            if not mine:
                continue
            hidden = sum(s.length for s in mine if s.linetype == "hidden")
            cont = sum(s.length for s in mine if s.linetype == "continuous")
            if hidden_only and hidden <= cont:
                continue
            out.append(_projection(ctx, f, ring, near, mine, drafts, hidden > cont))
    return out


def _projection(ctx: Ctx, f: Polygon, ring, near: list[Seg], mine: list[Seg], drafts: list[OpeningDraft],
                overhead: bool) -> Projection:
    tol = ctx.params.tol_mm
    base = f.exterior.intersection(ring.buffer(2 * tol))
    # openings in a wall along the base (parallel to it, centre half a wall thickness inside it)
    on_base = []
    for d in drafts:
        if d.wall is None:
            continue
        close = base.buffer(d.wall.thickness / 2 + 2 * tol).contains(SPoint(d.center))
        along = any(angle_diff(axis_angle(q0, q1), d.wall.angle) < 3
                    for q0, q1 in _edges(base) if math.dist(q0, q1) > 4 * tol)
        if close and along:
            on_base.append(d)
    doors = [d for d in on_base if d.kind == "door"]
    windows = [d for d in on_base if d.kind == "window"]
    rail = _railing(f, near, tol)
    pid = ctx.new_id("projection")
    handles = [h for s in mine for h in s.handles]
    if overhead:
        kind = "chajja" if windows or not doors else "canopy"
        note = "drawn hidden (above the cut): " + ("sunshade over a window" if windows else "overhead projection")
    elif doors:
        kind, note = "balcony", "a door opens onto it"
    else:
        kind, note = "unknown", None
        ctx.finding("projection_kind", f"A projection outside the walls ({f.area / 1e6:.2f} m2) has no door "
                    "onto it and is drawn in full lines. Is it a balcony, a ledge, a sunshade or a planter?",
                    handles, options=["balcony", "ledge", "chajja", "canopy"])
    ring_pts = list(f.simplify(tol).exterior.coords)[:-1]
    if polygon_area(ring_pts) < 0:
        ring_pts.reverse()
    ctx.claim(handles, pid)
    return Projection(id=pid, kind=kind, outline=[(round(x, 2), round(y, 2)) for x, y in ring_pts],
                      railing=rail, evidence=ctx.ev(handles, note))


def _edges(geom) -> list[tuple[Point, Point]]:
    if hasattr(geom, "geoms"):
        return [e for g in geom.geoms for e in _edges(g)]
    if geom.geom_type == "Polygon":
        geom = geom.exterior
    coords = list(getattr(geom, "coords", []))
    return list(zip(coords, coords[1:]))


def _railing(face: Polygon, segs: list[Seg], tol: float) -> bool | None:
    """True when the free edges are drawn double (a second line just inside, along them); None otherwise."""
    inner = face.buffer(-tol * 2)
    if inner.is_empty:
        return None
    count = 0
    for s in segs:
        ls = LineString([s.a, s.b])
        if inner.contains(ls) and ls.length > 0.3 * math.sqrt(face.area):
            count += 1
    return True if count >= 2 else None
