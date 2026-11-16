"""Where MEP equipment stands when no room of the plan holds it (PRD Step 10, F4): on a wall near the stair or near a
point, outside the building on one of its sides, on the roof of the stair/lift room, and the vertical cores a rising
main can go up.

    from b2m.fitout import siting
    loc = siting.location(given)                       # a location answer, or None (it is a room key or nothing)
    spots = siting.wall_spots_at(ctx, loc, level, width, depth, bottom, top, n=1, min_gap=0)
    tank = siting.outside_box(ctx, side, near, width, depth)       # a box in the setback on that side, or a reason
    roof = siting.stair_roof(ctx)                      # the roof over the stair/lift rooms, or None

Why: the G+4 sample's ground floor is stilt parking: no meter room, no lobby, no site. The user places the meter
board "under the stair" and the underground tank "in the front setback, near the gate", so a location is answered
without a room. Each shape is answered by the user, never guessed:

- **On a wall** (``fitout:meter_room``, ``fitout:common_board``, ``fitout:extinguisher_room:<level>``):
  ``{"near": "stair"}`` (optionally ``"level"``: the storey; ``"under": false`` to not prefer the space under the
  flights) puts the device on the wall face nearest the foot of the stair that starts on that storey (the first
  riser of its lowest flight), clear of the stair's run and headroom: its box (width along the wall, depth off it,
  bottom to top) may stand under a flight or landing (below the flight's underside) or beside the stair, never in
  the walking zone above the treads (``STAIR_HEADROOM_MM``) or in the clear floor at either end of the stair. Spots
  under the stair come first, then the nearest to the stair's foot, within ``NEAR_STAIR_MAX_MM``.
  ``{"point": [x, y], "level": "<name>"}`` puts it on the wall face nearest that plan point (within
  ``NEAR_POINT_MAX_MM``), with the same clearances. The walls are the plan's wall ops on that storey that reach the
  device's top; a face is used clear of its wall's doors and windows (at the device's heights), of other walls and
  columns, of devices already on it, and outside the flats' rooms (a common room's face is the room's). No wall:
  the caller asks.
- **Outside, on a side** (``fitout:underground_tank``): ``{"side": "front" | "north" | "south" | "east" | "west",
  "near": "entrance" | "gate" | [x, y]}``. "front" is the answer to ``fitout:front_side`` (asked when not given:
  which face of the building is its front is the user's, not guessed). Plan +Y is taken as north (an assumption:
  the plan gives no north). The box stands beyond the building's face on that side (its slabs' outline), at least
  ``FOOTING_CLEAR_MM`` from the foundation slabs and footings (the building's outline when the plan has none),
  ``tanks.clear_mm`` from the building, inside the site (the boundary walls, when the plan has them, less
  ``BOUNDARY_CLEAR_MM``) and clear of the entrance path: the strip from the entrance to the building, as wide as the
  gate (``PATH_MARGIN_MM`` more each side). The entrance on that side is the gap in the boundary wall (the gate),
  else a door of the lowest storey opening that way, else the foot of the stair projected onto the face, else the
  face's middle. The box nearest the entrance wins.
- **On the stair/lift roof** (``fitout:overhead_tank_on`` = ``"stair_roof"``): the roof of the rooms on the terrace
  level that hold the stair, the mumty, the lift or its machine room; it is the level their walls reach (their
  height above the terrace, or their walls' top level). Its outline is those rooms with their walls.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass
from typing import Any

from shapely.geometry import LineString, Point as SPoint, Polygon, box
from shapely.ops import unary_union

from b2m.fitout.mepcore import CORNER_MM, OPENING_MARGIN_MM, Ctx, DEFAULT_DOOR_HEAD_MM, Host, Point, add, dot, \
    pt, r1, sub, unit
from b2m.fitout.rooms import Room, type_size, type_thickness
from b2m.plan import ColumnOp, DoorOp, FloorOp, FoundationSlabOp, OpeningTypeOp, ShaftOp, StairOp, WallOp, \
    WallTypeOp, WindowOp

STAIR_HEADROOM_MM = 2200.0  # NBC 2016 Part 3: clear headroom on a stair, as recalled
STAIR_WAIST_MM = 200.0  # a flight's or landing's slab under its walking surface
UNDER_CLEAR_MM = 100.0  # a device under a flight keeps this far below its underside
STAIR_APPROACH_MM = 1000.0  # clear floor in front of a stair's first riser and beyond its last
NEAR_STAIR_MAX_MM = 10000.0
NEAR_POINT_MAX_MM = 5000.0
FOOTING_CLEAR_MM = 1000.0  # an underground tank's pit keeps this far from the foundations
BOUNDARY_CLEAR_MM = 300.0
PATH_MARGIN_MM = 500.0
MIN_GATE_MM = 900.0
COMMON_WALL_ROOMS = ("lobby", "stair", "parking", "meter_room", "pump_room", "guard", "mumty")
STAIR_ROOF_CLASSES = ("stair", "mumty", "lift", "lift_machine_room")
SIDES = {"north": (0.0, 1.0), "south": (0.0, -1.0), "east": (1.0, 0.0), "west": (-1.0, 0.0)}


# ------------------------------------------------------------------------------------------------ answers

@dataclass(frozen=True)
class Location:
    """A location answer: near the stair (``near`` = "stair") or near a point; ``level`` None: the caller's."""

    near: str | None = None
    point: Point | None = None
    level: str | None = None
    under: bool = True

    def describe(self) -> str:
        if self.near:
            return f"near the {self.near}" + (" (under it where it fits)" if self.under else "")
        return f"near ({self.point[0]:.0f}, {self.point[1]:.0f})"


def location(given: Any) -> Location | None:
    """``{"near": "stair"}`` or ``{"point": [x, y]}`` (each with an optional ``"level"``) as a Location; None for any
    other answer (a room key, nothing)."""
    if not isinstance(given, dict):
        return None
    level = given.get("level") if isinstance(given.get("level"), str) else None
    if given.get("near") == "stair":
        return Location(near="stair", level=level, under=given.get("under", True) is not False)
    p = given.get("point")
    if isinstance(p, (list, tuple)) and len(p) == 2 and all(isinstance(v, (int, float)) for v in p):
        return Location(point=(float(p[0]), float(p[1])), level=level)
    return None


LOCATION_SHAPES = ('{"near": "stair"} (on the wall nearest the foot of the stair, under it where it fits, clear of '
                   'its run and headroom) or {"point": [x, y], "level": "<level>"} (on the wall nearest that point)')


# ------------------------------------------------------------------------------------------------ the stair

@dataclass
class Zone:
    """Part of a stair people walk on: a flight (surface rising along ``u`` from ``z0`` to ``z1``), a landing (flat
    at ``z0``) or the clear floor at an end (flat, nothing under it)."""

    poly: Polygon
    kind: str  # flight, landing, approach
    z0: float
    z1: float
    start: Point = (0.0, 0.0)
    u: Point = (1.0, 0.0)
    rise: float = 0.0
    tread: float = 1.0

    def surface(self, p: Point) -> float:
        if self.kind != "flight":
            return self.z0
        t = max(0.0, dot(sub(p, self.start), self.u))
        return min(self.z1, self.z0 + self.rise * (1 + t / self.tread))


@dataclass
class Stair:
    op: StairOp
    zones: list[Zone]
    foot: Point  # the first riser of its lowest flight
    head: Point  # the last riser of its top flight

    @property
    def footprint(self):
        return unary_union([z.poly for z in self.zones])


def _rect(a: Point, u: Point, length: float, width: float) -> Polygon:
    n = (-u[1], u[0])
    h = width / 2
    return Polygon([add(a, n, h), add(a, n, -h), add(add(a, u, length), n, -h), add(add(a, u, length), n, h)])


def stair_geometry(ctx: Ctx, op: StairOp) -> Stair:
    z_base = ctx.elev(op.base_level) + op.base_offset_mm
    z_top = ctx.elev(op.top_level) if op.top_level in ctx.levels else z_base + 3000
    total = sum(r.risers for r in op.runs)
    rise = (z_top - z_base) / total if total else 0.0
    W = op.width_mm
    zones: list[Zone] = []
    z = z_base
    runs = []
    for r in op.runs:
        u = unit(sub(r.end, r.start))
        going = (r.risers - 1) * op.tread_mm
        z1 = z + r.risers * rise
        zones.append(Zone(_rect(r.start, u, going, W), "flight", z, z1, r.start, u, rise, op.tread_mm))
        runs.append((r.start, u, going))
        z = z1
    for (s0, u0, g0), (s1, u1, _), zone in zip(runs, runs[1:], zones):
        e0 = add(s0, u0, g0)
        n0, n1 = (-u0[1], u0[0]), (-u1[1], u1[0])
        pts = [add(e0, n0, W / 2), add(e0, n0, -W / 2), add(add(e0, u0, W), n0, W / 2), add(add(e0, u0, W), n0, -W / 2),
               add(s1, n1, W / 2), add(s1, n1, -W / 2), add(add(s1, u1, -W), n1, W / 2),
               add(add(s1, u1, -W), n1, -W / 2)]
        zones.append(Zone(unary_union([SPoint(p) for p in pts]).convex_hull, "landing", zone.z1, zone.z1))
    s0, u0, _ = runs[0]
    sl, ul, gl = runs[-1]
    zones.append(Zone(_rect(add(s0, u0, -STAIR_APPROACH_MM), u0, STAIR_APPROACH_MM, W), "approach", z_base, z_base))
    end = add(sl, ul, gl)
    zones.append(Zone(_rect(end, ul, STAIR_APPROACH_MM, W), "approach", z_top, z_top))
    return Stair(op, zones, pt(s0), pt(end))


def stairs_of(ctx: Ctx) -> list[Stair]:
    if not hasattr(ctx, "_stairs"):
        ctx._stairs = [stair_geometry(ctx, op) for op in ctx.stairs]
    return ctx._stairs


def stair_clear(ctx: Ctx, foot: Polygon, zb: float, zt: float) -> tuple[bool, bool]:
    """(clear, under): whether a box over ``foot`` from ``zb`` to ``zt`` (absolute) keeps out of every stair's walking
    zone, and whether it stands under a flight or landing."""
    under = False
    for st in stairs_of(ctx):
        for z in st.zones:
            part = z.poly.intersection(foot)
            if part.is_empty or part.area < 1:
                continue
            geoms = list(part.geoms) if hasattr(part, "geoms") else [part]
            ss = [z.surface(c) for g in geoms if hasattr(g, "exterior") for c in g.exterior.coords]
            lo, hi = min(ss), max(ss)
            if zb >= hi + STAIR_HEADROOM_MM:
                continue
            if zt + UNDER_CLEAR_MM <= lo - STAIR_WAIST_MM:  # below it (an end's floor is the storey above's)
                under = under or z.kind != "approach"
                continue
            return False, False
    return True, under


# ------------------------------------------------------------------------------------------------ walls

@dataclass
class WallSpot:
    wall_key: str
    side: str
    s: float
    point: Point
    facing: Point
    room: Room | None
    under: bool
    level: str

    def host(self, height: float) -> Host:
        return Host(kind="wall", wall_key=self.wall_key, side=self.side, height_mm=r1(height))


def _thickness(ctx: Ctx, w: WallOp) -> float:
    types = {op.name: op for op in ctx.plan.ops if isinstance(op, WallTypeOp)}
    if w.type in types and types[w.type].thickness_mm:
        return float(types[w.type].thickness_mm)
    return type_thickness(w.type) or 250.0


def _base_top(ctx: Ctx, w: WallOp) -> tuple[float, float]:
    base = ctx.elev(w.level) + w.base_offset_mm
    top = ctx.elev(w.top_level) + w.top_offset_mm if w.top_level else base + (w.height_mm or 0)
    return base, top


def _columns(ctx: Ctx, z: float) -> list[Polygon]:
    out = []
    for c in ctx.plan.ops:
        if not isinstance(c, ColumnOp):
            continue
        if not ctx.elev(c.level) + c.base_offset_mm <= z < ctx.elev(c.top_level) + c.top_offset_mm:
            continue
        m = re.search(r"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)", c.type or "", re.I)
        if not m:
            continue
        b, h = float(m.group(1)), float(m.group(2))
        a = math.radians(c.rotation_deg)
        ux, uy = math.cos(a), math.sin(a)
        out.append(Polygon([(c.point[0] + sx * b / 2 * ux - sy * h / 2 * uy, c.point[1] + sx * b / 2 * uy + sy * h / 2 * ux)
                            for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]))
    return out


def _openings(ctx: Ctx, w: WallOp) -> list[tuple[float, float, float, float]]:
    """(s0, s1, bottom, top) along the wall of its doors and windows, bottom/top above its level."""
    types = {(op.category, op.name): op for op in ctx.plan.ops if isinstance(op, OpeningTypeOp)}
    out = []
    for op in ctx.plan.ops:
        if not isinstance(op, (DoorOp, WindowOp)) or op.host_key != w.key:
            continue
        t = types.get((op.op, op.type))
        nw, nh = type_size(op.type)
        width = (t.width_mm if t else nw) or 1000.0
        height = (t.height_mm if t else nh)
        if isinstance(op, DoorOp):
            b, top = 0.0, height or DEFAULT_DOOR_HEAD_MM
        else:
            b = op.sill_mm if op.sill_mm is not None else 900.0
            top = b + (height or 1200.0)
        out.append((op.offset_mm - width / 2, op.offset_mm + width / 2, b, top))
    return out


def wall_spots_at(ctx: Ctx, loc: Location, level: str, width: float, depth: float, bottom: float, top: float,
                  n: int = 1, min_gap: float = 0.0) -> tuple[list[WallSpot], str | None]:
    """Up to ``n`` spots on wall faces of ``level`` for a device ``width`` x ``depth`` from ``bottom`` to ``top``
    above the level, at ``loc``; or the reason there is none."""
    z = ctx.elev(level)
    zb, zt = z + bottom, z + top
    if loc.near == "stair":
        st = [s for s in stairs_of(ctx) if s.op.base_level == level]
        target = st[0].foot if st else None
        if target is None:
            st = [s for s in stairs_of(ctx) if s.op.top_level == level]
            target = st[0].head if st else None
        if target is None:
            return [], f"no stair starts or ends on {level}"
        reach = NEAR_STAIR_MAX_MM
    else:
        target, reach = loc.point, NEAR_POINT_MAX_MM
    walls = [w for w in ctx.plan.ops if isinstance(w, WallOp)]
    spans = {w.key: _base_top(ctx, w) for w in walls}
    here = [w for w in walls if spans[w.key][0] <= zb + 1 and spans[w.key][1] >= zt + 50]
    solids = [_rect(w.start, unit(sub(w.end, w.start)), math.dist(w.start, w.end), _thickness(ctx, w))
              for w in walls if spans[w.key][0] < zt and spans[w.key][1] > zb and math.dist(w.start, w.end) > 1]
    solid_keys = [w.key for w in walls if spans[w.key][0] < zt and spans[w.key][1] > zb and
                  math.dist(w.start, w.end) > 1]
    solids += _columns(ctx, zb + 1)
    solid_keys += ["column"] * (len(solids) - len(solid_keys))
    rooms = [r for r in ctx.model.on_level(level) if r.polygon]
    others = [d for d in ctx.devices if d.level == level and d.host.kind == "wall"]
    cands: list[tuple[tuple, WallSpot]] = []
    for w in here:
        L = math.dist(w.start, w.end)
        if L < width + 2 * CORNER_MM:
            continue
        u = unit(sub(w.end, w.start))
        nl = (-u[1], u[0])
        t = _thickness(ctx, w)
        if min(math.dist(target, w.start), math.dist(target, w.end), _seg_dist(target, w.start, w.end)) > reach + t:
            continue
        blocks = [(s0 - OPENING_MARGIN_MM, s1 + OPENING_MARGIN_MM) for s0, s1, b, tp in _openings(ctx, w)
                  if tp > bottom and b < top]
        for side, sgn in (("left", 1.0), ("right", -1.0)):
            facing = (nl[0] * sgn, nl[1] * sgn)
            a = add(w.start, facing, t / 2)
            taken = []
            for d in others:
                if (d.host.wall_key, d.host.side) != (w.key, side) or not d.size:
                    continue
                if d.elevation_mm + d.size.height_mm / 2 + 50 <= bottom or d.elevation_mm - d.size.height_mm / 2 - 50 >= top:
                    continue
                c = dot(sub(d.insertion, a), u)
                taken.append((c - d.size.width_mm / 2 - OPENING_MARGIN_MM, c + d.size.width_mm / 2 + OPENING_MARGIN_MM))
            s = CORNER_MM + width / 2
            while s <= L - CORNER_MM - width / 2 + 1e-6:
                if any(s + width / 2 > b0 and s - width / 2 < b1 for b0, b1 in blocks + taken):
                    s += 100
                    continue
                p = add(a, u, s)
                foot = Polygon([add(p, u, -width / 2), add(p, u, width / 2), add(add(p, u, width / 2), facing, depth),
                                add(add(p, u, -width / 2), facing, depth)])
                if any(k != w.key and g.intersects(foot.buffer(-2)) for g, k in zip(solids, solid_keys)):
                    s += 100
                    continue
                probe = SPoint(add(p, facing, 50))
                room = next((r for r in rooms if r.shape().covers(probe)), None)
                if room is not None and (room.flat or room not in ctx.fit_rooms or
                                         not any(c in COMMON_WALL_ROOMS for c in ctx.classes(room))):
                    s += 100
                    continue
                ok, under = stair_clear(ctx, foot, zb, zt)
                dist = math.dist(p, target)
                if ok and dist <= reach:
                    rank = (0 if (under and loc.under and loc.near) else 1, round(dist), w.key, side, round(s))
                    cands.append((rank, WallSpot(w.key, side, s, pt(p), facing, room, under, level)))
                s += 100
    cands.sort(key=lambda c: c[0])
    chosen: list[WallSpot] = []
    for _, sp in cands:
        if len(chosen) >= n:
            break
        if all(math.dist(sp.point, c.point) >= max(min_gap, width + OPENING_MARGIN_MM) for c in chosen):
            chosen.append(sp)
    if not chosen:
        return [], (f"no wall face on {level} {loc.describe()} (within {reach / 1000:g} m) clear of doors, windows, "
                    f"the stair's run and headroom for a {width:g} mm wide device from {bottom:g} to {top:g} mm")
    return chosen, None


def _seg_dist(p: Point, a: Point, b: Point) -> float:
    return LineString([a, b]).distance(SPoint(p))


def place_on_wall(ctx: Ctx, what: str, system: str, sp: WallSpot, height: float, **kw):
    return ctx.place(what, system, sp.room, sp.level, sp.point, height, sp.facing, sp.host(height), **kw)


def spot_note(sp: WallSpot, loc: Location) -> str:
    where = "under the stair" if sp.under else ("beside the stair, clear of its run and headroom" if loc.near
                                                else "on the wall nearest the answered point")
    if loc.near and loc.under and not sp.under:
        where += " (no room under the flights for it)"
    return f"{where}, on wall {sp.wall_key} ({sp.side} side)"


# ------------------------------------------------------------------------------------------------ outside

def building_outline(ctx: Ctx):
    parts = [Polygon(op.points) for op in ctx.plan.ops if isinstance(op, FloorOp)]
    parts += [Polygon(r.polygon).buffer(300, join_style="mitre") for r in ctx.model.rooms if r.polygon]
    return unary_union(parts).buffer(0) if parts else Polygon()


def footings(ctx: Ctx) -> tuple[Any, str]:
    """The foundations' outline and what it is: foundation slabs and footing ops, else the building's outline."""
    parts = []
    for op in ctx.plan.ops:
        if isinstance(op, FoundationSlabOp) or "footing" in str(getattr(op, "op", "")):
            pts = getattr(op, "points", None)
            if pts:
                parts.append(Polygon(pts))
    if parts:
        return unary_union(parts), "the foundation slab/footings"
    return building_outline(ctx), "the building's outline (the plan has no foundation or footing ops)"


def boundary_walls(ctx: Ctx, outline) -> list[WallOp]:
    lowest = ctx.elev(ctx.storeys[0]) if ctx.storeys else 0.0
    out = []
    for w in ctx.plan.ops:
        if not isinstance(w, WallOp) or w.top_level or not w.height_mm or w.height_mm > 2400:
            continue
        if ctx.elev(w.level) > lowest + 1:
            continue
        if outline.buffer(500).intersects(LineString([w.start, w.end])):
            continue
        out.append(w)
    return out


def front_side(ctx: Ctx, side: str | None, why: str) -> tuple[str | None, Point | None]:
    """A compass side and its direction; "front" is the answer to fitout:front_side (asked when not given)."""
    if side == "front":
        given = ctx.answer("fitout:front_side")
        if given in SIDES:
            side = given
        else:
            ctx.ask("fitout:front_side", "fitout_site", f"Which side of the building is its front ({why})? Answer "
                    "north, south, east or west (plan +Y is taken as north).", options=list(SIDES))
            return None, None
    if side not in SIDES:
        return None, None
    ctx.assume("fitout:plan_north", "Plan +Y is north: the plan gives no true north, so a side answered as north, "
               "south, east or west is read in plan directions", "+Y", "B2M convention")
    return side, SIDES[side]


@dataclass
class Entrance:
    point: Point
    path: Any  # the entrance path's strip (a polygon), or None
    what: str


def entrance(ctx: Ctx, d: Point, outline, bounds: list[WallOp]) -> Entrance:
    """The entrance on the side facing ``d``: the gate in the boundary wall, else a door of the lowest storey opening
    that way, else the stair's foot projected onto the face, else the face's middle."""
    a = (-d[1], d[0])
    coords = [c for g in (outline.geoms if hasattr(outline, "geoms") else [outline]) for c in g.exterior.coords]
    face = max(dot(c, d) for c in coords)
    lat = [dot(c, a) for c in coords if dot(c, d) >= face - 1000] or [dot(c, a) for c in coords]
    mid_lat = (min(lat) + max(lat)) / 2

    def xy(l: float, p: float) -> Point:
        return (a[0] * l + d[0] * p, a[1] * l + d[1] * p)

    def strip(l0: float, l1: float, p0: float, p1: float):
        return Polygon([xy(l0, p0), xy(l1, p0), xy(l1, p1), xy(l0, p1)]).buffer(PATH_MARGIN_MM, join_style="mitre")

    front = [w for w in bounds if abs(dot(unit(sub(w.end, w.start)), d)) < 0.1
             and min(dot(w.start, d), dot(w.end, d)) > face]
    if front:
        line = max(dot(w.start, d) for w in front)
        ivs = sorted((min(dot(w.start, a), dot(w.end, a)), max(dot(w.start, a), dot(w.end, a))) for w in front
                     if abs(dot(w.start, d) - line) < 1500)
        gaps = []
        hi = ivs[0][1]
        for lo2, hi2 in ivs[1:]:
            if lo2 - hi >= MIN_GATE_MM:
                gaps.append((hi, lo2))
            hi = max(hi, hi2)
        if gaps:
            g0, g1 = min(gaps, key=lambda g: (abs((g[0] + g[1]) / 2 - mid_lat), g))
            return Entrance(pt(xy((g0 + g1) / 2, line)), strip(g0, g1, face, line),
                            f"the gate ({g1 - g0:.0f} mm gap in the boundary wall)")
    lowest = ctx.storeys[0] if ctx.storeys else None
    for r in sorted(ctx.model.on_level(lowest) if lowest else [], key=lambda r: r.key):
        for o in r.doors:
            if o.other_room is None and o.face is not None and r.faces[o.face].outer and \
                    dot((-r.faces[o.face].inward[0], -r.faces[o.face].inward[1]), d) > 0.9:
                lc, w_ = dot(o.center, a), max(o.width_mm or 0, 1500)
                return Entrance(pt(o.center), strip(lc - w_ / 2, lc + w_ / 2, face, face + 20000), f"door {o.key}")
    st = [s for s in stairs_of(ctx) if s.op.base_level == lowest]
    if st:
        lc = dot(st[0].foot, a)
        return Entrance(pt(xy(lc, face)), strip(lc - 1500, lc + 1500, face, face + 20000),
                        "the stair's foot, on the building's face")
    return Entrance(pt(xy(mid_lat, face)), None, "the middle of the building's face")


def outside_box(ctx: Ctx, side: str, near: Any, width: float, depth: float, clear: float
                ) -> tuple[Point | None, str, dict]:
    """The centre of a ``width`` x ``depth`` box outside the building beyond its ``side`` face (compass side,
    resolved), and a note; or None and the reason. ``info``: the zones (for the pump beside it)."""
    d = SIDES[side]
    outline = building_outline(ctx)
    if outline.is_empty:
        return None, "the plan has no slabs or rooms to find the building's face", {}
    found, found_what = footings(ctx)
    bounds = boundary_walls(ctx, outline)
    ent = entrance(ctx, d, outline, bounds)
    target = ent.point
    if isinstance(near, (list, tuple)) and len(near) == 2:
        target = (float(near[0]), float(near[1]))
    coords = [c for g in (outline.geoms if hasattr(outline, "geoms") else [outline]) for c in g.exterior.coords]
    face = max(dot(c, d) for c in coords)
    a = (-d[1], d[0])
    big = 1e6
    half = Polygon([(a[0] * l + d[0] * p, a[1] * l + d[1] * p) for l, p in
                    ((-big, face), (big, face), (big, face + big), (-big, face + big))])
    if bounds:
        site = unary_union([LineString([w.start, w.end]) for w in bounds]).convex_hull
        t = max(_thickness(ctx, w) for w in bounds)
        site = site.buffer(-(t / 2 + BOUNDARY_CLEAR_MM), join_style="mitre")
        site_what = "inside the boundary walls"
    else:
        site = outline.buffer(30000, join_style="mitre")
        site_what = "(the plan has no boundary walls: the site's edge is not checked)"
    free = site.intersection(half).difference(found.buffer(FOOTING_CLEAR_MM, join_style="mitre"))
    free = free.difference(outline.buffer(clear, join_style="mitre"))
    if ent.path is not None:
        free = free.difference(ent.path)
    info = {"site": site, "path": ent.path, "outline": outline, "entrance": ent}
    if free.is_empty:
        return None, f"no ground on the {side} side clear of {found_what} by {FOOTING_CLEAR_MM:g} mm and of the " \
                     "entrance path", info
    best = None
    minx, miny, maxx, maxy = free.bounds
    step = 250.0
    x = math.floor((minx + width / 2) / step) * step
    while x <= maxx - width / 2 + 1e-6:
        y = math.floor((miny + depth / 2) / step) * step
        while y <= maxy - depth / 2 + 1e-6:
            if free.covers(box(x - width / 2, y - depth / 2, x + width / 2, y + depth / 2)):
                key = (round(math.dist((x, y), target)), x, y)
                if best is None or key < best[0]:
                    best = (key, (x, y))
            y += step
        x += step
    if best is None:
        return None, f"no {width:g} x {depth:g} mm of ground on the {side} side clear of {found_what} by " \
                     f"{FOOTING_CLEAR_MM:g} mm, the building and the entrance path", info
    note = (f"{side} of the building near {ent.what}, clear of {found_what} by at least {FOOTING_CLEAR_MM:g} mm and of "
            f"the entrance path, {site_what}")
    return pt(best[1]), note, info


def beside(ctx: Ctx, centre: Point, half: float, size: tuple[float, float], info: dict, towards: Point | None
           ) -> Point | None:
    """A point beside a box (``half`` its half side) for a small item ``size`` (w, d): clear of the entrance path,
    the building and the site's edge; the side nearest ``towards`` first."""
    w, dp = size
    cands = []
    for v in ((1.0, 0.0), (-1.0, 0.0), (0.0, 1.0), (0.0, -1.0)):
        c = add(centre, v, half + 600)
        b = box(c[0] - w / 2, c[1] - dp / 2, c[0] + w / 2, c[1] + dp / 2)
        if info.get("path") is not None and info["path"].intersects(b):
            continue
        if info.get("outline") is not None and info["outline"].buffer(300).intersects(b):
            continue
        if info.get("site") is not None and not info["site"].covers(b):
            continue
        key = (abs(c[0] - towards[0]) + abs(c[1] - towards[1]) if towards else 0.0, v)
        cands.append((key, pt(c)))
    return min(cands)[1] if cands else None


# ------------------------------------------------------------------------------------------------ roofs and cores

@dataclass
class StairRoof:
    level: str
    outline: Any  # the roof's outline (the rooms with their walls)
    rooms: list[Room]
    slab: bool  # the plan has a slab at that level over them


def stair_roof(ctx: Ctx) -> StairRoof | None:
    """The roof over the stair/lift rooms on the terrace level: the level their walls reach."""
    if not ctx.roof:
        return None
    rooms = [r for r in ctx.fit_rooms if r.level == ctx.roof and any(c in STAIR_ROOF_CLASSES for c in ctx.classes(r))]
    if not rooms:
        return None
    above = {n: z for n, z in ctx.levels.items() if z > ctx.elev(ctx.roof) + 1}
    votes: dict[str, int] = {}
    walls = {w.key: w for w in ctx.plan.ops if isinstance(w, WallOp)}
    for r in rooms:
        lv = None
        if r.height_mm:
            top = r.elevation_mm + r.height_mm
            lv = min((n for n, z in above.items() if abs(z - top) <= 100), key=lambda n: (above[n], n), default=None)
        if lv is None:
            tops = [walls[f.wall_key].top_level for f in r.faces if f.wall_key in walls and
                    walls[f.wall_key].top_level in above]
            lv = max(set(tops), key=lambda n: (tops.count(n), -above[n], n)) if tops else None
        if lv:
            votes[lv] = votes.get(lv, 0) + 1
    if not votes:
        return None
    level = max(votes, key=lambda n: (votes[n], -above[n], n))
    rooms = [r for r in rooms if r.height_mm is None or abs(r.elevation_mm + r.height_mm - above[level]) <= 100]
    t = max((f.thickness_mm or 0 for r in rooms for f in r.faces if f.kind == "wall"), default=250) or 250
    outline = unary_union([Polygon(r.polygon).buffer(t, join_style="mitre") for r in rooms])
    slab = any(isinstance(op, FloorOp) and op.level == level and Polygon(op.points).intersects(outline)
               for op in ctx.plan.ops)
    return StairRoof(level, outline, rooms, slab)


def stair_cores(ctx: Ctx) -> list[tuple[str, Polygon]]:
    """Stair wells a pipe can rise through: the stair shaft ops (by outline), else the stairs' footprint."""
    groups: dict[tuple, list[ShaftOp]] = {}
    for op in ctx.plan.ops:
        if isinstance(op, ShaftOp):
            groups.setdefault(tuple(sorted(pt(p) for p in op.points)), []).append(op)
    out = []
    for _, ops in sorted(groups.items(), key=lambda kv: kv[1][0].key):
        if any(ctx.shaft_kinds.get(op.key) == "stair" for op in ops) or \
                ctx.answer(f"fitout:shaft_kind:{ops[0].key}") == "stair":
            out.append((ops[0].key, Polygon(ops[0].points)))
    if not out and stairs_of(ctx):
        out.append((stairs_of(ctx)[0].op.key, stairs_of(ctx)[0].footprint.convex_hull))
    return out


def core_corner(ctx: Ctx, core: Polygon, near: Point, inset: float = 100.0) -> Point | None:
    """A corner of a stair well (``inset`` inside its walls) off the flights and their ends, nearest ``near``."""
    inner = core.buffer(-inset, join_style="mitre")
    if inner.is_empty:
        return None
    flights = [z.poly.buffer(50) for s in stairs_of(ctx) for z in s.zones if z.kind in ("flight", "approach")]
    pts = [pt(c) for c in list(inner.exterior.coords)[:-1]]
    ok = [p for p in pts if not any(f.covers(SPoint(p)) for f in flights)]
    return min(ok, key=lambda p: (abs(p[0] - near[0]) + abs(p[1] - near[1]), p), default=None)
