"""The MEP layout (PRD Step 10, F4): plumbing and drainage, electrical, HVAC and fire safety, laid out by rules on the
room model and the furniture layout.

    from b2m.fitout import furniture, mep, rooms, rules
    ruleset = rules.load("west-bengal")
    model = rooms.build(plan, ruleset, answers)
    layout = mep.layout(plan, model, furniture.solve(model, ruleset), ruleset, answers,
                        shaft_kinds=mep.shaft_kinds_from_sources(import_plan.sources))
    layout.devices, layout.runs, layout.circuits         # what F5 builds (see b2m.fitout.mepcore for each record)
    layout.unplaced, layout.unrouted                     # what could not be laid out, with why
    layout.questions, layout.assumptions                 # asked (never guessed) / taken from the rules, listed back
    mep.problems(layout, model, furniture_layout)        # an independent audit; empty when the layout keeps the rules

Why one entry point over four modules: the systems depend on each other in one order. HVAC first (the AC indoor units
and exhaust fans take the high wall spots), then plumbing (geysers, floor traps, stacks, branches, tanks; the fire
band's reserves go into the tanks), then the electrical points (sockets beside the AC units and geysers just placed),
then fire safety (exit signs need power), and last the circuits and conduits over every electrical load. Each module
says why it lays its system out as it does. The layout is deterministic: the same plan, rules and answers give the
same records with the same keys. It is an indicative coordination model (``NOTICE``), not an engineer's design.

``shaft_kinds``: what each shaft op is (duct, lift, stair). A plan's shaft op does not say; a plan made from DWGs knows
it from the drawing (``shaft_kinds_from_sources``), otherwise it is asked (``fitout:shaft_kind:<op key>``).
"""

from __future__ import annotations

import math
from typing import Any, Mapping

from shapely.geometry import LineString, Point as SPoint, Polygon

from b2m.fitout import electrical, fire, hvac, plumbing
from b2m.fitout.furniture import FurnitureLayout
from b2m.fitout.mepcore import DEFAULT_DOOR_HEAD_MM, NOTICE, TEE_GAP_MM, Ctx, MepLayout, Run, add
from b2m.fitout.rooms import RoomModel
from b2m.fitout.rules import FitoutRules, load
from b2m.plan import BuildPlan

__all__ = ["layout", "problems", "shaft_kinds_from_sources", "MepLayout", "NOTICE"]

MAX_POINTS = 200  # create_mep_run's limit
TEE_MIN_GAP_MM = 120.0  # two tees on one piece of main at least this far apart (fittings side by side)


def shaft_kinds_from_sources(sources: Mapping[str, Any]) -> dict[str, str]:
    """Shaft op key -> duct / lift / stair, from an import plan's sources (``b2m.imported.to_plan``: the note says
    "duct shaft", "lift shaft" or "stair opening ...")."""
    out = {}
    for key, src in sources.items():
        note = (getattr(src, "note", None) or (src.get("note") if isinstance(src, dict) else "") or "").lower()
        for kind in ("duct", "lift", "stair"):
            if note.startswith(kind):
                out[key] = kind
    return out


def layout(plan: BuildPlan, model: RoomModel, furniture: FurnitureLayout, rules: FitoutRules,
           answers: Mapping[str, Any] | None = None, shaft_kinds: Mapping[str, str] | None = None) -> MepLayout:
    ctx = Ctx(plan, model, furniture, rules, answers, shaft_kinds)
    ctx.assume("fitout:indicative", NOTICE, True, "PRD Step 10 (decided with the user 2026-11-16)")
    hvac.lay_out(ctx)
    plumbing.lay_out(ctx)
    state = electrical.place(ctx)
    fire.lay_out(ctx)
    electrical.wire(ctx, state)
    return MepLayout(rule_set=rules.id, height_m=ctx.height_m,
                     fire_band_max_m=ctx.fire_band.max_height_m if ctx.fire_band else None,
                     devices=ctx.devices, runs=ctx.runs, circuits=ctx.circuits, unplaced=ctx.unplaced,
                     unrouted=ctx.unrouted, provisions=ctx.provisions, water=ctx.water,
                     questions=sorted(ctx.questions.values(), key=lambda q: q.id),
                     assumptions=sorted(ctx.assumptions.values(), key=lambda a: a.id))


# ------------------------------------------------------------------------------------------------ audit

def _door_boxes(model: RoomModel) -> list[tuple[Polygon, float, float, str]]:
    seen = {}
    for r in model.rooms:
        for o in r.doors:
            if o.key in seen or o.face is None:
                continue
            f = r.faces[o.face]
            t = f.thickness_mm or 250
            box_ = Polygon([o.left, o.right, add(o.right, f.inward, -t), add(o.left, f.inward, -t)])
            z = r.elevation_mm
            seen[o.key] = (box_.buffer(-2), z, z + (o.height_mm or DEFAULT_DOOR_HEAD_MM) + 50, o.key)
    return list(seen.values())


def _tee(main: Run, p) -> tuple[int, float] | None:
    """(segment index, distance along it) where an end at ``p`` tees into ``main``: the segment whose axis is nearest
    (within ``TEE_GAP_MM`` + 50), with at least 50 mm of it on each side; None when there is none."""
    best = None
    for i, (a, b) in enumerate(zip(main.points, main.points[1:])):
        d = [b[k] - a[k] for k in range(3)]
        L = math.sqrt(sum(x * x for x in d))
        if L < 1:
            continue
        t = sum((p[k] - a[k]) * d[k] for k in range(3)) / L
        foot = [a[k] + d[k] * t / L for k in range(3)]
        off = math.dist(foot, p)
        if 50 <= t <= L - 50 and off <= TEE_GAP_MM + 50 and (best is None or off < best[0]):
            best = (off, i, t)
    return (best[1], best[2]) if best else None


def problems(layout: MepLayout, model: RoomModel, furniture: FurnitureLayout) -> list[str]:
    """An audit of a layout against its rooms, independent of how it was laid out (and for the fit-out self-check):
    devices outside their room, runs through a door or beyond create_mep_run's limits, ends naming nothing, fixtures
    not drained or fed, electrical loads on no circuit, circuits on no board."""
    out: list[str] = []
    rules = load(layout.rule_set)
    rooms = model.by_key()
    devices = {d.key: d for d in layout.devices}
    runs = {r.key: r for r in layout.runs}
    fit = {p.key: p for p in furniture.placed}
    for d in layout.devices:
        if d.room_key is None or d.host.kind == "outside_wall":
            continue
        room = rooms.get(d.room_key)
        if room is None or room.polygon is None or not room.shape().buffer(5).covers(SPoint(d.insertion)):
            out.append(f"{d.key}: outside its room")
    doors = _door_boxes(model)
    for r in layout.runs:
        if not 2 <= len(r.points) <= MAX_POINTS:
            out.append(f"{r.key}: {len(r.points)} points (create_mep_run takes 2 to {MAX_POINTS})")
        for end in (r.connect_start, r.connect_end):
            if end is None:
                continue
            if end in runs:
                if r.kind == "conduit":
                    out.append(f"{r.key}: a conduit cannot tee into {end}")
            elif end not in devices and end not in fit:
                out.append(f"{r.key}: joins {end}, which is not in the layout")
        crossed = set()
        for a, b in zip(r.points, r.points[1:]):
            zlo, zhi = min(a[2], b[2]), max(a[2], b[2])
            geom = LineString([a[:2], b[:2]]) if math.dist(a[:2], b[:2]) > 0.5 else SPoint(a[:2])
            for box_, z0, z1, key in doors:
                if zhi > z0 and zlo < z1 and box_.intersects(geom):
                    crossed.add(key)
        for key in sorted(crossed):
            out.append(f"{r.key}: passes through door {key}")
    # tees: beside the middle of a straight piece of the main, 50 mm of it each side, apart from the other tees
    tees: dict[tuple[str, int], list[tuple[float, str]]] = {}
    for r in layout.runs:
        for end, p in ((r.connect_start, r.points[0]), (r.connect_end, r.points[-1])):
            main = runs.get(end) if end else None
            if main is None:
                continue
            hit = _tee(main, p)
            if hit is None:
                out.append(f"{r.key}: its end is not beside the middle of a straight piece of {main.key}")
            else:
                tees.setdefault((main.key, hit[0]), []).append((hit[1], r.key))
    for (mkey, _), at in tees.items():
        at.sort()
        for (t0, k0), (t1, k1) in zip(at, at[1:]):
            if t1 - t0 < TEE_MIN_GAP_MM:
                out.append(f"{k0} and {k1} tee into {mkey} {t1 - t0:.0f} mm apart")
    # fixtures drained and fed
    item_fixture = {f.furniture_item: f for f in rules.plumbing.fixtures.values() if f.furniture_item}
    wet = []
    for p in furniture.placed:
        if p.item in item_fixture:
            room = rooms[p.room_key]
            if any(item_fixture[p.item] is rules.plumbing.fixtures[f] for c in room.classes
                   if c in rules.plumbing.classes for f in rules.plumbing.classes[c].fixtures):
                wet.append((p.key, item_fixture[p.item]))
    wet += [(d.key, rules.plumbing.fixtures["floor_trap"]) for d in layout.devices if d.what == "floor_trap"]
    for key, rule in wet:
        if not any(r.connect_start == key and r.system == rule.drains_to for r in layout.runs):
            out.append(f"{key}: not drained ({rule.drains_to})")
        if rule.cold and not any(r.connect_end == key and r.system == "cold_water" for r in layout.runs):
            out.append(f"{key}: no cold water")
        if rule.hot and not any(r.connect_end == key and r.system == "hot_water" for r in layout.runs):
            out.append(f"{key}: no hot water")
    # every drain reaches a stack, every supply reaches a source
    for r in layout.runs:
        if r.system in ("soil", "waste") and r.connect_end is None and not r.key.split("-")[-2].endswith("stack"):
            out.append(f"{r.key}: a drain that ends at nothing")
    on_circuit = {k for c in layout.circuits for k in c.devices}
    for d in layout.devices:
        if d.load_w is not None and d.key not in on_circuit:
            out.append(f"{d.key}: an electrical load on no circuit")
    for c in layout.circuits:
        if c.panel not in devices or not devices[c.panel].panel:
            out.append(f"{c.key}: its board {c.panel} is not in the layout")
    return out
