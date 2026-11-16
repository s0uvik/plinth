"""Plumbing and drainage by rules (PRD Step 10, F4 "Plumbing and drainage"): stacks in the ducts, branches from every
fixture, cold water from the overhead tank, hot water from geysers, rainwater off the roof, tanks sized from occupancy.

    from b2m.fitout import plumbing
    plumbing.lay_out(ctx)     # ctx: b2m.fitout.mepcore.Ctx; adds devices, runs, unplaced/unrouted, questions

Why this shape: a two-pipe system (NBC 2016 Part 9 Section 2) as Indian apartment blocks are built: soil, waste and vent
stacks stand in the service ducts through every storey and on above the roof as open vents; under each floor's slab
one sloping branch per flat, storey and stack collects that flat's fixtures (the WC to the soil stack, the rest to the
waste stack) and tees into the stack, the branches of one stack arriving at heights ``tee_spacing_mm`` apart. Water
comes down from the overhead tank on the roof terrace: one down-take per duct, and on each storey one branch per flat
at ceiling height (stepping down ``stagger_mm`` per flat on the same down-take), with a drop to every cold fixture and
geyser; each geyser feeds the hot fixtures of its room. Rainwater pipes leave the roof terraces through the parapet and
run down the building's outer face (Indian practice; the ducts carry soil, waste and vent), where no projection below
is in the way. Tanks are sized from the occupancy (persons per flat x litres per head per day, overhead half a day,
underground a day, plus the fire reserves the height band asks for).

How a branch is drawn (a "tree"): from its port (beside the stack or down-take, square to the duct's row of pipes; or
the geyser) a main runs along the port's axis and then square to it to a header line laid parallel to the port axis,
200 mm clear of every fixture it serves; each fixture's stub runs square into the header (moved along it when two would
meet the header within 150 mm). So every tee is beside the middle of a straight piece of main, as create_mep_run needs.
Drains fall at the rule's slope towards the stack; supply runs are level. A fixture farther from every stack than
``max_length_m`` is not drawn: a nearer stack is asked.

What is not drawn: the building drain and inspection chambers outside, vent branches from each trap, the municipal
connection; they are listed in the stacks' notes.
"""

from __future__ import annotations

import math
from collections import defaultdict
from typing import Literal

from shapely.geometry import LineString, Point as SPoint, Polygon, box

from b2m.fitout.furniture import Placed
from b2m.fitout.mepcore import (TEE_GAP_MM, Ctx, Duct, Host, Spot, WaterSizing, add, centre_of, face_point,
                                nearest_spot, pt, spread, wall_spots)
from b2m.fitout.rooms import Room
from b2m.fitout.rules import Fixture, Size

Point = tuple[float, float]
HEADER_CLEAR_MM = 200.0  # a header line keeps this far from the fixtures it serves
HEADER_END_MM = 150.0  # and runs this far past the outermost tee
STUB_GAP_MM = 150.0  # stubs meet a header at least this far apart
HEADER_SHIFTS, HEADER_SHIFT_MM = 4, 300.0  # header lines tried farther out, to keep the run off doors and inside


class Fx:
    """A fixture to plumb: a furniture record (WC, basin, shower, sink) or a floor trap placed here."""

    def __init__(self, key: str, room: Room, fid: str, rule: Fixture, drain: Point, drain_z: float, supply: Point,
                 supply_z: float):
        self.key, self.room, self.fid, self.rule = key, room, fid, rule
        self.drain, self.drain_z, self.supply, self.supply_z = drain, drain_z, supply, supply_z
        self.group = room.flat or room.key
        self.level = room.level


def _facing(p: Placed) -> Point:
    a = math.radians(p.facing_deg)
    return (math.cos(a), math.sin(a))


def lay_out(ctx: Ctx) -> None:
    P = _Plumbing(ctx)
    P.run()


class _Plumbing:
    def __init__(self, ctx: Ctx):
        self.ctx, self.rules = ctx, ctx.rules.plumbing
        self.fixtures: list[Fx] = []
        self.geysers: dict[str, tuple] = {}  # room key -> (device, inlet xy, z, front)
        self.stacks: dict[tuple[str, str], str] = {}  # (duct key, system) -> stack run key
        self.stack_tees: dict[str, list[float]] = defaultdict(list)
        self.downtakes: dict[str, str] = {}  # duct key -> down-take run key
        self.downtake_tees: dict[tuple[str, str], int] = defaultdict(int)  # (duct, level) -> branches so far
        self.cold_trees: list[tuple[Duct, str, str, list]] = []
        self.flats_by_duct: dict[str, set] = defaultdict(set)

    # ---------------------------------------------------------------- run

    def run(self) -> None:
        ctx = self.ctx
        for d in ctx.ducts:
            d.assign([*self.rules.stacks.per_duct, "cold", "rising"])
        self.collect_fixtures()
        self.place_geysers()
        if not ctx.ducts and self.fixtures:
            ctx.ask("fitout:duct", "fitout_duct", "Where do the soil, waste and vent stacks run? The plan has no duct "
                    "(a shaft or a room of class shaft), so the fixtures cannot be drained or fed.",
                    instances=sorted({f.room.key for f in self.fixtures}))
            for f in self.fixtures:
                ctx.unroute(f"{f.fid} drain", "no duct for the stacks (asked)", start=f.key, level=f.level)
        self.stacks_in_ducts()
        self.drainage()
        self.water()
        self.hot_water()
        self.rainwater()

    # ---------------------------------------------------------------- fixtures

    def collect_fixtures(self) -> None:
        ctx, rules = self.ctx, self.rules
        by_item = {f.furniture_item: (fid, f) for fid, f in rules.fixtures.items() if f.furniture_item}
        for room in ctx.fit_rooms:
            classes = [c for c in ctx.classes(room) if c in rules.classes]
            if not classes:
                continue
            wanted = list(dict.fromkeys(f for c in classes for f in rules.classes[c].fixtures))
            z = ctx.elev(room.level)
            placed = ctx.furn_by_room.get(room.key, [])
            for fid in wanted:
                rule = rules.fixtures[fid]
                if rule.furniture_item:
                    items = [p for p in placed if p.item == rule.furniture_item]
                    if not items:
                        why = next((u.reason for u in ctx.furniture.unplaced
                                    if u.room_key == room.key and u.item == rule.furniture_item), None)
                        ctx.unplace(fid, room, room.level, f"{rule.label} not placed by the furniture layout"
                                    + (f": {why}" if why else "") + "; nothing to plumb")
                    for p in items:
                        face = _facing(p)
                        drain = p.center if rule.outlet_from_back_mm is None else \
                            add(p.insertion, face, rule.outlet_from_back_mm)
                        supply = add(p.insertion, face, 100)
                        self.fixtures.append(Fx(p.key, room, fid, rule, pt(drain),
                                                z + max(0.0, p.elevation_mm - 300), pt(supply),
                                                z + (rule.supply_height_mm or 600)))
                elif fid == "floor_trap":
                    self.floor_trap(room, fid, rule)

    def floor_trap(self, room: Room, fid: str, rule: Fixture) -> None:
        ctx = self.ctx
        shape = Polygon(room.polygon, room.holes).buffer(-200, join_style="mitre")
        items = ctx.furn_by_room.get(room.key, [])
        for p in items:
            if p.item != "shower":
                shape = shape.difference(Polygon(p.footprint).buffer(100, join_style="mitre"))
        for o in room.obstacles:
            shape = shape.difference(Polygon(o).buffer(100))
        if shape.is_empty:
            ctx.unplace(fid, room, room.level, "no free floor left for a floor trap (the fixtures fill the room)")
            return
        shower = next((p for p in items if p.item == "shower"), None)
        target = shower.center if shower else centre_of(room)
        if shower is not None:  # in the shower area's corner away from its wall: the wet area drains to it
            target = add(shower.center, _facing(shower), shower.depth_mm / 4)
        from shapely.ops import nearest_points
        q = nearest_points(shape, SPoint(target))[0].coords[0]
        d = ctx.place("floor_trap", "plumbing", room, room.level, q, 0.0, 90.0, Host(kind="floor"),
                      source=rule.source)
        z = ctx.elev(room.level)
        self.fixtures.append(Fx(d.key, room, fid, rule, d.insertion, z, d.insertion, z))

    def place_geysers(self) -> None:
        ctx, g = self.ctx, self.rules.geysers
        spec = ctx.spec("geyser")
        size = spec.size or Size(width_mm=400, depth_mm=400, height_mm=550)
        hot_by_room: dict[str, list[Fx]] = defaultdict(list)
        for f in self.fixtures:
            if f.rule.hot:
                hot_by_room[f.room.key].append(f)
        for room in ctx.fit_rooms:
            litres = [g.litres[c] for c in ctx.classes(room) if c in g.litres]
            hot = hot_by_room.get(room.key, [])
            if not litres or not hot:
                continue
            top = g.bottom_mm + size.height_mm
            if top > ctx.ceiling(room) - 50:
                ctx.unplace("geyser", room, room.level, f"the ceiling ({ctx.ceiling(room):.0f} mm) is too low for a "
                            f"geyser from {g.bottom_mm:g} to {top:g} mm")
                continue
            spots = wall_spots(ctx, room, size.width_mm, g.bottom_mm, top, furniture=False)
            order = sorted(hot, key=lambda f: ({"shower": 0, "kitchen_sink": 1, "basin": 2}.get(f.fid, 3), f.key))
            sp = nearest_spot(spots, order[0].supply)
            if sp is None:
                ctx.unplace("geyser", room, room.level, "no wall space clear of doors and windows at "
                            f"{g.bottom_mm:g}-{top:g} mm for a geyser")
                continue
            vol = max(litres)
            d = ctx.on_wall("geyser", "plumbing", room, sp, g.bottom_mm + size.height_mm / 2, rating=f"{vol:g} L",
                            capacity_l=vol, size=size, source=g.source)
            f = room.faces[sp.face]
            inlet = add(sp.point, f.inward, 150)
            self.geysers[room.key] = (d, pt(inlet), ctx.elev(room.level) + g.bottom_mm, f.inward)

    # ---------------------------------------------------------------- stacks

    def stack_span(self) -> tuple[float, float]:
        ctx, st = self.ctx, self.rules.stacks
        bottom = ctx.elev(ctx.storeys[0]) - st.base_below_floor_mm
        top_level = ctx.roof or ctx.storeys[-1]
        top = ctx.elev(top_level) + (0 if ctx.roof else ctx.storey_height(top_level)) + st.vent_above_roof_mm
        return bottom, top

    def stacks_in_ducts(self) -> None:
        ctx, st = self.ctx, self.rules.stacks
        if not ctx.ducts:
            return
        bottom, top = self.stack_span()
        ctx.assume("fitout:stacks", "Soil, waste and vent stacks stand in every duct from "
                   f"{st.base_below_floor_mm:g} mm "
                   f"under the lowest floor to {st.vent_above_roof_mm:g} mm above the roof (open vents); their feet "
                   "go to inspection chambers outside (not drawn)", [st.base_below_floor_mm, st.vent_above_roof_mm],
                   st.source, [d.key for d in ctx.ducts])
        for d in ctx.ducts:
            for system in st.per_duct:
                if system == "rainwater":
                    continue  # rainwater pipes run down the outer face (rainwater())
                xy = d.slot(system)
                run = ctx.add_run("pipe", system, ctx.storeys[0], [(*xy, bottom), (*xy, top)],
                                  st.sizes_mm.get(system, 100), scope=d.key, what=f"{system}_stack", source=st.source,
                                  note=f"{system} stack in duct {d.key}, open vent above the roof; foot to an "
                                       "inspection chamber outside (not drawn)"
                                       + ("; vent branches from the traps are not drawn" if system == "vent" else ""))
                if run:
                    self.stacks[(d.key, system)] = run.key

    def nearest_duct(self, points: list[Point]) -> Duct | None:
        if not self.ctx.ducts:
            return None
        mx = sum(p[0] for p in points) / len(points)
        my = sum(p[1] for p in points) / len(points)
        return min(self.ctx.ducts, key=lambda d: (abs(d.centre[0] - mx) + abs(d.centre[1] - my), d.key))

    # ---------------------------------------------------------------- trees

    def tree(self, level: str, port: Point, axis: Literal["x", "y"], targets: list[Point], avoid: list = ()):
        """A main from ``port`` (leaving along ``axis``) to a header parallel to ``axis``, and a stub from each target
        square into the header. Returns (main points from the port, stubs from each target to beside the header,
        tee points on the header) or None when it cannot be laid; a single target gets no header."""
        u = 0 if axis == "x" else 1
        v = 1 - u

        def P(a: float, b: float) -> Point:
            return pt((a, b) if u == 0 else (b, a))

        if len(targets) == 1:
            t = targets[0]
            mid = P(t[u], port[v])
            return [port, mid, t], [], []
        options = []
        vs = [t[v] for t in targets]
        cands = sorted([c for k in range(HEADER_SHIFTS) for c in (min(vs) - HEADER_CLEAR_MM - k * HEADER_SHIFT_MM,
                                                                  max(vs) + HEADER_CLEAR_MM + k * HEADER_SHIFT_MM)],
                       key=lambda c: (abs(c - port[v]), c))
        for c in cands:
            if abs(c - port[v]) < 100:
                c += math.copysign(HEADER_CLEAR_MM, c - (sum(vs) / len(vs)))
            arrive: list[float] = []
            stubs = []
            for t in sorted(targets, key=lambda t: (t[u], t[v])):
                a = t[u]
                k = 0
                while any(abs(a - b) < STUB_GAP_MM for b in arrive):
                    k += 1
                    a = t[u] + (STUB_GAP_MM * ((k + 1) // 2)) * (1 if k % 2 else -1)
                arrive.append(a)
                side = 1 if t[v] > c else -1
                end = P(a, c + side * TEE_GAP_MM)
                stubs.append((t, [t, P(a, t[v]), end], P(a, c)))
            lo, hi = min(arrive) - HEADER_END_MM, max(arrive) + HEADER_END_MM
            near, far = (lo, hi) if abs(port[u] - lo) <= abs(port[u] - hi) else (hi, lo)
            if abs(port[u] - near) < 100:
                near += math.copysign(HEADER_END_MM, near - far)
            main = [port, P(near, port[v]), P(near, c), P(far, c)]
            stub_map = {id(t): s for t, s, _ in stubs}
            ordered = [stub_map[id(t)] for t in targets]
            tees = [next(p for t2, _, p in stubs if t2 is t) for t in targets]
            inside = self.ctx.inside(level, main) and all(self.ctx.inside(level, s) for s in ordered)
            crossing = sum(1 for path in [main, *ordered] for a, b in zip(path, path[1:])
                           for box_ in avoid if box_.intersects(LineString([a, b])))
            options.append(((not inside, crossing, self.length(main)), main, ordered, tees))
        options.sort(key=lambda o: o[0])
        _, main, stubs, tees = options[0]
        return main, stubs, tees

    def door_boxes(self, level: str | None, below_z: float | None = None) -> list:
        """Plan boxes of a storey's doors (whose heads reach above ``below_z``): runs at that height keep off them."""
        if level is None:
            return []
        return [b for b, z0, z1, _ in self.ctx.doors(level) if below_z is None or z1 > below_z]

    @staticmethod
    def length(path: list[Point]) -> float:
        return sum(math.dist(a, b) for a, b in zip(path, path[1:]))

    # ---------------------------------------------------------------- drainage

    def below(self, level: str) -> str | None:
        """The storey under a level (its ceiling holds the level's drains)."""
        i = self.ctx.storeys.index(level)
        return self.ctx.storeys[i - 1] if i > 0 else None

    def drainage(self) -> None:
        ctx, br = self.ctx, self.rules.branches
        groups: dict[tuple, list[Fx]] = defaultdict(list)
        for f in self.fixtures:
            groups[(ctx.elev(f.level), f.level, f.group, f.rule.drains_to)].append(f)
        ctx.assume("fitout:branches", f"Drains fall at {br.slope_pct} % (soil, waste) under the slab, starting "
                   f"{br.depth_below_floor_mm:g} mm below the floor; one branch per flat, storey and stack",
                   br.slope_pct, br.source)
        for (_, level, group, system), fxs in sorted(groups.items(), key=lambda kv: kv[0]):
            duct = self.nearest_duct([f.drain for f in fxs])
            stack = self.stacks.get((duct.key, system)) if duct else None
            if duct is None or stack is None:
                for f in fxs:
                    ctx.unroute(f"{f.fid} drain", f"no {system} stack in a duct to drain to", start=f.key, level=level)
                continue
            self.drain_tree(level, group, system, duct, stack, fxs)

    def drain_tree(self, level: str, group: str, system: str, duct: Duct, stack: str, fxs: list[Fx]) -> None:
        ctx, br = self.ctx, self.rules.branches
        sxy = duct.slot(system)
        mean = (sum(f.drain[0] for f in fxs) / len(fxs), sum(f.drain[1] for f in fxs) / len(fxs))
        port, n = duct.approach(sxy, mean)
        axis = "y" if duct.long == "x" else "x"
        far = [f for f in fxs if abs(f.drain[0] - port[0]) + abs(f.drain[1] - port[1]) > br.max_length_m * 1000]
        for f in far:
            ctx.ask(f"fitout:stack_near:{f.room.key}", "fitout_stack", f"Where does \"{f.room.name}\" on {level} "
                    f"drain? Its fixtures are more than {br.max_length_m:g} m from the nearest stack (duct "
                    f"{duct.key}); a duct or an outside stack near it is needed.", instances=[f.room.key])
            ctx.unroute(f"{f.fid} drain", f"more than {br.max_length_m:g} m from the nearest {system} stack "
                        "(a nearer stack is asked)", start=f.key, end=stack, level=level)
        fxs = [f for f in fxs if f not in far]
        if not fxs:
            return
        laid = self.tree(level, port, axis, [f.drain for f in fxs], self.door_boxes(self.below(level)))
        main, stubs, tees = laid
        slope = br.slope_pct[system] / 100
        floor = ctx.elev(level)
        z_top = floor - br.depth_below_floor_mm
        stub_drop = max((slope * self.length(s) for s in stubs), default=0.0)
        rev = list(reversed(main))  # header end first, the port (beside the stack) last
        L = self.length(rev)
        z_start = z_top - stub_drop - 10
        used = self.stack_tees[stack]
        while any(abs((z_start - slope * L) - z) < br.tee_spacing_mm for z in used):
            z_start -= 50
        z_end = z_start - slope * L
        if z_end < floor - br.max_depth_below_floor_mm:
            for f in fxs:
                ctx.unroute(f"{f.fid} drain", f"the branch would reach the stack {floor - z_end:.0f} mm below the "
                            f"floor (more than {br.max_depth_below_floor_mm:g}): too far or too many branches",
                            start=f.key, end=stack, level=level)
            return
        used.append(z_end)
        size = max(f.rule.outlet_mm for f in fxs)
        if system == "soil":
            size = max(size, self.rules.stacks.sizes_mm.get("soil", 100))
        zs, acc = [], 0.0
        for i, p in enumerate(rev):
            if i:
                acc += math.dist(rev[i - 1], p)
            zs.append(z_start - slope * acc)
        pts3 = [(*p, z) for p, z in zip(rev, zs)]
        flat = fxs[0].room.flat
        if len(fxs) == 1:
            f = fxs[0]
            pts3 = [(*f.drain, f.drain_z), *pts3]  # down from the outlet, then the branch (rev starts at it)
            ctx.add_run("pipe", system, level, pts3, f.rule.outlet_mm if system != "soil" else size, scope=group,
                        what=f"{system}_branch", start=f.key, end=stack, slope=br.slope_pct[system],
                        room=f.room.key, flat=flat, source=br.source, note=self.cross_note(pts3))
            return
        header = ctx.add_run("pipe", system, level, pts3, size, scope=group, what=f"{system}_branch", end=stack,
                             slope=br.slope_pct[system], flat=flat, source=br.source,
                             note="; ".join(x for x in ("closed end: a cleaning eye", self.cross_note(pts3)) if x))
        if header is None:
            return
        for f, stub, tee in zip(fxs, stubs, tees):
            s_at = math.dist(rev[0], tee)
            z_tee = z_start - slope * s_at
            sl = self.length(stub)
            z_gap = z_tee + slope * TEE_GAP_MM
            z_first = z_gap + slope * (sl - TEE_GAP_MM)
            acc, pts = 0.0, []
            for i, p in enumerate(stub):
                if i:
                    acc += math.dist(stub[i - 1], p)
                pts.append((*p, z_first - slope * acc))
            pts = [(*f.drain, f.drain_z), *pts]
            ctx.add_run("pipe", system, level, pts, f.rule.outlet_mm, scope=f.room.key, what=f"{f.fid}_drain",
                        start=f.key, end=header.key, slope=br.slope_pct[system], room=f.room.key, flat=flat,
                        source=f.rule.source)

    def cross_note(self, pts) -> str | None:
        doors = self.ctx.crossings(pts)
        return f"passes door(s) {', '.join(doors)}" if doors else None

    # ---------------------------------------------------------------- water

    def water(self) -> None:
        ctx, w, tanks, sup = self.ctx, self.rules.water, self.rules.tanks, self.rules.supply
        ppf = w.persons_per_flat
        given = ctx.answer("fitout:persons_per_flat")
        try:
            if given is not None and float(given) > 0:
                ppf = float(given)
        except (TypeError, ValueError):
            pass
        flats = sorted({(r.level, r.flat) for r in ctx.model.rooms if r.flat})
        persons = len(flats) * ppf
        ctx.assume("fitout:persons_per_flat", f"{ppf:g} persons per flat ({len(flats)} flats, {persons:g} persons) "
                   f"at {w.lpcd:g} litres per head per day size the tanks", ppf,
                   w.source if given is None else "answer")
        demand = persons * w.lpcd
        band = ctx.fire_band
        fire_t = band.terrace_tank_l if band and "terrace_tank" in band.provisions and band.terrace_tank_l else 0.0
        fire_u = band.underground_tank_l if band and "underground_tank" in band.provisions and \
            band.underground_tank_l else 0.0
        ugt_l = math.ceil(demand * tanks.underground_days / 100) * 100
        # cold targets by (duct, level, group)
        targets: dict[tuple, list] = defaultdict(list)
        for f in self.fixtures:
            if f.rule.cold:
                targets[(f.level, f.group)].append(("fix", f))
        for room_key, (dev, inlet, z, _) in self.geysers.items():
            room = ctx.by_key[room_key]
            targets[(room.level, room.flat or room.key)].append(("geyser", (dev, inlet, z, room)))
        by_duct: dict[str, list] = defaultdict(list)
        for (level, group), items in sorted(targets.items(), key=lambda kv: (ctx.elev(kv[0][0]), kv[0])):
            pts = [it[1].supply if it[0] == "fix" else it[1][1] for it in items]
            d = self.nearest_duct(pts)
            if d is None:
                continue
            by_duct[d.key].append((level, group, items))
            for it in items:
                room = it[1].room if it[0] == "fix" else it[1][3]
                if room.flat:
                    self.flats_by_duct[d.key].add((room.level, room.flat))
        terraces = ctx.roof_terraces()
        oht: dict[str, tuple] = {}  # duct key -> (tank device, terrace)
        overhead: dict[str, float] = {}
        if not terraces:
            for dk in by_duct:
                ctx.unplace("overhead_tank", None, None, "no roof terrace to stand on (the roof level is asked)")
        else:
            serve: dict[str, list[Duct]] = defaultdict(list)
            for d in ctx.ducts:
                if d.key not in by_duct:
                    continue
                t = min(terraces, key=lambda t: (Polygon(t.polygon).distance(SPoint(d.centre)), t.key))
                serve[t.key].append(d)
            first = True
            for t in terraces:
                ducts = serve.get(t.key)
                if not ducts:
                    continue
                n_flats = len(set().union(*(self.flats_by_duct[d.key] for d in ducts)))
                litres = math.ceil(n_flats * ppf * w.lpcd * tanks.overhead_days / 100) * 100
                extra = fire_t if first else 0.0
                dev = self.place_tank(t, "overhead_tank", litres + extra, [d.centre for d in ducts],
                                      note=(f"includes the fire reserve of {fire_t:g} L" if extra else None))
                first = False
                if dev is None:
                    continue
                overhead[dev.key] = litres
                for d in ducts:
                    oht[d.key] = (dev, t)
        ctx.water = WaterSizing(persons_per_flat=ppf, flats=len(flats), persons=persons, demand_lpd=demand,
                                overhead_l=overhead, underground_l=ugt_l, fire_terrace_l=fire_t,
                                fire_underground_l=fire_u, source=f"{w.source}; {tanks.source}")
        # down-takes, then a branch per flat and storey
        for d in ctx.ducts:
            groups = by_duct.get(d.key)
            if not groups:
                continue
            if d.key not in oht:
                for level, group, items in groups:
                    for kind, it in items:
                        ctx.unroute("cold water", "no overhead tank to feed the down-take",
                                    end=it.key if kind == "fix" else it[0].key, level=level)
                continue
            tank, terrace = oht[d.key]
            plan: list[tuple] = []
            for level, group, items in groups:
                k = self.downtake_tees[(d.key, level)]
                self.downtake_tees[(d.key, level)] += 1
                rooms = [it.room if kind == "fix" else it[3] for kind, it in items]
                ceiling = min(ctx.ceiling(r) for r in rooms)
                z = ctx.elev(level) + ceiling - sup.below_ceiling_mm - k * sup.stagger_mm
                plan.append((level, group, items, z))
            bottom = min(p[3] for p in plan) - 300
            run = self.downtake(d, tank, terrace, bottom)
            if run is None:
                continue
            for level, group, items, z in plan:
                self.cold_tree(d, run.key, level, group, items, z)
        self.underground(ugt_l + fire_u, fire_u, oht)

    def place_tank(self, terrace: Room, what: str, litres: float, near: list[Point], note: str | None = None):
        ctx, tanks = self.ctx, self.rules.tanks
        side = math.ceil(math.sqrt(litres / 1000 / (tanks.water_depth_mm / 1000)) * 10) * 100
        height = tanks.water_depth_mm + tanks.freeboard_mm
        free = Polygon(terrace.polygon).buffer(-tanks.clear_mm, join_style="mitre")
        for h in [*terrace.holes, *(d.outline for d in ctx.ducts)]:  # off the ducts and their vent pipes
            free = free.difference(Polygon(h).buffer(tanks.clear_mm, join_style="mitre"))
        best = None
        if not free.is_empty:
            minx, miny, maxx, maxy = free.bounds
            x = minx + side / 2
            while x <= maxx - side / 2 + 1e-6:
                y = miny + side / 2
                while y <= maxy - side / 2 + 1e-6:
                    if free.covers(box(x - side / 2, y - side / 2, x + side / 2, y + side / 2)):
                        score = sum(abs(x - q[0]) + abs(y - q[1]) for q in near)
                        if best is None or (score, x, y) < best[0]:
                            best = ((score, x, y), (x, y))
                    y += 250
                x += 250
        if best is None:
            ctx.unplace(what, terrace, terrace.level, f"no {side:g} x {side:g} mm space on the terrace clear of its "
                        f"parapet and ducts by {tanks.clear_mm:g} mm for a {litres:g} L tank")
            ctx.ask(f"fitout:{what}", "fitout_tank", f"Where does the {litres:g} L overhead tank go? It does not fit "
                    "on "
                    f"\"{terrace.name}\" ({side:g} x {side:g} mm plus access).", instances=[terrace.key])
            return None
        c = best[1]
        return ctx.place(what, "plumbing", terrace, terrace.level, c, 0.0, 270.0, Host(kind="floor"),
                         size=Size(width_mm=side, depth_mm=side, height_mm=height), rating=f"{litres:g} L",
                         capacity_l=litres, source=tanks.source, note=note)

    def downtake(self, d: Duct, tank, terrace: Room, bottom: float):
        ctx, sup = self.ctx, self.rules.supply
        roof = ctx.elev(terrace.level)
        dxy = d.slot("cold")
        half = tank.size.width_mm / 2
        c = tank.insertion
        dx, dy = dxy[0] - c[0], dxy[1] - c[1]
        out = (math.copysign(1, dx), 0.0) if abs(dx) >= abs(dy) else (0.0, math.copysign(1, dy))
        o = add(c, out, half)
        z = roof + 150
        shape = Polygon(terrace.polygon).buffer(5)
        path = None
        for first in ("x", "y") if out[0] else ("y", "x"):  # leave the tank square to its side first
            cand = [o, (dxy[0], o[1]) if first == "x" else (o[0], dxy[1]), dxy]
            if shape.covers(LineString(cand)):
                path = cand
                break
        path = path or [o, (dxy[0], o[1]), dxy]
        pts = [(*p, z) for p in path] + [(*dxy, bottom)]
        return ctx.add_run("pipe", "cold_water", terrace.level, pts, sup.sizes_mm.get("downtake", 50), scope=d.key,
                           what="cold_downtake", start=tank.key, source=sup.source,
                           note=f"from {tank.key} down duct {d.key}; capped at its foot")

    def cold_tree(self, d: Duct, downtake: str, level: str, group: str, items: list, z: float) -> None:
        ctx, sup = self.ctx, self.rules.supply
        dxy = d.slot("cold")
        pts_t = [it.supply if kind == "fix" else it[1] for kind, it in items]
        ends = [(it.supply_z if kind == "fix" else it[2]) for kind, it in items]
        keys = [it.key if kind == "fix" else it[0].key for kind, it in items]
        rooms = [it.room if kind == "fix" else it[3] for kind, it in items]
        mean = (sum(p[0] for p in pts_t) / len(pts_t), sum(p[1] for p in pts_t) / len(pts_t))
        port, _ = d.approach(dxy, mean)
        axis = "y" if d.long == "x" else "x"
        main, stubs, tees = self.tree(level, port, axis, pts_t, self.door_boxes(level, z))
        flat = rooms[0].flat
        if len(items) == 1:
            pts = [(*p, z) for p in main] + [(*pts_t[0], ends[0])]
            ctx.add_run("pipe", "cold_water", level, pts, sup.sizes_mm.get("fixture", 15), scope=group,
                        what="cold_branch", start=downtake, end=keys[0], room=rooms[0].key, flat=flat,
                        source=sup.source, note=self.cross_note(pts))
            return
        pts = [(*p, z) for p in main]
        header = ctx.add_run("pipe", "cold_water", level, pts, sup.sizes_mm.get("branch", 25), scope=group,
                             what="cold_branch", start=downtake, flat=flat, source=sup.source,
                             note="capped at its far end; " + (self.cross_note(pts) or "").strip("; "))
        if header is None:
            return
        for stub, end_z, key, room in zip(stubs, ends, keys, rooms):
            path = list(reversed(stub))
            pts = [(*p, z) for p in path] + [(*path[-1], end_z)]
            ctx.add_run("pipe", "cold_water", level, pts, sup.sizes_mm.get("fixture", 15), scope=room.key,
                        what="cold_drop", start=header.key, end=key, room=room.key, flat=flat, source=sup.source)

    def hot_water(self) -> None:
        ctx, sup = self.ctx, self.rules.supply
        for room_key, (dev, inlet, z, front) in sorted(self.geysers.items()):
            room = ctx.by_key[room_key]
            hot = [f for f in self.fixtures if f.room.key == room_key and f.rule.hot]
            axis = "x" if abs(front[0]) >= abs(front[1]) else "y"
            port = pt(inlet)
            main, stubs, tees = self.tree(room.level, port, axis, [f.supply for f in hot],
                                          self.door_boxes(room.level, z))
            if len(hot) == 1:
                pts = [(*p, z) for p in main] + [(*hot[0].supply, hot[0].supply_z)]
                ctx.add_run("pipe", "hot_water", room.level, pts, sup.sizes_mm.get("fixture", 15), scope=room.key,
                            what="hot_branch", start=dev.key, end=hot[0].key, room=room.key, flat=room.flat,
                            source=sup.source)
                continue
            pts = [(*p, z) for p in main]
            header = ctx.add_run("pipe", "hot_water", room.level, pts, sup.sizes_mm.get("hot", 20), scope=room.key,
                                 what="hot_branch", start=dev.key, room=room.key, flat=room.flat, source=sup.source,
                                 note="capped at its far end")
            if header is None:
                continue
            for f, stub in zip(hot, stubs):
                path = list(reversed(stub))
                pts = [(*p, z) for p in path] + [(*path[-1], f.supply_z)]
                ctx.add_run("pipe", "hot_water", room.level, pts, sup.sizes_mm.get("fixture", 15), scope=room.key,
                            what="hot_drop", start=header.key, end=f.key, room=room.key, flat=room.flat,
                            source=sup.source)

    def underground(self, litres: float, fire_l: float, oht: dict) -> None:
        """The underground tank where the user puts it, its pump (in a pump room, else beside the tank) and the rising
        main up a duct to each overhead tank."""
        ctx, tanks, sup = self.ctx, self.rules.tanks, self.rules.supply
        ground = ctx.storeys[0]
        z0 = ctx.elev(ground)
        given = ctx.answer("fitout:underground_tank")
        side = math.ceil(math.sqrt(litres / 1000 / (tanks.water_depth_mm / 1000)) * 10) * 100
        height = tanks.water_depth_mm + tanks.freeboard_mm
        note = f"includes the fire reserve of {fire_l:g} L" if fire_l else None
        if not (isinstance(given, (list, tuple)) and len(given) == 2):
            ctx.ask("fitout:underground_tank", "fitout_tank", f"Where does the {litres:g} L underground water tank "
                    f"(about {side:g} x {side:g} mm, {height:g} mm deep) go? Give its centre as a plan point [x, y] in "
                    "mm, on the site clear of the footings; the plan has no site or pump room to put it in.")
            ctx.unplace("underground_tank", None, ground, f"{litres:g} L sized from the occupancy; its place on the "
                        "site is asked")
            ctx.unplace("pump", None, ground, "stands at the underground tank, whose place is asked")
            ctx.unroute("rising main", "from the pump at the underground tank (asked) to the overhead tank(s)")
            return
        c = (float(given[0]), float(given[1]))
        ugt = ctx.place("underground_tank", "plumbing", None, ground, c, -height, 270.0, Host(kind="floor"),
                        scope="site", size=Size(width_mm=side, depth_mm=side, height_mm=height),
                        rating=f"{litres:g} L", capacity_l=litres, source=tanks.source, note=note)
        pump_room = next(iter(ctx.rooms_of("pump_room", level=ground)), None)
        if pump_room is not None:
            pxy, room = centre_of(pump_room), pump_room
        else:
            pxy, room = add(c, (1.0, 0.0), side / 2 + 600), None
        pump = ctx.place("pump", "plumbing", room, ground, pxy, 0.0, 270.0, Host(kind="floor"),
                         scope=room.key if room else "site", source=ctx.spec("pump").source,
                         note=f"draws from {ugt.key}")
        if not oht:
            ctx.unroute("rising main", "no overhead tank placed", start=pump.key)
            return
        tanks_ = {}
        for dk, (dev, terrace) in oht.items():
            tanks_.setdefault(dev.key, (dev, terrace, dk))
        duct = min(ctx.ducts, key=lambda d: (abs(d.centre[0] - pxy[0]) + abs(d.centre[1] - pxy[1]), d.key))
        rxy = duct.slot("rising")
        z = z0 + 300
        main_tank, terrace, _ = next(iter(sorted(tanks_.values(), key=lambda t: (
            abs(t[0].insertion[0] - rxy[0]) + abs(t[0].insertion[1] - rxy[1]), t[0].key))))
        roof = ctx.elev(terrace.level)
        top = roof + main_tank.size.height_mm + 150
        pts = [(*pxy, z0 + 300), (rxy[0], pxy[1], z), (*rxy, z), (*rxy, top), (main_tank.insertion[0], rxy[1], top),
               (*main_tank.insertion, top)]
        run = ctx.add_run("pipe", "cold_water", ground, pts, sup.sizes_mm.get("rising_main", 40), scope="site",
                          what="rising_main", start=pump.key, end=main_tank.key, source=sup.source,
                          note=f"up duct {duct.key}; enters the tank from above")
        for dev, terrace, _ in tanks_.values():
            if dev.key == main_tank.key or run is None:
                continue
            port, _ = duct.approach(rxy, dev.insertion)
            zt = roof + 150
            pts = [(*port, zt), (port[0], dev.insertion[1], zt), (*dev.insertion, zt)] if duct.long == "x" else \
                [(*port, zt), (dev.insertion[0], port[1], zt), (*dev.insertion, zt)]
            pts = pts[:-1] + [(*pts[-1][:2], roof + dev.size.height_mm + 150)]
            ctx.add_run("pipe", "cold_water", terrace.level, pts, sup.sizes_mm.get("rising_main", 40), scope="site",
                        what="rising_main", start=run.key, end=dev.key, source=sup.source)

    # ---------------------------------------------------------------- rainwater

    def rainwater(self) -> None:
        ctx, st = self.ctx, self.rules.stacks
        terraces = ctx.roof_terraces()
        if not terraces:
            return
        roof = ctx.elev(ctx.roof)
        ground = ctx.elev(ctx.storeys[0])
        below = [s for s in ctx.storeys if ctx.elev(s) < roof]
        slabs = [Polygon(op.points) for op in ctx.ops_floor if ctx.elev(op.level) < roof]
        tanks = [Polygon(_tank_box(d)) for d in ctx.devices if d.what == "overhead_tank"]
        size = st.sizes_mm.get("rainwater", 100)
        ctx.assume("fitout:rainwater", f"One {size:g} mm rainwater pipe per {st.rainwater_roof_m2_per_pipe:g} m² of "
                   "roof terrace, through the parapet and down the outer face of the building to a storm drain "
                   "(not drawn), where no projection below is in the way", st.rainwater_roof_m2_per_pipe, st.source)
        for t in terraces:
            n = math.ceil((t.area_m2 or 0) / st.rainwater_roof_m2_per_pipe)
            spots: list[Spot] = []
            for sp in wall_spots(ctx, t, 300, 0, 300, step=250, furniture=False):
                f = t.faces[sp.face]
                if not f.outer:
                    continue
                q = face_point(f, sp.s, -((f.thickness_mm or 250) + size / 2 + 50))
                o = face_point(f, sp.s, 200)
                if any(ctx.footprint(s).covers(SPoint(q)) for s in below) or any(s.covers(SPoint(q)) for s in slabs):
                    continue
                if any(tk.buffer(300).covers(SPoint(o)) for tk in tanks):
                    continue
                spots.append(sp)
            chosen = spread(spots, n, [centre_of(t)], min_gap=2000)
            for sp in chosen:
                f = t.faces[sp.face]
                o = face_point(f, sp.s, 200)
                q = face_point(f, sp.s, -((f.thickness_mm or 250) + size / 2 + 50))
                dev = ctx.place("roof_outlet", "plumbing", t, t.level, o, 0.0, f.inward, Host(kind="floor"),
                                source=st.source)
                ctx.add_run("pipe", "rainwater", t.level, [(*o, roof), (*q, roof), (*q, ground)], size, scope=t.key,
                            what="rainwater_pipe", start=dev.key, source=st.source,
                            note="down the outer face; foot to a storm drain or recharge pit (not drawn)")
            for _ in range(n - len(chosen)):
                ctx.unplace("roof_outlet", t, t.level, "no more place on the parapet whose outside face runs clear "
                            f"to the ground (projections below): {len(chosen)} of {n} rainwater pipes placed")


def _tank_box(d) -> list[Point]:
    h = (d.size.width_mm if d.size else 1000) / 2
    x, y = d.insertion
    return [(x - h, y - h), (x + h, y - h), (x + h, y + h), (x - h, y + h)]
