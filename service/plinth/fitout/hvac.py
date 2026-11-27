"""HVAC by rules (PRD Step 10, F4 "HVAC"): split AC units in bedrooms and living rooms, exhaust fans in toilets and
kitchens.

    from plinth.fitout import hvac
    hvac.lay_out(ctx)     # ctx: plinth.fitout.mepcore.Ctx; adds devices, unplaced, questions

Why so: Indian apartments are cooled room by room with wall-mounted split units, not central systems; the indoor unit
goes high (``indoor_bottom_mm``) on an outside wall so the refrigerant pipe goes straight through the wall to the
outdoor unit on brackets outside it (well within ``max_pipe_run_m``). It may sit above a window (its head is below
the unit) but not above a door, nor over a wardrobe. The size steps with the room's area (``split_ac.sizing``);
a room larger than the last step is asked (how many units of what size), never guessed, once per room class:
``fitout:ac:<class>`` (``fitout:ac:living``) answers every room of that class beyond the table, with one size in TR, a
list of sizes (``[1.5, 1.5]``: two units) or "none"; ``fitout:ac:room:<room key>`` overrides it for one room. The
class is the room's first class the rule set cools (``split_ac.classes``), so the answer holds when another import of
the same drawing names the room differently or measures it a little larger. The older per-room key
``fitout:ac:<name>:<whole m²>`` is still accepted (before the class answer). Several units in one room stand on its
outside walls as far apart as the walls let them (up to ``AC_SPACING_MM``, then nearest the middle), each with its
outdoor unit outside its wall clear of the others; the electrical layout gives each its 16 A socket and circuit. Exhaust fans go high on a
wall that opens to the outside or onto an open-to-sky duct (the toilets of the G+4 sample have no outside wall: their
windows open onto the duct), near the wet area (shower, WC) or the hob. Refrigerant piping and condensate drains are
not drawn (listed in the unit's note).
"""

from __future__ import annotations

import math

from shapely.geometry import Point as SPoint

from plinth.fitout.mepcore import (OPENING_MARGIN_MM, Ctx, Host, Spot, centre_of, face_point, nearest_spot, r1,
                                wall_spots)
from plinth.fitout.rooms import Room
from plinth.fitout.rules import Size, normalise

AC_SPACING_MM = 3000.0  # several units in one room: this far apart is far enough (each cools its part)


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
    if isinstance(given, (list, tuple)) and given and \
            all(isinstance(x, (int, float)) and not isinstance(x, bool) and x > 0 for x in given):
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
               f"({', '.join(f'<= {s.max_area_m2:g} m²: {s.tr:g} TR' for s in ac.sizing)}), on an outside wall with "
               f"its underside at {ac.indoor_bottom_mm:g} mm and the outdoor unit on brackets outside",
               [s.model_dump() for s in ac.sizing], ac.source)
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
        top = ac.indoor_bottom_mm + ac.indoor.height_mm
        if top > ctx.ceiling(room) - 50:
            ctx.unplace("ac_indoor", room, room.level, f"the ceiling ({ctx.ceiling(room):.0f} mm) is too low for the "
                        f"indoor unit ({ac.indoor_bottom_mm:g}-{top:g} mm)")
            continue
        outer = [fi for fi, f in enumerate(room.faces) if f.kind == "wall" and f.outer]
        if not outer:
            ctx.unplace("ac_indoor", room, room.level, "no outside wall for the indoor unit (the outdoor unit must "
                        "stand outside it)")
            continue
        spots = []
        for sp in wall_spots(ctx, room, ac.indoor.width_mm, ac.indoor_bottom_mm, top, faces=outer):
            if not ctx.footprint(room.level).covers(SPoint(_outdoor_point(ac, room, sp))):
                spots.append(sp)
        chosen = _choose(ac, room, spots, len(sizes))
        for _ in range(len(sizes) - len(chosen)):
            ctx.unplace("ac_indoor", room, room.level, "no stretch of outside wall clear of doors, windows above "
                        f"{ac.indoor_bottom_mm:g} mm and tall furniture for a {ac.indoor.width_mm:g} mm unit" +
                        (f" (only {len(chosen)} of the {len(sizes)} units fit)" if chosen else ""))
        for sp, tr in zip(chosen, sizes):
            f = room.faces[sp.face]
            indoor = ctx.on_wall("ac_indoor", "hvac", room, sp, ac.indoor_bottom_mm + ac.indoor.height_mm / 2,
                                 size=ac.indoor, rating=f"{tr:g} TR", source=ac.source,
                                 note="refrigerant pipes and condensate drain through the wall (not drawn)")
            other = "right" if f.side == "left" else "left"
            ctx.place("ac_outdoor", "hvac", room, room.level, _outdoor_point(ac, room, sp), ac.outdoor_bottom_mm,
                      (-f.inward[0], -f.inward[1]),
                      Host(kind="outside_wall", wall_key=f.wall_key, side=other,
                           height_mm=r1(ac.outdoor_bottom_mm + ac.outdoor.height_mm / 2)),
                      size=ac.outdoor, rating=f"{tr:g} TR", load=False, source=ac.source,
                      note=f"outdoor unit of {indoor.key}, powered through it")


def _outdoor_point(ac, room: Room, sp: Spot):
    f = room.faces[sp.face]
    return face_point(f, sp.s, -((f.thickness_mm or 250) + ac.outdoor.depth_mm / 2 + 50))


def _apart(ac, room: Room, a: Spot, b: Spot) -> bool:
    """Two units' spots do not overlap: on one face a unit's width and the margins apart, and the outdoor units'
    boxes clear of each other."""
    if a.face == b.face and abs(a.s - b.s) < ac.indoor.width_mm + 2 * OPENING_MARGIN_MM:
        return False
    oa, ob = _outdoor_point(ac, room, a), _outdoor_point(ac, room, b)
    return math.dist(oa, ob) >= ac.outdoor.width_mm + OPENING_MARGIN_MM


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
