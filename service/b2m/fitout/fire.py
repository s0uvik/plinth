"""Fire safety by rules (PRD Step 10, F4 "Fire safety"): the provisions NBC 2016 Part 4 asks for at the building's
height and occupancy, laid out where they are simple devices and listed where they are not.

    from b2m.fitout import fire
    fire.lay_out(ctx)     # ctx: b2m.fitout.mepcore.Ctx; adds devices, the riser, provisions, unplaced, questions

Why so: the provisions follow from the height band alone (``fire.bands``: the first whose ``max_height_m`` is at or
above the building's height; the height is the roof terrace above the lowest floor, listed as an assumption since NBC
measures from the average ground level, and an answer overrides it). Below 15 m (the G+4 sample at 13.5 m) a
residential A-4 building needs portable extinguishers on every floor and exit signs; higher bands add hose reels, a
wet riser with a landing valve per floor, call points, tanks and pumps, sprinklers, detection, which this module
places in the common lobbies and the common core where they are devices or a riser, and lists for the fire consultant
where they are systems (sprinklers, detection, PA, fire lift, refuge areas, yard hydrants). Extinguishers: one per
``per_area_m2`` of the floor, at least ``min_per_floor``, on the walls of the common rooms by their doors; a floor
with no common room (a stilt floor) hangs them on the walls nearest the foot of the stair starting on it, clear of
the stair's run and headroom, 1.5 m apart (an assumption, listed back), and is asked when it has no stair or no such
wall: ``fitout:extinguisher_room:<level>`` answers a room key, or a place on a wall (``{"near": "stair"}``, or
``{"point": [x, y]}``: the walls nearest that point; see ``b2m.fitout.siting``). Exit signs: above each door of a common room that leads outside, and
facing each stair that starts or ends in it. Every provision gets a record saying what was laid out for it.
"""

from __future__ import annotations

import math

from shapely.geometry import Point as SPoint, Polygon

from b2m.fitout.mepcore import (Ctx, Host, Provision, add, centre_of, common_core, core_points, nearest_spot, spread,
                                wall_spots)
from b2m.fitout import siting
from b2m.fitout.rooms import Room
from b2m.plan import ShaftOp

COMMON = ("lobby", "stair", "parking", "mumty", "lift_machine_room", "meter_room", "pump_room", "guard")
CIRCULATION = ("lobby", "stair", "mumty", "parking")
NOT_LAID_OUT = ("sprinklers", "detection", "pa_system", "fire_lift", "refuge_area", "yard_hydrant", "fire_pump")


def lay_out(ctx: Ctx) -> None:
    fire = ctx.rules.fire
    band = ctx.fire_band
    if band is None:
        ctx.ask("fitout:building_height_m", "fitout_height", "How tall is the building (m, from the average ground "
                "level to the roof terrace)? The plan has no roof terrace to measure to, and the fire provisions "
                "depend on it.")
        return
    for pid in band.provisions:
        prov = fire.provisions[pid]
        if pid == "extinguishers":
            keys = extinguishers(ctx)
            ctx.provisions.append(Provision(id=pid, label=prov.label, status="placed", devices=keys,
                                            note=f"{fire.extinguishers.kind}", source=fire.extinguishers.source))
        elif pid == "exit_signage":
            keys = exit_signs(ctx)
            ctx.provisions.append(Provision(id=pid, label=prov.label, status="placed", devices=keys,
                                            note="exit-way lighting is on the common lighting circuits",
                                            source=prov.source))
        elif pid in ("hose_reel", "manual_alarm"):
            what = "hose_reel" if pid == "hose_reel" else "call_point"
            keys = per_floor(ctx, what)
            ctx.provisions.append(Provision(id=pid, label=prov.label, status="placed", devices=keys,
                                            source=prov.source))
        elif pid in ("wet_riser", "dry_riser"):
            keys, runs = riser(ctx, pid)
            ctx.provisions.append(Provision(id=pid, label=prov.label, status="placed" if runs else "not_laid_out",
                                            devices=keys, runs=runs, source=prov.source,
                                            note=None if runs else "no common core on every storey to carry it"))
        elif pid in ("terrace_tank", "underground_tank"):
            litres = band.terrace_tank_l if pid == "terrace_tank" else band.underground_tank_l
            ctx.provisions.append(Provision(id=pid, label=prov.label, status="sized", source=band.source,
                                            note=(f"{litres:g} L fire reserve, added to the "
                                                  f"{'overhead' if pid == 'terrace_tank' else 'underground'} tank"
                                                  if litres else "capacity not in the rule set: asked")))
            if not litres:
                ctx.ask(f"fitout:{pid}_l", "fitout_fire", f"How many litres must the {prov.label.lower()} hold? The "
                        "rule set's height band gives no capacity.")
        else:
            ctx.provisions.append(Provision(id=pid, label=prov.label, status="not_laid_out", source=prov.source,
                                            note="a system for the fire consultant to design; not laid out by rules"))


def common_rooms(ctx: Ctx, level: str, classes=COMMON) -> list[Room]:
    return sorted((r for r in ctx.fit_rooms if r.level == level and not r.flat
                   and any(c in classes for c in ctx.classes(r))),
                  key=lambda r: (-(r.area_m2 or 0), r.key))


def extinguishers(ctx: Ctx) -> list[str]:
    ex = ctx.rules.fire.extinguishers
    spec = ctx.spec("extinguisher")
    w, h = (spec.size.width_mm, spec.size.height_mm) if spec.size else (200, 600)
    keys = []
    for level in ctx.storeys:
        area = sum(r.area_m2 or 0 for r in ctx.fit_rooms if r.level == level)
        n = max(ex.min_per_floor, math.ceil(area / ex.per_area_m2))
        rooms = common_rooms(ctx, level)
        qid = f"fitout:extinguisher_room:{level}"
        given = ctx.answer(qid)
        if not rooms and isinstance(given, str) and given in ctx.by_key and ctx.by_key[given].level == level:
            rooms = [ctx.by_key[given]]
        if not rooms:
            loc = siting.location(given)
            assumed = loc is None and given is None and any(s.op.base_level == level for s in siting.stairs_of(ctx))
            if assumed:  # a stilt floor with its stair: by the stair, where people pass (listed back)
                loc = siting.Location(near="stair")
            reason = None
            if loc is not None:
                spots, reason = siting.wall_spots_at(ctx, loc, level, w, spec.size.depth_mm if spec.size else 200,
                                                     ex.height_mm - h, ex.height_mm, n=n, min_gap=1500)
                for sp in spots:
                    keys.append(siting.place_on_wall(ctx, "extinguisher", "fire", sp, ex.height_mm - h / 2,
                                                     load=False, rating=ex.kind, source=ex.source,
                                                     note=siting.spot_note(sp, loc) +
                                                     (" (assumed)" if assumed else " (answer)")).key)
                for _ in range(n - len(spots) if spots else 0):
                    ctx.unplace("extinguisher", None, level, "no more wall near the stair, 1.5 m from the other "
                                "extinguishers")
                if spots:
                    if assumed:
                        ctx.assume(qid, f"The {n} extinguishers of {level} (no common room on it) hang on the walls "
                                   "nearest the foot of its stair, clear of the stair's run and headroom (answer a "
                                   "room key or a place on a wall to move them)", {"near": "stair"}, ex.source,
                                   [level])
                    continue
            ctx.ask(qid, "fitout_fire", f"Where do the {n} extinguishers of {level} go? " +
                    (f"The {'assumed' if assumed else 'answered'} place has no wall for them: {reason}. " if reason
                     else "The floor has no common room (lobby, stair, parking) to hang them in. ") +
                    f"Answer a room key, or a place on a wall: {siting.LOCATION_SHAPES}.",
                    options=[r.key for r in ctx.fit_rooms if r.level == level])
            for _ in range(n):
                ctx.unplace("extinguisher", None, level, "no common room on the floor (asked)" if not reason else
                            f"{reason} (asked)")
            continue
        left = n
        for i, room in enumerate(rooms):
            share = left if i == len(rooms) - 1 else max(1, round(n * (room.area_m2 or 0) /
                                                                   sum(r.area_m2 or 0 for r in rooms)))
            share = min(share, left)
            spots = wall_spots(ctx, room, w, ex.height_mm - h, ex.height_mm)
            doors = [o.center for o in room.doors]
            chosen = spread(spots, share, doors or [centre_of(room)], prefer=doors, min_gap=1500)
            for sp in chosen:
                keys.append(ctx.on_wall("extinguisher", "fire", room, sp, ex.height_mm - h / 2, load=False,
                                        rating=ex.kind, source=ex.source).key)
            left -= len(chosen)
            if left <= 0:
                break
        for _ in range(left):
            ctx.unplace("extinguisher", None, level, "no more wall in the common rooms clear of doors and 1.5 m apart")
    return keys


def _exit_targets(ctx: Ctx, room: Room) -> list:
    """Points the room's exit signs mark: doors to the outside, and stairs starting or ending in it."""
    out = []
    shape = room.shape()
    for o in room.doors:
        if o.other_room is not None or o.face is None:
            continue
        f = room.faces[o.face]
        beyond = add(o.center, f.inward, -((f.thickness_mm or 250) + 150))
        shafts = [Polygon(op.points) for op in ctx.plan.ops if isinstance(op, ShaftOp)]
        if not f.outer or any(s.covers(SPoint(beyond)) for s in shafts):
            continue  # into a lift, a duct or another enclosed space, not out of the building
        out.append(o.center)
    for st in ctx.stairs:
        ends = []
        if st.base_level == room.level:
            ends.append(st.runs[0].start)
        if st.top_level == room.level:
            r = st.runs[-1]
            d = (r.end[0] - r.start[0], r.end[1] - r.start[1])
            L = math.hypot(*d) or 1
            ends.append(add(r.start, (d[0] / L, d[1] / L), r.risers * st.tread_mm))
        out += [e for e in ends if shape.buffer(300).covers(SPoint(e))]
    return out


def exit_signs(ctx: Ctx) -> list[str]:
    spec = ctx.spec("exit_sign")
    w, h = (spec.size.width_mm, spec.size.height_mm) if spec.size else (400, 200)
    z = float(ctx.rules.fire.mounting_mm["exit_sign"].value)
    keys = []
    for room in ctx.fit_rooms:
        if room.flat or not any(c in CIRCULATION for c in ctx.classes(room)):
            continue
        for target in _exit_targets(ctx, room):
            spots = wall_spots(ctx, room, w, z - h / 2, z + h / 2, furniture=False)
            sp = nearest_spot(spots, target)
            if sp is None:
                ctx.unplace("exit_sign", room, room.level, "no wall above the doors for the sign")
                continue
            keys.append(ctx.on_wall("exit_sign", "fire", room, sp, z, source=spec.source).key)
    return keys


def per_floor(ctx: Ctx, what: str) -> list[str]:
    spec = ctx.spec(what)
    w, h = (spec.size.width_mm, spec.size.height_mm) if spec.size else (300, 300)
    z = float(ctx.rules.fire.mounting_mm[what].value)
    keys = []
    for level in ctx.storeys:
        rooms = common_rooms(ctx, level, CIRCULATION)
        if not rooms:
            ctx.unplace(what, None, level, "no common lobby or stair on the floor")
            continue
        room = rooms[0]
        target = (_exit_targets(ctx, room) or [centre_of(room)])[0]
        sp = nearest_spot(wall_spots(ctx, room, w, z - h / 2, z + h / 2), target)
        if sp is None:
            ctx.unplace(what, room, level, "no wall space in the common lobby")
            continue
        keys.append(ctx.on_wall(what, "fire", room, sp, z, load=False, source=spec.source).key)
    return keys


def riser(ctx: Ctx, pid: str) -> tuple[list[str], list[str]]:
    core = common_core(ctx)
    if core is None or not ctx.storeys:
        return [], []
    pts = core_points(core, core.centroid.coords[0])
    xy = pts[-1] if len(pts) > 1 else pts[0]  # the corner away from the electrical riser's
    bottom = ctx.elev(ctx.storeys[0]) + 300
    top = ctx.elev(ctx.roof or ctx.storeys[-1]) + 1000
    size = float(ctx.rules.fire.riser_mm.value)
    run = ctx.add_run("pipe", "fire", ctx.storeys[0], [(*xy, bottom), (*xy, top)], size, scope="site", what=pid,
                      source=ctx.rules.fire.riser_mm.source,
                      note="fed from the fire pump / fire tank (not drawn); landing valve per floor")
    keys = []
    z = float(ctx.rules.fire.mounting_mm["landing_valve"].value)
    for level in ctx.storeys:
        room = next((r for r in common_rooms(ctx, level, CIRCULATION) if r.shape().buffer(400).covers(SPoint(xy))),
                    None)
        if room is None:
            ctx.unplace("landing_valve", None, level, "the riser passes no common room on this floor")
            continue
        keys.append(ctx.place("landing_valve", "fire", room, level, xy, z, 270.0,
                              Host(kind="floor", height_mm=z),
                              load=False).key)
    return keys, [run.key] if run else []
