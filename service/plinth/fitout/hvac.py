"""HVAC by rules (PRD Step 10, F4 "HVAC"): split AC units in bedrooms and living rooms, exhaust fans in toilets and
kitchens.

    from plinth.fitout import hvac
    hvac.lay_out(ctx)     # ctx: plinth.fitout.mepcore.Ctx; adds devices, unplaced, questions

Why so: Indian apartments are cooled room by room with wall-mounted split units, not central systems; the indoor unit
goes high (``indoor_bottom_mm``) on an exterior wall so the refrigerant pipe goes through the wall to the outdoor unit
on brackets on that wall's outer face. An exterior wall is a wall face with the outside beyond it that the plan does
not type as interior (``Ctx.exterior``): a recess or court between two wings, drawn with interior walls, is not one.
The outdoor unit's box stands outside the building's footprint, clear of the face's windows and doors over its
height (never in front of a window), nearest its indoor unit; the run (along the outer face, through the wall, the
drop) is kept within ``max_pipe_run_m`` and stated in both units' notes. When no indoor spot on an exterior wall is
left, the indoor unit goes on another wall of the room and the run (along the room's walls at high level) is stated.
A room with no exterior wall (or none with room for the outdoor unit) is asked, ``fitout:ac_outdoor:<room key>``: a
wall key of the room's outer walls (hang it there anyway) or "none". The indoor unit may sit above a window (its
head is below the unit) but not above a door, nor over a wardrobe. The size steps with the room's area
(``split_ac.sizing``); a room larger than the last step is asked (how many units of what size), never guessed, once
per room class:
``fitout:ac:<class>`` (``fitout:ac:living``) answers every room of that class beyond the table, with one size in TR, a
list of sizes (``[1.5, 1.5]``: two units) or "none"; ``fitout:ac:room:<room key>`` overrides it for one room. The
class is the room's first class the rule set cools (``split_ac.classes``), so the answer holds when another import of
the same drawing names the room differently or measures it a little larger. The older per-room key
``fitout:ac:<name>:<whole m²>`` is still accepted (before the class answer). Several units in one room stand on its
exterior walls as far apart as the walls let them (up to ``AC_SPACING_MM``, then nearest the middle), each with its
outdoor unit on an exterior wall clear of the others (side by side, or one under the other); the electrical layout
gives each its 16 A socket and circuit. Exhaust fans go high on a wall that opens to the outside or onto an
open-to-sky duct (the toilets of the G+4 sample have no outside wall: their windows open onto the duct), near the wet
area (shower, WC) or the hob. Refrigerant piping and condensate drains are not drawn (their run is stated in the
units' notes).
"""

from __future__ import annotations

import math

from shapely.geometry import Polygon
from shapely.ops import unary_union

from plinth.fitout.mepcore import (OPENING_MARGIN_MM, Ctx, Host, Spot, along, centre_of,
                                face_point, nearest_spot, opening_span, pt, r1, wall_spots)
from plinth.fitout.rooms import Room
from plinth.fitout.rules import Size, normalise

AC_SPACING_MM = 3000.0  # several units in one room: this far apart is far enough (each cools its part)
OUTDOOR_GAP_MM = 50.0  # an outdoor unit's back stands this far off the wall's outer face (its brackets)
LOW_OUTDOOR_MIN_MM = 150.0  # an outdoor unit under a window's sill stands at least this high (clear of the floor)


def lay_out(ctx: Ctx) -> None:
    split_acs(ctx)
    exhausts(ctx)


def _ac_class(ctx: Ctx, room: Room) -> str:
    """The class a room is cooled as: its first class in ``split_ac.classes``."""
    ac = ctx.rules.hvac.split_ac
    return next(c for c in ctx.classes(room) if c in ac.classes)


def _units(given) -> list[float] | None:
    """An AC answer's unit sizes: "none" -> [], a size or a list of sizes in TR; None when it is no such answer."""
    if given == "none":
        return []
    if isinstance(given, (int, float)) and not isinstance(given, bool):
        given = [given]
    if isinstance(given, (list, tuple)) and given and             all(isinstance(x, (int, float)) and not isinstance(x, bool) and x > 0 for x in given):
        return [float(x) for x in given]
    return None


def _ac_sizes(ctx: Ctx, room: Room, beyond: dict[str, list[Room]]) -> list[float] | None:
    """The room's units in TR: from the sizing table, else answered (per room, the older per-room key, per class),
    else None (asked per class; ``beyond``: the rooms of each class past the table, for the question's text)."""
    ac = ctx.rules.hvac.split_ac
    tr = ac.tr_for(room.area_m2 or 0)
    if tr is not None:
        return [tr]
    cls = _ac_class(ctx, room)
    legacy = f"fitout:ac:{normalise(room.name) or room.key}:{(room.area_m2 or 0):.0f}"  # whole m² (older answers)
    class_q = f"fitout:ac:{cls}"
    for qid in (f"fitout:ac:room:{room.key}", legacy, class_q):
        units = _units(ctx.answer(qid))
        if units is not None:
            return units
    rooms = beyond.get(cls, [room])
    areas = sorted({round(r.area_m2 or 0, 1) for r in rooms})
    span = f"{areas[0]:g} m²" if len(areas) == 1 else f"{areas[0]:g} to {areas[-1]:g} m²"
    ctx.ask(class_q, "fitout_ac", f"How should the {cls} rooms larger than the rule set's largest step "
            f"({ac.sizing[-1].max_area_m2:g} m² for {ac.sizing[-1].tr:g} TR) be air-conditioned? {len(rooms)} "
            f"room(s), {span}. Answer the units' sizes in TR for each such room (e.g. [1.5, 1.5]: two units) or "
            f"none; one room can be answered on its own with fitout:ac:room:<room key>.", options=["none"],
            instances=[room.key])
    return None


def split_acs(ctx: Ctx) -> None:
    ac = ctx.rules.hvac.split_ac
    ctx.assume("fitout:split_ac", f"One wall-mounted split AC per {', '.join(ac.classes)} room, sized by area "
               f"({', '.join(f'<= {s.max_area_m2:g} m²: {s.tr:g} TR' for s in ac.sizing)}), on an exterior wall with "
               f"its underside at {ac.indoor_bottom_mm:g} mm and the outdoor unit on brackets on that wall's outer "
               f"face (underside at {ac.outdoor_bottom_mm:g} mm), outside the building and clear of its windows and "
               f"doors, the refrigerant run at most {ac.max_pipe_run_m:g} m", [s.model_dump() for s in ac.sizing],
               ac.source)
    cooled = [r for r in ctx.fit_rooms if any(c in ac.classes for c in ctx.classes(r))]
    beyond: dict[str, list[Room]] = {}
    for room in cooled:
        if ac.tr_for(room.area_m2 or 0) is None:
            beyond.setdefault(_ac_class(ctx, room), []).append(room)
    for room in cooled:
        sizes = _ac_sizes(ctx, room, beyond)
        if sizes is None:
            ctx.unplace("ac_indoor", room, room.level, f"{room.area_m2:.1f} m² is beyond the sizing table: the units "
                        "are asked")
            continue
        if not sizes:
            continue
        top = ac.indoor_bottom_mm + ac.indoor.height_mm
        if top > ctx.ceiling(room) - 50:
            ctx.unplace("ac_indoor", room, room.level, f"the ceiling ({ctx.ceiling(room):.0f} mm) is too low for the "
                        f"indoor unit ({ac.indoor_bottom_mm:g}-{top:g} mm)")
            continue
        ext = _outdoor_faces(ctx, room)
        if ext is None:
            continue
        outs = _outdoor_spots(ctx, ac, room, ext)
        if not outs:
            _ask_outdoor(ctx, room, "no stretch of exterior wall outside the building and clear of its windows and "
                         f"doors at {ac.outdoor_bottom_mm:g}-{ac.outdoor_bottom_mm + ac.outdoor.height_mm:g} mm for "
                         f"a {ac.outdoor.width_mm:g} mm outdoor unit")
            continue
        # the indoor units on the exterior walls; else on another wall of the room, the refrigerant run stated
        spots = [sp for sp in wall_spots(ctx, room, ac.indoor.width_mm, ac.indoor_bottom_mm, top, faces=ext)
                 if _reachable(ac, room, sp, outs)]
        if len(spots) < len(sizes):
            more = [sp for sp in wall_spots(ctx, room, ac.indoor.width_mm, ac.indoor_bottom_mm, top)
                    if sp.face not in ext and _reachable(ac, room, sp, outs)]
            spots += more if not spots or len(sizes) > 1 else []
        chosen = _choose(ac, room, spots, len(sizes))
        pairs: list[tuple[Spot, tuple[Spot, float], float]] = []
        for sp in chosen:
            free = [o for o in outs if not any(_clash(ac, o[0].point, o[1], q[0].point, q[1]) for _, q, _ in pairs)]
            best = min(((_run_m(ac, room, sp, o), o[0].face, o[0].s, -o[1], i) for i, o in enumerate(free)),
                       default=None)
            if best is not None and best[0] <= ac.max_pipe_run_m:
                pairs.append((sp, free[best[4]], best[0]))
        for _ in range(len(sizes) - len(pairs)):
            ctx.unplace("ac_indoor", room, room.level, "no stretch of wall clear of doors, windows above "
                        f"{ac.indoor_bottom_mm:g} mm and tall furniture for a {ac.indoor.width_mm:g} mm unit within "
                        f"{ac.max_pipe_run_m:g} m of an outdoor unit's place on an exterior wall" +
                        (f" (only {len(pairs)} of the {len(sizes)} units fit)" if pairs else ""))
        for (sp, (out, bottom), run), tr in zip(pairs, sizes):
            fo = room.faces[out.face]
            if out.face == sp.face and abs(out.s - sp.s) < 50:
                route = "through the wall behind it to the outdoor unit"
            elif out.face == sp.face:
                route = (f"through the wall behind it and {abs(out.s - sp.s) / 1000:.1f} m along the outer face to "
                         "the outdoor unit")
            else:
                route = (f"along the room's walls at high level to wall {fo.wall_key} and through it to the outdoor "
                         "unit (the indoor unit is not on that exterior wall)")
            indoor = ctx.on_wall("ac_indoor", "hvac", room, sp, ac.indoor_bottom_mm + ac.indoor.height_mm / 2,
                                 size=ac.indoor, rating=f"{tr:g} TR", source=ac.source,
                                 note=f"refrigerant pipes and condensate drain {route}: about {run:.1f} m "
                                      "(not drawn)")
            other = "right" if fo.side == "left" else "left"
            ctx.place("ac_outdoor", "hvac", room, room.level, out.point, bottom, (-fo.inward[0], -fo.inward[1]),
                      Host(kind="outside_wall", wall_key=fo.wall_key, side=other,
                           height_mm=r1(bottom + ac.outdoor.height_mm / 2)),
                      size=ac.outdoor, rating=f"{tr:g} TR", load=False, source=ac.source,
                      note=f"outdoor unit of {indoor.key}, powered through it; on the outer face of exterior wall "
                           f"{fo.wall_key}, clear of its windows and doors" +
                           ("" if bottom == ac.outdoor_bottom_mm else " (under the window: no room beside it at "
                            f"{ac.outdoor_bottom_mm:g} mm)") + f"; refrigerant run about {run:.1f} m")


def _outdoor_faces(ctx: Ctx, room: Room) -> list[int] | None:
    """The room's faces an outdoor unit may hang outside: its exterior walls (``Ctx.exterior``), and a wall answered
    by ``fitout:ac_outdoor:<room key>`` (a recess or court the plan draws with interior walls); None when there is
    none (asked) or the answer is "none"."""
    qid = f"fitout:ac_outdoor:{room.key}"
    given = ctx.answer(qid)
    if given == "none":
        ctx.unplace("ac_indoor", room, room.level, "no air-conditioning: the outdoor unit's place is answered none")
        return None
    ext = [fi for fi, f in enumerate(room.faces) if ctx.exterior(f)]
    ext += [fi for fi, f in enumerate(room.faces) if f.kind == "wall" and f.outer and isinstance(given, str)
            and f.wall_key == given and fi not in ext]
    if not ext:
        _ask_outdoor(ctx, room, "no exterior wall: the outdoor unit must hang on an exterior wall's outer face")
        return None
    return ext


def _ask_outdoor(ctx: Ctx, room: Room, why: str) -> None:
    qid = f"fitout:ac_outdoor:{room.key}"
    walls = sorted({f.wall_key for f in room.faces if f.kind == "wall" and f.outer and f.wall_key})
    ctx.ask(qid, "fitout_ac_outdoor", f"Where does the split AC's outdoor unit of \"{room.name}\" ({room.key}, "
            f"{room.level}) go? The room has {why}." + (f" Answer a wall key ({', '.join(walls)}: the room's walls "
                                                         "with the outside, or a recess, beyond them) to hang it on "
                                                         "that wall's outer face anyway, or" if walls else
                                                         " Answer") + " none (no AC in this room).",
            options=["none", *walls], instances=[room.key])
    ctx.unplace("ac_indoor", room, room.level, f"{why} (asked)")


def _outdoor_spots(ctx: Ctx, ac, room: Room, faces: list[int]) -> list[tuple[Spot, float]]:
    """Places for outdoor units on the outer faces of ``faces``' walls, as (spot, underside above the floor): the
    unit's box (``outdoor`` width along the wall, depth from it, ``OUTDOOR_GAP_MM`` off it) along the wall's outer
    face (within the room's stretch or beyond it, along the same wall), outside the building's rooms and their walls,
    its width plus a margin clear of every door and window in that wall spanning its height (it never hangs in front
    of a window) and of the outdoor units already there. At the rule's height (``outdoor_bottom_mm``) and below the
    wall's windows (on brackets under the sills, ``OPENING_MARGIN_MM`` under the lowest, at least
    ``LOW_OUTDOOR_MIN_MM`` up); the run decides between them. A spot's ``s`` is along the room's face (negative or
    past its end beyond the room's stretch), its ``point`` the unit's centre in plan."""
    w, d, h = ac.outdoor.width_mm, ac.outdoor.depth_mm, ac.outdoor.height_mm
    level_rooms = [r for r in ctx.model.on_level(room.level) if r.polygon]
    rooms = unary_union([Polygon(r.polygon) for r in level_rooms] + [dk.poly for dk in ctx.ducts])
    placed = [dv for dv in ctx.devices if dv.what == "ac_outdoor" and dv.level == room.level]
    out: list[tuple[Spot, float]] = []
    for fi in faces:
        f = room.faces[fi]
        t = f.thickness_mm or 250
        wall = ctx.walls.get(f.wall_key or "")
        lo, hi = (0.0, f.length_mm) if wall is None else sorted((along(f, wall.start), along(f, wall.end)))
        if hi - lo < w:
            continue
        shape = rooms.buffer(t + OUTDOOR_GAP_MM / 2, join_style="mitre")  # the rooms with their walls
        spans, seen = [], set()
        for r in level_rooms:
            for o in r.openings:
                if o.wall_key == f.wall_key and o.face is not None and o.key not in seen:
                    seen.add(o.key)
                    _, _, ob, ot = opening_span(r, o)
                    s0, s1 = sorted((along(f, o.left), along(f, o.right)))
                    spans.append((s0, s1, ob, ot))
        sills = [ob for _, _, ob, _ in spans if ob > 0]
        heights = [ac.outdoor_bottom_mm]
        low = min(sills, default=0.0) - OPENING_MARGIN_MM - h
        if sills and LOW_OUTDOOR_MIN_MM <= low < ac.outdoor_bottom_mm:
            heights.append(low)
        near, far = t + OUTDOOR_GAP_MM, t + OUTDOOR_GAP_MM + d
        for bottom in heights:
            top = bottom + h
            blocks = [(s0 - OPENING_MARGIN_MM, s1 + OPENING_MARGIN_MM) for s0, s1, ob, ot in spans
                      if ot > bottom - OPENING_MARGIN_MM and ob < top + OPENING_MARGIN_MM]
            s = lo + w / 2
            while s <= hi - w / 2 + 1e-6:
                c = pt(face_point(f, s, -(near + d / 2)))
                if not any(s + w / 2 > b0 and s - w / 2 < b1 for b0, b1 in blocks) and \
                        not any(_clash(ac, c, bottom, dv.insertion, dv.elevation_mm) for dv in placed):
                    box_ = Polygon([face_point(f, s - w / 2, -near), face_point(f, s + w / 2, -near),
                                    face_point(f, s + w / 2, -far), face_point(f, s - w / 2, -far)])
                    if not shape.intersects(box_):
                        out.append((Spot(fi, round(s, 1), c), bottom))
                s += 100.0
    return out


def _clash(ac, p, bottom: float, q, q_bottom: float) -> bool:
    """Two outdoor units' boxes (centres ``p``, ``q``, undersides) would touch: closer than a unit's width and a
    margin in plan, their heights overlapping."""
    return math.dist(p, q) < ac.outdoor.width_mm + OPENING_MARGIN_MM and \
        abs(bottom - q_bottom) < ac.outdoor.height_mm + OPENING_MARGIN_MM


def _run_m(ac, room: Room, sp: Spot, outdoor: tuple[Spot, float]) -> float:
    """The refrigerant run from an indoor unit's spot to an outdoor unit's (spot, underside), in m: along the outer
    face (or along the room's walls to the outdoor unit's wall and through it), the wall's thickness, and the drop
    between the units' centres."""
    out, bottom = outdoor
    fo = room.faces[out.face]
    t = fo.thickness_mm or 250
    drop = abs((ac.indoor_bottom_mm + ac.indoor.height_mm / 2) - (bottom + ac.outdoor.height_mm / 2))
    if out.face == sp.face:
        along_ = abs(out.s - sp.s)
    else:
        q = face_point(fo, out.s)
        along_ = abs(sp.point[0] - q[0]) + abs(sp.point[1] - q[1])
    return round((along_ + t + drop) / 1000, 2)


def _reachable(ac, room: Room, sp: Spot, outs: list[tuple[Spot, float]]) -> bool:
    return any(_run_m(ac, room, sp, o) <= ac.max_pipe_run_m for o in outs)


def _apart(ac, room: Room, a: Spot, b: Spot) -> bool:
    """Two indoor units' spots do not overlap: on one face a unit's width and the margins apart, on two faces (round
    a corner) a unit's width apart."""
    if a.face == b.face:
        return abs(a.s - b.s) >= ac.indoor.width_mm + 2 * OPENING_MARGIN_MM
    return math.dist(a.point, b.point) >= ac.indoor.width_mm + OPENING_MARGIN_MM


def _choose(ac, room: Room, spots: list[Spot], n: int) -> list[Spot]:
    """``n`` spots for a room's units: one unit nearest the room's middle; several as far apart as the outside walls
    let them (up to ``AC_SPACING_MM``, so each cools its part of the room), then nearest the middle."""
    centre = centre_of(room)
    if n <= 1 or len(spots) < 2:
        sp = nearest_spot(spots, centre)
        return [sp] if sp is not None and n >= 1 else []

    def gap(group):
        return min(math.dist(p.point, q.point) for i, p in enumerate(group) for q in group[i + 1:])

    best = None
    for i, a in enumerate(spots):
        for b in spots[i + 1:]:
            if not _apart(ac, room, a, b):
                continue
            pair = [a, b]
            key = (-min(gap(pair), AC_SPACING_MM), sum(math.dist(p.point, centre) for p in pair),
                   [(p.face, p.s) for p in pair])
            if best is None or key < best[0]:
                best = (key, pair)
    if best is None:
        sp = nearest_spot(spots, centre)
        return [sp] if sp is not None else []
    chosen = sorted(best[1], key=lambda p: (p.face, p.s))
    while len(chosen) < n:
        rest = [sp for sp in spots if all(_apart(ac, room, sp, c) for c in chosen)]
        if not rest:
            break
        chosen.append(max(rest, key=lambda sp: (min(min(math.dist(sp.point, c.point) for c in chosen),
                                                    AC_SPACING_MM), -math.dist(sp.point, centre), -sp.face, -sp.s)))
    return chosen


def exhausts(ctx: Ctx) -> None:
    rules = ctx.rules.hvac.exhaust
    for room in ctx.fit_rooms:
        for cls in [c for c in ctx.classes(room) if c in rules]:
            ex = rules[cls]
            dia, bottom = ex.fan_dia_mm, ex.height_mm
            top = bottom + dia
            if top > ctx.ceiling(room) - 50:
                ctx.unplace("exhaust_fan", room, room.level, f"the ceiling ({ctx.ceiling(room):.0f} mm) is too low "
                            f"for a {dia:g} mm fan from {bottom:g} mm")
                continue
            spots = []
            for sp in wall_spots(ctx, room, dia, bottom, top, furniture=False):
                f = room.faces[sp.face]
                if f.outer or ctx.duct_beyond(room, f, sp.s) is not None:
                    spots.append(sp)
            near = [p for p in ctx.furn_by_room.get(room.key, [])
                    if p.item in (("hob", "kitchen_sink", "counter") if cls == "kitchen" else ("shower", "wc"))]
            target = near[0].insertion if near else centre_of(room)
            sp = nearest_spot(spots, target)
            if sp is None:
                ctx.unplace("exhaust_fan", room, room.level, "no wall opening to the outside or onto an open duct, "
                            "clear of doors and windows: mechanical ducting is needed (ask the MEP engineer)")
                continue
            f = room.faces[sp.face]
            vol = (room.area_m2 or 0) * ctx.ceiling(room) / 1000
            flow = math.ceil(vol * ex.air_changes_per_hour)
            via = "outside" if f.outer else f"duct {ctx.duct_beyond(room, f, sp.s).key}"
            ctx.on_wall("exhaust_fan", "hvac", room, sp, bottom + dia / 2,
                        size=Size(width_mm=dia, depth_mm=150, height_mm=dia),
                        rating=f"{dia:g} mm, {flow} m³/h for {ex.air_changes_per_hour:g} ACH", source=ex.source,
                        note=f"{cls} exhaust to the {via}")
