"""HVAC by rules (PRD Step 10, F4 "HVAC"): split AC units in bedrooms and living rooms, exhaust fans in toilets and
kitchens.

    from b2m.fitout import hvac
    hvac.lay_out(ctx)     # ctx: b2m.fitout.mepcore.Ctx; adds devices, unplaced, questions

Why so: Indian apartments are cooled room by room with wall-mounted split units, not central systems; the indoor unit
goes high (``indoor_bottom_mm``) on an outside wall so the refrigerant pipe goes straight through the wall to the
outdoor unit on brackets outside it (well within ``max_pipe_run_m``). It may sit above a window (its head is below
the unit) but not above a door, nor over a wardrobe. The size steps with the room's area (``split_ac.sizing``);
a room larger than the last step is asked (how many units of what size), never guessed. Exhaust fans go high on a
wall that opens to the outside or onto an open-to-sky duct (the toilets of the G+4 sample have no outside wall: their
windows open onto the duct), near the wet area (shower, WC) or the hob. Refrigerant piping and condensate drains are
not drawn (listed in the unit's note).
"""

from __future__ import annotations

import math

from shapely.geometry import Point as SPoint

from b2m.fitout.mepcore import Ctx, Host, add, centre_of, face_point, nearest_spot, r1, wall_spots
from b2m.fitout.rooms import Room
from b2m.fitout.rules import Size, normalise


def lay_out(ctx: Ctx) -> None:
    split_acs(ctx)
    exhausts(ctx)


def _ac_sizes(ctx: Ctx, room: Room) -> list[float] | None:
    ac = ctx.rules.hvac.split_ac
    tr = ac.tr_for(room.area_m2 or 0)
    if tr is not None:
        return [tr]
    qid = f"fitout:ac:{normalise(room.name) or room.key}:{(room.area_m2 or 0):.1f}"
    given = ctx.answer(qid)
    if given == "none":
        return []
    if isinstance(given, (int, float)):
        given = [given]
    if isinstance(given, (list, tuple)) and given and all(isinstance(x, (int, float)) and x > 0 for x in given):
        return [float(x) for x in given]
    ctx.ask(qid, "fitout_ac", f"How should \"{room.name}\" ({room.area_m2:.1f} m²) be air-conditioned? It is larger "
            f"than the rule set's largest step ({ac.sizing[-1].max_area_m2:g} m² for {ac.sizing[-1].tr:g} TR): answer "
            "the units' sizes in TR (e.g. [2.0, 1.5]) or none.", options=["none"], instances=[room.key])
    return None


def split_acs(ctx: Ctx) -> None:
    ac = ctx.rules.hvac.split_ac
    ctx.assume("fitout:split_ac", f"One wall-mounted split AC per {', '.join(ac.classes)} room, sized by area "
               f"({', '.join(f'<= {s.max_area_m2:g} m²: {s.tr:g} TR' for s in ac.sizing)}), on an outside wall with "
               f"its underside at {ac.indoor_bottom_mm:g} mm and the outdoor unit on brackets outside",
               [s.model_dump() for s in ac.sizing], ac.source)
    for room in ctx.fit_rooms:
        if not any(c in ac.classes for c in ctx.classes(room)):
            continue
        sizes = _ac_sizes(ctx, room)
        if sizes is None:
            ctx.unplace("ac_indoor", room, room.level, f"{room.area_m2:.1f} m² is beyond the sizing table: the units "
                        "are asked")
            continue
        top = ac.indoor_bottom_mm + ac.indoor.height_mm
        if top > ctx.ceiling(room) - 50:
            ctx.unplace("ac_indoor", room, room.level, f"the ceiling ({ctx.ceiling(room):.0f} mm) is too low for the "
                        f"indoor unit ({ac.indoor_bottom_mm:g}-{top:g} mm)")
            continue
        for i, tr in enumerate(sizes):
            outer = [fi for fi, f in enumerate(room.faces) if f.kind == "wall" and f.outer]
            if not outer:
                ctx.unplace("ac_indoor", room, room.level, "no outside wall for the indoor unit (the outdoor unit "
                            "must stand outside it)")
                break
            spots = []
            for sp in wall_spots(ctx, room, ac.indoor.width_mm, ac.indoor_bottom_mm, top, faces=outer):
                f = room.faces[sp.face]
                out = face_point(f, sp.s, -((f.thickness_mm or 250) + ac.outdoor.depth_mm / 2 + 50))
                if not ctx.footprint(room.level).covers(SPoint(out)):
                    spots.append(sp)
            sp = nearest_spot(spots, centre_of(room))
            if sp is None:
                ctx.unplace("ac_indoor", room, room.level, "no stretch of outside wall clear of doors, windows above "
                            f"{ac.indoor_bottom_mm:g} mm and tall furniture for a {ac.indoor.width_mm:g} mm unit")
                break
            f = room.faces[sp.face]
            indoor = ctx.on_wall("ac_indoor", "hvac", room, sp, ac.indoor_bottom_mm + ac.indoor.height_mm / 2,
                                 size=ac.indoor, rating=f"{tr:g} TR", source=ac.source,
                                 note="refrigerant pipes and condensate drain through the wall (not drawn)")
            out = face_point(f, sp.s, -((f.thickness_mm or 250) + ac.outdoor.depth_mm / 2 + 50))
            other = "right" if f.side == "left" else "left"
            ctx.place("ac_outdoor", "hvac", room, room.level, out, ac.outdoor_bottom_mm, (-f.inward[0], -f.inward[1]),
                      Host(kind="outside_wall", wall_key=f.wall_key, side=other,
                           height_mm=r1(ac.outdoor_bottom_mm + ac.outdoor.height_mm / 2)),
                      size=ac.outdoor, rating=f"{tr:g} TR", load=False, source=ac.source,
                      note=f"outdoor unit of {indoor.key}, powered through it")


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
