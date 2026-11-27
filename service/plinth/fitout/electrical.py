"""Electrical by rules (PRD Step 10, F4 "Electrical"): points per room, switchboards, distribution boards, the meter
board, circuits and conduits.

    from plinth.fitout import electrical
    state = electrical.place(ctx)    # lights, fans, switchboards, sockets, TV/data, DBs, meter board
    electrical.wire(ctx, state)      # circuits per DB and conduits (after the fire devices, which need power too)

Why so: the points per room come from the rule set (NBC 2016 Part 8 Section 2 practice: a count, or one per so many
m²; a room of several classes, a living/dining/kitchen, takes the most of each kind of point its classes ask for, and
the sum of their 16 A points, which serve different appliances). Lights and fans hang from the ceiling (the slab's
underside) on a grid across the room, lights moved off the fans; each light is the fitting its room class takes
(``electrical.light_kinds``: a batten in a room, a downlight in a toilet), so F1/F5 match a family of the right kind and
size. A room open to the sky (a roof terrace, ``Ctx.outdoors``) has no ceiling: no fan, and its lights are wall
bulkheads on the walls round it, at ``mounting_mm.bulkhead_light`` on the stair/lift room walls and
``mounting_mm.parapet_light`` on the parapet's inner face, spread round it. One switchboard per room at its main door
(the door to a common area or the outside, else the first), on the latch side at ``switchboard.height_mm``, so it is at
hand as the door opens (decided with the user). An open room's switch is indoors: at the door that opens onto it, on
the indoor room's side; with no such door in the plan (the G+4 sample's terraces: the stair room's door opens onto its
landing), in the indoor common room beside it (stair or mumty first, sharing the longest wall), by its door (an
assumption listed back). Sockets spread along the walls away from the door, first beside the bed's
side table, the TV unit, the study table, never behind tall furniture or in a door or window; a 16 A point is the AC's
(beside the indoor unit: a room with several answered AC units gets one per unit), the geyser's (beside it) or an
appliance's (above the kitchen counter). One distribution board per flat beside its entrance door (hinge side, the
switchboard having the latch side).

The meter board stands on the lowest storey in its meter room, or where ``fitout:meter_room`` puts it: a room key, or
a place on a wall when no room holds it (``{"near": "stair"}``: the wall nearest the foot of the stair, under it
where it fits, clear of its run and headroom; ``{"point": [x, y], "level": "<name>"}``: the wall nearest that point;
see ``plinth.fitout.siting``); without either it is asked, and asked again when the answered place has no wall for it.
The common board for the lobbies, stairs, terraces and pumps stands where ``fitout:common_board`` says (a room key,
``"meter_board"``: beside the meter board, or a place on a wall), else in the lowest common lobby or stair (an
assumption), else beside the meter board (an assumption). The electrical riser starts at the meter board.

Circuits (per board, IS 732 / NBC practice): lights, fans and exhaust fans up to ``lighting_max_points`` or
``lighting_max_w``; 6 A sockets likewise; other 16 A sockets two to a circuit; an AC or a geyser (``dedicated``) on a
circuit of its own with its socket; a pump alone. Conduits are simplified for coordination (``wiring``): in the slab
over the room and dropping in the wall, one per point: board -> each room's switchboard -> its lights, fans and 6 A
sockets; board -> each 16 A socket; the rising main up the common core from the meter board, and a feeder from it to
every board. Conduits cannot tee (Revit), so each ends at a device or at nothing (leaving the riser).
"""

from __future__ import annotations

import math
from collections import defaultdict

from plinth.fitout.mepcore import (Circuit, Ctx, Device, Host, add, ceiling_points, centre_of, common_core, core_points,
                                nearest_spot, r1, spread, sub, unit, wall_spots)
from plinth.fitout import siting
from plinth.fitout.rooms import Opening, Room

COMMON_DB_CLASSES = ("lobby", "stair")
AC_CLASSES_USE = "ac"


class State:
    def __init__(self):
        self.switchboards: dict[str, Device] = {}  # room key -> its switchboard (an open room's: in a room beside)
        self.open_rooms: list[Room] = []  # lit rooms open to the sky: switched from indoors (after every room)
        self.use: dict[str, str] = {}  # 16 A socket key -> ac / geyser / appliance
        self.appliance: dict[str, str] = {}  # socket key -> the AC or geyser it feeds
        self.dbs: dict[tuple, Device] = {}  # (level, flat) -> DB; (None, None) the common board
        self.meter: Device | None = None
        self.riser: tuple | None = None  # (xy, run key)


def place(ctx: Ctx) -> State:
    st = State()
    for room in ctx.fit_rooms:
        points(ctx, st, room)
    for room in st.open_rooms:
        outdoor_switch(ctx, st, room)
    boards(ctx, st)
    return st


# ------------------------------------------------------------------------------------------------ points

def _counts(ctx: Ctx, room: Room) -> dict[str, int]:
    rules = ctx.rules.electrical.points
    area = room.area_m2 or 0
    out = defaultdict(int)
    for c in ctx.classes(room):
        if c not in rules:
            continue
        p = rules[c]
        for k in ("lights", "fans", "sockets_6a", "tv", "data"):
            out[k] = max(out[k], getattr(p, k).number(area))
        out[f"16a:{c}"] += p.sockets_16a.number(area)
    return out


def main_door(ctx: Ctx, room: Room) -> Opening | None:
    doors = [o for o in room.doors if o.face is not None]
    if not doors:
        return None

    def rank(o: Opening):
        other = ctx.by_key.get(o.other_room) if o.other_room else None
        outward = other is None or other.flat != room.flat or \
            any(c in ("lobby", "living") for c in ctx.classes(other) if other.key in ctx.room_classes) or \
            any(c in ("lobby", "living") for c in other.classes)
        return (0 if outward else 1, o.key)

    return min(doors, key=rank)


def points(ctx: Ctx, st: State, room: Room) -> None:
    el = ctx.rules.electrical
    counts = _counts(ctx, room)
    if not counts:
        return
    src = "; ".join(el.points[c].source for c in ctx.classes(room) if c in el.points)
    kind = ctx.light_kind(room)
    if ctx.outdoors(room):
        if counts["fans"]:
            ctx.unplace("fan", room, room.level, "open to the sky: no ceiling for a fan")
        wall_lights(ctx, room, counts["lights"], kind, src)
        if counts["lights"]:
            st.open_rooms.append(room)
    else:
        ceiling = ctx.ceiling(room)
        host = Host(kind="ceiling", height_mm=r1(ceiling))
        fans = ceiling_points(room, counts["fans"])
        blade = float(el.mounting_mm["fan_blade_min"].value)
        for p in fans:
            note = None if ceiling - 300 >= blade else f"blades below {blade:g} mm under a {ceiling:.0f} mm ceiling"
            ctx.place("fan", "power", room, room.level, p, ceiling, 270.0, host, source=src, note=note)
        spec = ctx.spec(kind or "light")
        minx, miny, maxx, maxy = room.shape().bounds
        along_y = spec.size is not None and spec.size.width_mm > 2 * spec.size.depth_mm and maxy - miny > maxx - minx
        for p in ceiling_points(room, counts["lights"], avoid=fans):
            ctx.place("light", "lighting", room, room.level, p, ceiling, 0.0 if along_y else 270.0, host,
                      source=src, kind=kind)  # a linear fitting along the room's long side
        # the switchboard at the main door's latch side
        door = main_door(ctx, room)
        if door is None:
            ctx.unplace("switchboard", room, room.level, "the room has no door to put its switchboard by")
        else:
            sw = switchboard_at(ctx, room, door)
            if sw is not None:
                st.switchboards[room.key] = sw
    # 16 A points by use
    mount = {k: float(v.value) for k, v in el.mounting_mm.items()}
    n_ac = len(ctx.in_room_devices(room.key, "ac_indoor"))  # a large room's answered units: a socket each
    ac_key = next((k for k in sorted(counts) if k.startswith("16a:") and k[4:] in ctx.rules.hvac.split_ac.classes),
                  None)
    if ac_key is not None:
        counts[ac_key] = max(counts[ac_key], n_ac)
    for key, n in sorted(counts.items()):
        if not key.startswith("16a:") or n <= 0:
            continue
        cls = key[4:]
        for _ in range(n):
            socket16(ctx, st, room, cls, mount)
    # 6 A sockets spread round the walls
    n6 = counts["sockets_6a"]
    if n6:
        h = mount["socket_kitchen"] if ctx.classes(room)[0] == "kitchen" else mount["socket_6a"]
        spots = wall_spots(ctx, room, 100, h - 50, h + 50)
        prefer = [p.insertion for p in ctx.furn_by_room.get(room.key, [])
                  if p.item in ("side_table", "tv_unit", "study_table", "sofa", "dressing_table")]
        doors = [o.center for o in room.doors]
        chosen = spread(spots, n6, doors or [centre_of(room)], prefer)
        for sp in chosen:
            ctx.on_wall("socket_6a", "power", room, sp, h, rating="6 A", source=src)
        for _ in range(n6 - len(chosen)):
            ctx.unplace("socket_6a", room, room.level, "no more wall clear of doors, windows and tall furniture, "
                        "600 mm from the other sockets")
    # TV and data outlets
    tv_unit = next((p for p in ctx.furn_by_room.get(room.key, []) if p.item == "tv_unit"), None)
    desk = next((p for p in ctx.furn_by_room.get(room.key, []) if p.item == "study_table"), None)
    for what, n, h, near in (("tv_outlet", counts["tv"], mount["tv_point"], tv_unit),
                             ("data_outlet", counts["data"], mount["data_point"], desk or tv_unit)):
        for _ in range(n):
            spots = wall_spots(ctx, room, 100, h - 50, h + 50)
            sp = nearest_spot(spots, near.insertion if near else centre_of(room))
            if sp is None:
                ctx.unplace(what, room, room.level, "no wall space for it")
                continue
            ctx.on_wall(what, "data", room, sp, h, source=src)


def switchboard_at(ctx: Ctx, room: Room, door: Opening, note: str | None = None) -> Device | None:
    """A switchboard in ``room`` beside ``door`` (its view of the door): on the latch side, ``from_jamb_mm`` off the
    jamb, at ``switchboard.height_mm``; else the nearest wall space (said in its note); None (unplaced) without one."""
    sb = ctx.rules.electrical.switchboard
    spec = ctx.spec("switchboard")
    w = spec.size.width_mm if spec.size else 250
    spots = wall_spots(ctx, room, w, sb.height_mm - 100, sb.height_mm + 100, faces=[door.face], furniture=False)
    jambs = [door.latch, door.hinge] if door.latch else [door.left, door.right]
    sp = None
    for j in jambs:
        away = unit(sub(j, door.center))
        target = add(j, away, sb.from_jamb_mm + w / 2)
        cand = nearest_spot(spots, target)
        if cand is not None and math.dist(cand.point, target) <= 300:
            sp = cand
            break
    if sp is None:
        sp = nearest_spot(wall_spots(ctx, room, w, sb.height_mm - 100, sb.height_mm + 100, furniture=False),
                          door.center)
    if sp is None:
        ctx.unplace("switchboard", room, room.level, f"no wall space at {sb.height_mm:g} mm near door {door.key}")
        return None
    where = None if math.dist(sp.point, jambs[0]) <= sb.from_jamb_mm + w / 2 + 300 else \
        f"not at the latch side of {door.key} (no wall there)"
    note = "; ".join(x for x in (note, where) if x) or None
    return ctx.on_wall("switchboard", "lighting", room, sp, sb.height_mm, load=False, source=sb.source, note=note)


def wall_lights(ctx: Ctx, room: Room, n: int, kind: str | None, src: str) -> None:
    """``n`` lights on the walls of a room open to the sky, spread round it: on walls tall enough at
    ``mounting_mm.bulkhead_light`` (the stair/lift room walls facing a terrace), on parapets at
    ``mounting_mm.parapet_light``."""
    if n <= 0:
        return
    mount = ctx.rules.electrical.mounting_mm
    spec = ctx.spec(kind or "light")
    w, h = (spec.size.width_mm, spec.size.height_mm) if spec.size else (250.0, 150.0)
    tall_z, low_z = float(mount["bulkhead_light"].value), float(mount["parapet_light"].value)
    tall, low = [], []
    for fi, f in enumerate(room.faces):
        if f.kind != "wall":
            continue
        top = ctx.wall_top(f.wall_key, room.level) or room.height_mm or 0.0
        if top >= tall_z + h / 2 + 100:
            tall.append(fi)
        elif top >= low_z + h / 2 + 50:
            low.append(fi)
    at = {}
    for faces, z in ((tall, tall_z), (low, low_z)):
        if faces:
            for sp in wall_spots(ctx, room, w, z - h / 2, z + h / 2, faces=faces, furniture=False):
                at[id(sp)] = (sp, z)
    chosen = spread([sp for sp, _ in at.values()], n, [centre_of(room)], min_gap=2000)
    for sp in chosen:
        z = at[id(sp)][1]
        ctx.on_wall("light", "lighting", room, sp, z, kind=kind, source=src,
                    note="on the wall: open to the sky, no ceiling" + (" (the parapet)" if z == low_z else ""))
    for _ in range(n - len(chosen)):
        ctx.unplace("light", room, room.level, "open to the sky (no ceiling) and no more wall or parapet for a "
                    "wall light")


def outdoor_switch(ctx: Ctx, st: State, room: Room) -> None:
    """The switchboard of a room open to the sky, indoors: in the indoor room whose door opens onto it, beside that
    door; else (the plan has no such door) in the indoor room beside it on its level, a stair or mumty first, sharing
    the longest wall, by that room's main door (assumed)."""
    names = {room.key, *room.shared_with}
    level = [r for r in ctx.fit_rooms if r.level == room.level and r.key != room.key and not ctx.outdoors(r)]
    onto = sorted(((r.key, o.key, r, o) for r in level for o in r.doors
                   if o.face is not None and o.other_room in names), key=lambda x: (x[0], x[1]))
    if onto:
        _, _, r, door = onto[0]
        sw = switchboard_at(ctx, r, door, note=f"switches the lights of {room.name} ({room.key}): at the door onto it, "
                                               "indoors")
    else:
        walls = {f.wall_key for f in room.faces if f.kind == "wall"}
        best = None
        for r in level:
            shared = sum(f.length_mm for f in r.faces if f.kind == "wall" and f.wall_key in walls)
            door = main_door(ctx, r)
            if shared <= 0 or door is None:
                continue
            core = 0 if any(c in ("stair", "mumty", "lobby") for c in ctx.classes(r)) else 1
            key = (core, -round(shared), r.key)
            if best is None or key < best[0]:
                best = (key, r, door)
        if best is None:
            ctx.unplace("switchboard", room, room.level, "open to the sky with no door onto it from an indoor room "
                        "and no indoor room beside it to switch its lights from")
            return
        _, r, door = best
        sw = switchboard_at(ctx, r, door, note=f"switches the lights of {room.name} ({room.key}): the plan has no "
                                               f"door onto it, so by the door of {r.name} beside it (assumed)")
        if sw is not None:
            ctx.assume(f"fitout:outdoor_switch:{room.key}", f"{room.name} ({room.key}) is open to the sky and the plan "
                       f"has no door onto it: its lights are switched from {r.name} ({r.key}) beside it, by that "
                       "room's door", r.key, ctx.rules.electrical.switchboard.source, [room.key])
    if sw is not None:
        st.switchboards[room.key] = sw


def socket16(ctx: Ctx, st: State, room: Room, cls: str, mount: dict[str, float]) -> None:
    el = ctx.rules.electrical
    src = el.points[cls].source
    if cls in ctx.rules.hvac.split_ac.classes:
        use, h = "ac", mount["socket_ac"]
        app = [d for d in ctx.in_room_devices(room.key, "ac_indoor") if d.key not in st.appliance.values()]
    elif cls in ctx.rules.plumbing.geysers.litres and cls == "toilet":
        use, h = "geyser", mount["socket_geyser"]
        app = [d for d in ctx.in_room_devices(room.key, "geyser") if d.key not in st.appliance.values()]
    else:
        use, h = "appliance", mount["socket_16a"] if "kitchen" not in ctx.classes(room) else mount["socket_kitchen"]
        app = []
    spots = wall_spots(ctx, room, 100, h - 50, h + 50)
    if app:
        target = app[0].insertion
    elif use == "ac":
        outer = [sp for sp in spots if room.faces[sp.face].outer]
        spots = outer or spots
        target = centre_of(room)
    else:
        near = [p for p in ctx.furn_by_room.get(room.key, [])
                if p.item in ("fridge", "counter", "washing_machine")]
        used = [d.insertion for d in ctx.in_room_devices(room.key, "socket_16a")]
        target = next((p.insertion for p in near if all(math.dist(p.insertion, u) > 900 for u in used)),
                      centre_of(room))
    used = [d.insertion for d in ctx.in_room_devices(room.key, "socket_16a")]
    sp = nearest_spot(spots, target, used, 600)
    if sp is None:
        ctx.unplace("socket_16a", room, room.level, f"no wall space at {h:g} mm for the {use} socket")
        return
    d = ctx.on_wall("socket_16a", "power", room, sp, h, rating="16 A", source=src,
                    note={"ac": "for the split AC", "geyser": "for the geyser"}.get(use, "power socket"))
    st.use[d.key] = use
    if app:
        st.appliance[d.key] = app[0].key


# ------------------------------------------------------------------------------------------------ boards

def boards(ctx: Ctx, st: State) -> None:
    el = ctx.rules.electrical
    db = el.distribution_board
    spec = ctx.spec("distribution_board")
    w = spec.size.width_mm if spec.size else 400
    flats: dict[tuple, list[Room]] = defaultdict(list)
    for r in ctx.fit_rooms:
        if r.flat:
            flats[(r.level, r.flat)].append(r)
    for (level, flat), rooms in sorted(flats.items(), key=lambda kv: (ctx.elev(kv[0][0]), kv[0])):
        entries = [(r, o) for r in rooms for o in r.doors if o.face is not None and
                   (o.other_room is None or ctx.by_key[o.other_room].flat != flat)]
        entries.sort(key=lambda ro: (0 if "living" in ctx.classes(ro[0]) else 1, ro[0].key, ro[1].key))
        placed = None
        for room, door in entries:
            spots = wall_spots(ctx, room, w, db.height_mm - 250, db.height_mm + 250, faces=[door.face])
            jambs = [door.hinge, door.latch] if door.hinge else [door.right, door.left]
            for j in jambs:
                target = add(j, unit(sub(j, door.center)), 150 + w / 2)
                sp = nearest_spot(spots, target)
                if sp is not None and math.dist(sp.point, target) <= 600:
                    placed = ctx.on_wall("distribution_board", "power", room, sp, db.height_mm, panel=True,
                                         load=False, source=db.source, note=f"flat {flat}, {level}: by door {door.key}")
                    break
            if placed:
                break
        if placed is None:
            room = next((r for r in rooms if "living" in ctx.classes(r)), rooms[0])
            sp = nearest_spot(wall_spots(ctx, room, w, db.height_mm - 250, db.height_mm + 250),
                              entries[0][1].center if entries else centre_of(room))
            if sp is not None:
                placed = ctx.on_wall("distribution_board", "power", room, sp, db.height_mm, panel=True, load=False,
                                     source=db.source, note=f"flat {flat}, {level}: not beside its entrance (no wall)")
        if placed is None:
            ctx.unplace("distribution_board", None, level, f"flat {flat}: no wall space for its board")
        else:
            st.dbs[(level, flat)] = placed
    meter_board(ctx, st)
    if db.common_board:
        common_board(ctx, st, w)


def meter_board(ctx: Ctx, st: State) -> None:
    """The meter board on the lowest storey: in its meter room, or where ``fitout:meter_room`` says (a room key or a
    location: ``siting.LOCATION_SHAPES``); else asked."""
    el = ctx.rules.electrical
    ground = ctx.storeys[0]
    spec = ctx.spec("meter_board")
    h = float(el.mounting_mm["meter_board"].value)
    size = spec.size
    width = size.width_mm if size else 1500
    bh = size.height_mm if size else 900
    meter_rooms = ctx.rooms_of("meter_room", level=ground)
    given = ctx.answer("fitout:meter_room")
    loc = siting.location(given)
    if isinstance(given, str) and given in ctx.by_key and ctx.by_key[given] in ctx.fit_rooms:
        meter_rooms = [ctx.by_key[given]]
    reason = None
    if loc is not None:
        level = loc.level if loc.level in ctx.levels else ground
        spots, reason = siting.wall_spots_at(ctx, loc, level, width, size.depth_mm if size else 250, h - bh / 2,
                                             h + bh / 2)
        if spots:
            sp = spots[0]
            st.meter = siting.place_on_wall(ctx, "meter_board", "power", sp, h, panel=True, load=False,
                                            source=el.meter_room.source,
                                            note=f"{el.meter_room.value}; {siting.spot_note(sp, loc)} (answer)")
            return
    elif meter_rooms:
        room = meter_rooms[0]
        sp = nearest_spot(wall_spots(ctx, room, width, h - bh / 2, h + bh / 2), centre_of(room))
        if sp is None:
            ctx.unplace("meter_board", room, ground, "no wall in the meter room long enough for the board")
        else:
            st.meter = ctx.on_wall("meter_board", "power", room, sp, h, panel=True, load=False,
                                   source=el.meter_room.source, note=el.meter_room.value)
        return
    ctx.ask("fitout:meter_room", "fitout_meter_room", f"Where on {ground} do the electricity meters go "
            f"({el.meter_room.value})? " + (f"The answered place has no wall for the board: {reason}. " if reason
                                             else "The plan has no meter room there. ") +
            f"Answer a room key, or a place on a wall: {siting.LOCATION_SHAPES}.",
            options=[r.key for r in ctx.fit_rooms if r.level == ground])
    ctx.unplace("meter_board", None, ground, "no meter room on the lowest storey (asked)" if not reason else
                f"{reason} (asked)")


def common_board(ctx: Ctx, st: State, w: float) -> None:
    """The common areas' board: where ``fitout:common_board`` says (a room key; "meter_board": beside the meter board;
    a location), else in the lowest common lobby or stair (an assumption), else beside the meter board."""
    db = ctx.rules.electrical.distribution_board
    given = ctx.answer("fitout:common_board")
    note = "common board: lobbies, stairs, terraces, pumps"
    lobbies = sorted((r for r in ctx.fit_rooms if not r.flat and any(c in COMMON_DB_CLASSES for c in ctx.classes(r))),
                     key=lambda r: (ctx.elev(r.level), r.key))
    if isinstance(given, str) and given in ctx.by_key and ctx.by_key[given] in ctx.fit_rooms:
        lobbies = [ctx.by_key[given]]
    with_meter = given in ("meter_board", "with_meter_board")
    loc = siting.location(given)
    if not with_meter and loc is None:
        for room in lobbies:
            sp = nearest_spot(wall_spots(ctx, room, w, db.height_mm - 250, db.height_mm + 250), centre_of(room))
            if sp is None:
                continue
            st.dbs[(None, None)] = ctx.on_wall("distribution_board", "power", room, sp, db.height_mm, panel=True,
                                               load=False, scope=room.key, source=db.source, note=note)
            if given is None:
                ctx.assume("fitout:common_board", f"The common areas' board stands in {room.name} on {room.level} "
                           "(the lowest common lobby or stair; answer a room key, \"meter_board\" or a place on a "
                           "wall to move it)", room.key, db.source, [room.key])
            return
    spec = ctx.spec("distribution_board")
    depth = spec.size.depth_mm if spec.size else 100
    level = loc.level if loc and loc.level in ctx.levels else (st.meter.level if st.meter else ctx.storeys[0])
    if loc is None and st.meter is not None:  # beside the meter board, on its wall when there is room
        loc = siting.Location(point=st.meter.insertion, level=st.meter.level)
    if loc is None:
        ctx.unplace("distribution_board", None, None, "common board: no common lobby or stair to put it in, and no "
                    "meter board to put it beside")
        return
    spots, reason = siting.wall_spots_at(ctx, loc, level, w, depth, db.height_mm - 250, db.height_mm + 250)
    if not spots:
        ctx.unplace("distribution_board", None, level, f"common board: {reason}")
        return
    sp = spots[0]
    where = f"beside the meter board {st.meter.key}" if (with_meter or given is None) and st.meter else         siting.spot_note(sp, loc)
    st.dbs[(None, None)] = siting.place_on_wall(ctx, "distribution_board", "power", sp, db.height_mm, panel=True,
                                                load=False, scope=sp.room.key if sp.room else level, source=db.source,
                                                note=f"{note}; {where}")
    if given is None:
        ctx.assume("fitout:common_board", f"The common areas' board stands beside the meter board (no common lobby "
                   "or stair to put it in)", "meter_board", db.source)


# ------------------------------------------------------------------------------------------------ circuits, conduits

def panel_of(st: State, d: Device) -> Device | None:
    if d.flat is not None:
        return st.dbs.get((d.level, d.flat))
    return st.dbs.get((None, None))


def wire(ctx: Ctx, st: State) -> None:
    circuits(ctx, st)
    conduits(ctx, st)


def circuits(ctx: Ctx, st: State) -> None:
    c = ctx.rules.electrical.circuits
    loads: dict[str, list[Device]] = defaultdict(list)
    for d in ctx.devices:
        if d.load_w is None:
            continue
        p = panel_of(st, d)
        if p is None:
            ctx.unroute(d.what, "no distribution board for its flat or the common areas: not on a circuit",
                        end=d.key, level=d.level)
            continue
        loads[p.key].append(d)
    for panel_key in sorted(loads, key=lambda k: [d.key for d in ctx.devices].index(k)):
        devs = loads[panel_key]
        n = 0

        def add_circuit(kind: str, group: list[Device], rating: int, load: float | None = None):
            nonlocal n
            n += 1
            ctx.circuits.append(Circuit(key=f"CKT-{panel_key}-{n}", panel=panel_key, kind=kind,
                                        devices=[d.key for d in group],
                                        load_w=r1(load if load is not None else sum(d.load_w or 0 for d in group)),
                                        rating_a=rating, source=c.source))

        def greedy(kind: str, items: list[Device], max_n: int, max_w: float | None, rating: int):
            group: list[Device] = []
            for d in items:
                if group and (len(group) >= max_n or (max_w and sum(x.load_w for x in group) + d.load_w > max_w)):
                    add_circuit(kind, group, rating)
                    group = []
                group.append(d)
            if group:
                add_circuit(kind, group, rating)

        lighting = [d for d in devs if d.what in ("light", "fan", "exhaust_fan", "exit_sign")]
        sockets = [d for d in devs if d.what == "socket_6a"]
        appliances = {d.key: d for d in devs if d.what in ("ac_indoor", "geyser")}
        dedicated_sockets = [d for d in devs if d.what == "socket_16a" and st.use.get(d.key) in c.dedicated]
        power = [d for d in devs if d.what == "socket_16a" and d not in dedicated_sockets]
        greedy("lighting", lighting, c.lighting_max_points, c.lighting_max_w, 10)
        greedy("sockets", sockets, c.socket_max_points, c.lighting_max_w, 10)
        greedy("power", power, c.power_16a_max_outlets, None, 20)
        fed = set()
        for s in dedicated_sockets:
            app = appliances.get(st.appliance.get(s.key, ""))
            group = [s] + ([app] if app else [])
            fed.update(d.key for d in group)
            add_circuit("dedicated", group, 20, load=app.load_w if app else s.load_w)
        for d in devs:
            if d.key in fed or d in lighting or d in sockets or d in power or d in dedicated_sockets:
                continue
            add_circuit("dedicated", [d], 20 if d.what != "pump" else 16)


def slab_z(ctx: Ctx, level: str, room: Room | None = None) -> float:
    """Height of the conduit zone: inside the slab over the storey."""
    rooms = [room] if room else [r for r in ctx.fit_rooms if r.level == level]
    h = min((r.height_mm or ctx.storey_height(level)) for r in rooms) if rooms else ctx.storey_height(level)
    t = ctx.slab_above(rooms[0]) if rooms else 150
    return ctx.elev(level) + h - t / 2


def device_point(ctx: Ctx, d: Device) -> tuple[float, float, float]:
    z = ctx.elev(d.level) + d.elevation_mm
    return (*d.insertion, z)


def conduit_path(ctx: Ctx, a: Device, b: Device, via: tuple | None) -> list:
    """From device a to device b in the slab zone: up from a, along the axes, down to b; across storeys by ``via``
    (the riser's plan point)."""
    pa, pb = device_point(ctx, a), device_point(ctx, b)
    za = slab_z(ctx, a.level, ctx.by_key.get(a.room_key))
    zb = slab_z(ctx, b.level, ctx.by_key.get(b.room_key))
    if b.host.kind == "ceiling":
        pb = (*b.insertion, zb)
    best = None
    for first in ("x", "y"):
        if a.level == b.level:
            mid = (pb[0], pa[1]) if first == "x" else (pa[0], pb[1])
            path = [pa, (*pa[:2], za), (*mid, za), (*pb[:2], zb), pb]
            ok = ctx.inside(a.level, [pa[:2], mid, pb[:2]])
        else:
            v = via or pa[:2]
            m1 = (v[0], pa[1]) if first == "x" else (pa[0], v[1])
            m2 = (pb[0], v[1]) if first == "x" else (v[0], pb[1])
            path = [pa, (*pa[:2], za), (*m1, za), (*v, za), (*v, zb), (*m2, zb), (*pb[:2], zb), pb]
            ok = ctx.inside(a.level, [pa[:2], m1, v]) and ctx.inside(b.level, [v, m2, pb[:2]])
        if best is None or (ok and not best[0]):
            best = (ok, path)
        if ok:
            break
    return best[1]


def conduits(ctx: Ctx, st: State) -> None:
    el = ctx.rules.electrical
    wr = el.wiring
    riser_xy = None
    core = common_core(ctx)
    lowest = ctx.storeys[0]
    if core is None:
        ctx.unroute("electrical riser", "no common lobby or stair on every storey with flats to carry the riser",
                    start=st.meter.key if st.meter else None)
    else:
        near = st.meter.insertion if st.meter else core.centroid.coords[0]
        riser_xy = core_points(core, near)[0]
        top_flat = max((s for s in ctx.storeys if any(r.flat for r in ctx.model.on_level(s))),
                       key=ctx.elev, default=ctx.storeys[-1])
        z_top = ctx.elev(top_flat) + wr.riser_above_top_floor_mm
        if st.meter:
            pm = device_point(ctx, st.meter)
            zc = slab_z(ctx, st.meter.level, ctx.by_key.get(st.meter.room_key))
            pts = [pm, (*pm[:2], zc), (riser_xy[0], pm[1], zc), (*riser_xy, zc), (*riser_xy, z_top)]
            start = st.meter.key
        else:
            pts = [(*riser_xy, ctx.elev(lowest) + 300), (*riser_xy, z_top)]
            start = None
            ctx.unroute("electrical riser: meter board end", "the meter room is asked; the riser starts at the "
                        "lowest storey", level=lowest)
        run = ctx.add_run("conduit", "power", lowest, pts, wr.riser_mm, scope="site", what="riser", start=start,
                          source=wr.source, note="rising main up the common core; feeders leave it on each storey")
        st.riser = (riser_xy, run.key if run else None)
        for (level, flat), db in sorted(st.dbs.items(), key=lambda kv: (kv[1].key,)):
            pd = device_point(ctx, db)
            z = slab_z(ctx, db.level, ctx.by_key.get(db.room_key))
            best = None
            for first in ("x", "y"):
                mid = (pd[0], riser_xy[1]) if first == "x" else (riser_xy[0], pd[1])
                ok = ctx.inside(db.level, [riser_xy, mid, pd[:2]])
                path = [(*riser_xy, z), (*mid, z), (*pd[:2], z), pd]
                if best is None or (ok and not best[0]):
                    best = (ok, path)
                if ok:
                    break
            ctx.add_run("conduit", "power", db.level, best[1], wr.feeder_mm, scope=db.key, what="feeder",
                        end=db.key, room=db.room_key, flat=db.flat, source=wr.source,
                        note="from the riser (a conduit cannot tee: the cables leave the riser here)")
    for room in ctx.fit_rooms:
        devs = [d for d in ctx.in_room_devices(room.key) if d.load_w is not None or d.what == "switchboard"]
        if not devs:
            continue
        sb = st.switchboards.get(room.key)
        for d in devs:
            panel = panel_of(st, d)
            if panel is None:
                continue
            if d.what == "switchboard":
                src = panel
            elif d.what in ("light", "fan", "exhaust_fan", "socket_6a", "exit_sign") and sb is not None:
                src = sb
            elif d.what in ("ac_indoor", "geyser") and d.key in st.appliance.values():
                src = next(ctx.device(k) for k, v in st.appliance.items() if v == d.key)
            else:
                src = panel
            path = conduit_path(ctx, src, d, riser_xy)
            ctx.add_run("conduit", "lighting" if d.system == "lighting" or d.what in ("fan", "exhaust_fan")
                        else "power", d.level, path, wr.conduit_mm, scope=room.key, what="conduit", start=src.key,
                        end=d.key, room=room.key, flat=room.flat, source=wr.source,
                        note=_cross(ctx, path))
    for d in [d for d in ctx.devices if d.load_w is not None and d.room_key is None]:
        panel = panel_of(st, d)
        if panel is not None:
            path = conduit_path(ctx, panel, d, riser_xy)
            ctx.add_run("conduit", "power", d.level, path, wr.conduit_mm, scope=d.level, what="conduit",
                        start=panel.key, end=d.key, source=wr.source)


def _cross(ctx: Ctx, path) -> str | None:
    doors = ctx.crossings(path)
    return f"passes door(s) {', '.join(doors)}" if doors else None
