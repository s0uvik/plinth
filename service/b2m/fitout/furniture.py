"""Furniture layout by rules (PRD Step 10, F3): the standard set per room class, placed in each room's polygon.

    from b2m.fitout import furniture, rooms, rules
    layout = furniture.solve(rooms.build(plan, ruleset, answers), ruleset)
    layout.placed      # one record per item: key, room, level, item, category + keywords, size, position, rotation, wall
    layout.unplaced    # every item that did not fit, with why (never dropped silently, never forced)
    layout.skipped     # rooms not furnished, with why (not enclosed, no class yet, furnished with the room it shares)

Why a solver and not templates: the office has no furniture drawings, and rooms come in any shape from a brief or a
DWG. Each item is put where the rule set says (back on a wall, head of a bed on a wall, beside or opposite another
item, free-standing, a counter run along walls) at the best position that keeps every rule; when none does, the next
smaller standard size is tried, and when no size fits the item is listed with the reasons its positions failed. It is
deterministic: the same rooms and rules give the same layout (identical rooms on typical storeys are solved once).

What a position must keep, item by item in priority order:
- inside the room, off the columns standing in it;
- out of every door leaf's swing (+ ``door_swing_clearance_mm``) and the approach in front of each door on the side it
  does not swing into (door width x ``door_approach_mm``; both sides of a sliding door);
- tall items (``tall``, or higher than the window's sill when not set) out of the zone in front of each window (window
  width x ``window_zone_mm``);
- no overlap with an item placed before, nor with the clearance it keeps (front, sides, all round); its own clearance
  inside the room and free of items (a bed needs ``sides_required`` of its two sides free, a dining set its chairs'
  space all round, clear of door swings);
- circulation: after it is placed, every door of the room still reaches every other through a passage at least
  ``circulation_mm`` wide, and every item's use space (its front clearance) stays reachable from the doors.
Preferences (``prefer``) only rank the positions that keep all of that, earlier preferences weighing more.

Search: greedy first (each item at its best valid position, in priority order). When that leaves an item out, each
earlier item is tried at its next-best valid places (distinct ones, up to ``ALTERNATIVES``) with the rest redone
greedily, and a layout placing more (required items before optional ones, then larger sizes) is kept, within
``MAX_RUNS`` layouts per room. So a WC moves along the wall to leave the shower its corner, a bed shifts to leave
its side table room; a room where nothing fits everything still gets its best layout and the list of what is left
out, each with the rules its positions broke in the final layout.

Records (``Placed``), for F5 (placing families) and F4 (MEP on top of the furniture):
- ``key`` "FIT-<room key>-<item>-<n>", stable for the same plan and rules (a counter's legs are its items 1 and 2).
- ``insertion``: the middle of the item's back edge (the edge on the wall), on the room's level (``elevation_mm``
  above it: 0, or the counter top for a sink or hob); ``center`` and ``footprint`` (anticlockwise corners) as well.
- ``facing_deg``: the direction the item's front faces, anticlockwise from +X; ``rotation_deg`` = facing - 270: the
  rotation to give a family drawn the Revit way, its front towards -Y (the "Front" view), about its insertion point.
- ``wall_key`` / ``wall_side``: the wall its back stands on and which side of it (as the room's faces); none for a
  free-standing item. ``host``: the counter leg a sink or hob sits on.
"""

from __future__ import annotations

import json
import math
from collections import Counter
from typing import Literal

from pydantic import BaseModel, ConfigDict
from shapely import prepared
from shapely.geometry import Point as SPoint, Polygon
from shapely.ops import unary_union

from b2m.fitout.rooms import Face, Room, RoomModel
from b2m.fitout.rules import FitoutRules, FurnitureItem, SetEntry, Size

Point = tuple[float, float]
AREA_TOL = 1.0  # mm²: touching is not overlapping
BACKABLE = ("wall", "wall_end")
FREE_STEP_MM = 150.0  # free-standing items: centres tried on this grid
LENGTH_STEP_MM = 300.0  # counter legs: lengths tried at this step
ACCESS_SHARE = 0.25  # an item's use space is reachable when this share of it touches the passage from the doors
MAX_RUNS = 60  # greedy layouts tried per room when the first leaves something out
ALTERNATIVES = 10  # next-best valid positions (distinct places) tried per item
PASSAGE_TOL_MM = 5.0  # a passage exactly circulation_mm wide counts


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class Placed(_Model):
    key: str
    room_key: str
    level: str
    item: str
    label: str
    category: str
    keywords: list[str]
    width_mm: float  # along its back wall (a counter leg: its length)
    depth_mm: float
    height_mm: float
    size_index: int  # 0: the preferred size; n: the rule's n-th smaller standard size
    insertion: Point  # middle of the back edge
    center: Point
    footprint: list[Point]
    facing_deg: float
    rotation_deg: float
    elevation_mm: float = 0
    wall_key: str | None = None
    wall_side: Literal["left", "right"] | None = None
    host: str | None = None  # a counter fixture: the leg it sits on
    source: str


class Unplaced(_Model):
    room_key: str
    level: str
    item: str
    label: str
    optional: bool
    status: Literal["no_fit", "anchor_missing", "not_needed"]
    reason: str


class Skipped(_Model):
    room_key: str
    level: str
    name: str
    reason: str


class FurnitureLayout(_Model):
    rule_set: str
    placed: list[Placed] = []
    unplaced: list[Unplaced] = []
    skipped: list[Skipped] = []

    def in_room(self, room_key: str) -> list[Placed]:
        return [p for p in self.placed if p.room_key == room_key]


# ------------------------------------------------------------------------------------------------ geometry

def _add(a: Point, b: Point, k: float = 1.0) -> Point:
    return (a[0] + b[0] * k, a[1] + b[1] * k)


def _r(p: Point) -> Point:
    return (round(p[0], 1), round(p[1], 1))


def _rect(p0: Point, along: Point, inward: Point, width: float, depth: float) -> Polygon:
    """Anticlockwise rectangle with its back edge from p0 along ``along`` (width), extending ``inward`` (depth)."""
    p1 = _add(p0, along, width)
    return Polygon([p0, p1, _add(p1, inward, depth), _add(p0, inward, depth)])


def _overlap(a, b) -> bool:
    return a.intersects(b) and a.intersection(b).area > AREA_TOL


def _deg(v: Point) -> float:
    return round(math.degrees(math.atan2(v[1], v[0])) % 360, 3) % 360


class _Cand:
    """A candidate position: body and clearance zones, with where it stands (for the record and the score)."""

    def __init__(self, body: Polygon, along: Point, inward: Point, back: Point, face: int | None, s: float,
                 width: float, depth: float, zones: dict[str, list[Polygon]], score: float):
        self.body, self.along, self.inward, self.back = body, along, inward, back
        self.face, self.s, self.width, self.depth = face, s, width, depth
        self.zones, self.score = zones, score


class _Result:
    def __init__(self, choices: list, forced: dict[int, int], objective: tuple, complete: bool):
        self.choices, self.forced, self.objective, self.complete = choices, forced, objective, complete


class _Item:
    """A placed item, kept while solving the room."""

    def __init__(self, key: str, item_id: str, item: FurnitureItem, size: Size, size_index: int, cand: _Cand):
        self.key, self.item_id, self.item, self.size, self.size_index, self.cand = key, item_id, item, size, size_index, cand


# ------------------------------------------------------------------------------------------------ the solver

def solve(model: RoomModel, rules: FitoutRules) -> FurnitureLayout:
    by_key = model.by_key()
    placed: list[Placed] = []
    unplaced: list[Unplaced] = []
    skipped: list[Skipped] = []
    cache: dict[str, tuple[list[dict], list[dict]]] = {}
    for room in model.rooms:
        why = _skip_reason(room, by_key)
        if why:
            skipped.append(Skipped(room_key=room.key, level=room.level, name=room.name, reason=why))
            continue
        classes = list(dict.fromkeys(c for k in [room.key, *room.shared_with] for c in by_key[k].classes))
        if not classes:
            skipped.append(Skipped(room_key=room.key, level=room.level, name=room.name,
                                   reason="no room class yet (asked): nothing to furnish it with"))
            continue
        if not any(rules.rooms[c].furnish for c in classes):
            continue
        entries, not_needed = _entries(room, classes, model, rules)
        for e, why in not_needed:
            unplaced.append(Unplaced(room_key=room.key, level=room.level, item=e.item,
                                     label=rules.furniture.items[e.item].label, optional=e.optional,
                                     status="not_needed", reason=why))
        if not entries:
            continue
        sig = _signature(room, entries)
        if sig not in cache:
            cache[sig] = _RoomSolver(room, entries, rules).run()
        done, failed = cache[sig]
        for d in done:
            placed.append(_record(room, d, rules))
        for f in failed:
            unplaced.append(Unplaced(room_key=room.key, level=room.level, **f))
    return FurnitureLayout(rule_set=rules.id, placed=placed, unplaced=unplaced, skipped=skipped)


def _skip_reason(room: Room, by_key: dict[str, Room]) -> str | None:
    if room.status == "not_enclosed":
        return "not enclosed by walls: no room shape to furnish"
    if room.status == "in_wall":
        return "its point lies in a wall: no room shape to furnish"
    if room.status == "waiting":
        return "a wall thickness on its level is asked: no room shape yet"
    if room.status == "shared":
        first = min([room.key, *room.shared_with])
        if first != room.key:
            return f"shares one space with {by_key[first].name} ({first}): furnished there, with both rooms' sets"
    return None


def _entries(room: Room, classes: list[str], model: RoomModel, rules: FitoutRules):
    flat_classes = {c for r in model.rooms if room.flat and r.flat == room.flat and r.key != room.key
                    for c in r.classes}
    keep, dropped = [], []
    for e in rules.set_for(classes):
        has = sorted(set(e.unless_flat_has) & flat_classes)
        if has:
            dropped.append((e, f"the flat has a {rules.rooms[has[0]].label.lower()} room for it"))
        else:
            keep.append(e)
    return keep, dropped


def _signature(room: Room, entries: list[SetEntry]) -> str:
    """What the layout of a room depends on, without its keys: equal rooms on typical storeys share one solve."""
    return json.dumps([room.polygon, room.holes, room.obstacles,
                       [(f.start, f.end, f.kind, f.outer) for f in room.faces],
                       [(o.kind, o.face, o.center, o.width_mm, o.sill_mm, o.head_mm, o.sliding, o.swings_in, o.swing,
                         o.left, o.right) for o in room.openings],
                       [e.model_dump() for e in entries]])


def _record(room: Room, d: dict, rules: FitoutRules) -> Placed:
    item = rules.furniture.items[d["item"]]
    face: Face | None = room.faces[d["face"]] if d.get("face") is not None else None
    host = f"FIT-{room.key}-{d['host']}" if d.get("host") else None
    return Placed(key=f"FIT-{room.key}-{d['item']}-{d['n']}", room_key=room.key, level=room.level, item=d["item"],
                  label=item.label, category=item.category, keywords=list(item.keywords), width_mm=d["width"],
                  depth_mm=d["depth"], height_mm=d["height"], size_index=d["size_index"], insertion=d["insertion"],
                  center=d["center"], footprint=d["footprint"], facing_deg=d["facing"],
                  rotation_deg=round((d["facing"] - 270) % 360, 3) % 360, elevation_mm=d.get("elevation", 0),
                  wall_key=face.wall_key if face else None, wall_side=face.side if face else None, host=host,
                  source=item.source)


class _RoomSolver:
    def __init__(self, room: Room, entries: list[SetEntry], rules: FitoutRules):
        self.room, self.entries, self.rules = room, entries, rules
        lay = rules.furniture.layout
        self.circ = float(lay.circulation_mm.value)
        self.grid = float(lay.grid_mm.value)
        self.window_depth = float(lay.window_zone_mm.value)
        swing_margin = float(lay.door_swing_clearance_mm.value)
        approach = float(lay.door_approach_mm.value)
        self.shape = room.shape()
        obstacles = [Polygon(o) for o in room.obstacles]
        self.usable = self.shape.difference(unary_union(obstacles)) if obstacles else self.shape
        self.usable_in = prepared.prep(self.usable.buffer(0.5, join_style="mitre"))
        self.faces = room.faces
        self.backable = [i for i, f in enumerate(self.faces) if f.kind in BACKABLE]
        self.max_face = max((self.faces[i].length_mm for i in self.backable), default=1.0)
        minx, miny, maxx, maxy = self.shape.bounds
        self.diag = math.hypot(maxx - minx, maxy - miny) or 1.0
        swings, approaches, self.door_points = [], [], []
        for o in room.doors:
            if o.face is None:
                continue
            f = self.faces[o.face]
            inward = f.inward
            along = f.direction
            if o.swings_in and o.swing:
                swings.append(Polygon(o.swing).buffer(swing_margin))
            else:
                p0 = o.left if _dot_sign(o.left, o.right, along) else o.right
                approaches.append(_rect(p0, along, inward, o.width_mm, approach))
            self.door_points.append(_add(o.center, inward, self.circ / 2 + 10))
        self.swing_zone = unary_union(swings) if swings else None
        self.approach_zone = unary_union(approaches) if approaches else None
        keep = swings + approaches
        self.keep = unary_union(keep) if keep else None
        self.keep_prep = prepared.prep(self.keep) if self.keep is not None else None
        self.windows = []  # (zone, sill, face index, span start, span end along the face)
        for o in room.windows:
            if o.face is None:
                continue
            f = self.faces[o.face]
            along, inward = f.direction, f.inward
            p0 = o.left if _dot_sign(o.left, o.right, along) else o.right
            s0 = _along(f, p0)
            self.windows.append((_rect(p0, along, inward, o.width_mm, self.window_depth), o.sill_mm, o.face, s0,
                                 s0 + o.width_mm))
        self.door_faces = {o.face for o in room.doors if o.face is not None}
        self.door_centres = [o.center for o in room.doors]
        self.items: list[_Item] = []
        self.option_cache: dict[tuple, list[list[_Cand]]] = {}
        self.done: list[dict] = []
        self.failed: list[dict] = []
        self.counts: Counter[str] = Counter()

    # ---------------------------------------------------------------- run

    # ---------------------------------------------------------------- run: greedy, then a bounded search

    def run(self) -> tuple[list[dict], list[dict]]:
        """Greedy first (each item at its best valid position, in priority order). When that leaves an item out, try
        each earlier item at its next-best valid positions and redo the rest greedily, keeping a layout that places
        more (required items first), until nothing improves or the budget is spent; then lay out the best found."""
        self.active_doors = self.reachable_doors()
        items = self.rules.furniture.items
        self.units = [(e, items[e.item]) for e in self.entries for _ in range(e.count)
                      if items[e.item].placement != "on_counter"]
        best = self.greedy({})
        runs = 1
        while not best.complete and runs < MAX_RUNS:
            improved = False
            for i in range(len(self.units)):
                for k in range(1, ALTERNATIVES):
                    if runs >= MAX_RUNS:
                        break
                    res = self.greedy({**best.forced, i: k})
                    runs += 1
                    if res is None:
                        break  # the item has no more valid positions
                    if res.objective > best.objective:
                        best, improved = res, True
                        break
                if improved or runs >= MAX_RUNS:
                    break
            if not improved:
                break
        self.replay(best.choices)
        return self.done, self.failed

    def greedy(self, forced: dict[int, int]) -> "_Result | None":
        self.items = []
        choices: list[tuple | None] = []
        for i, (e, item) in enumerate(self.units):
            k = forced.get(i, 0)
            choice = self.kth_valid(item, k)
            if choice is None and k:
                return None
            if choice is not None:
                self.hold(e.item, item, choice)
            choices.append(choice)
        placed = [(e, c) for (e, _), c in zip(self.units, choices) if c is not None]
        objective = (sum(not e.optional for e, _ in placed), sum(e.optional for e, _ in placed),
                     -sum(c[0] for _, c in placed), -sum(forced.values()))
        return _Result(choices, dict(forced), objective, len(placed) == len(self.units))

    def hold(self, item_id: str, item: FurnitureItem, choice: tuple) -> None:
        si, size, cands = choice
        for c in cands:
            self.items.append(_Item(item_id, item_id, item, size, si, c))

    def anchor_of(self, item: FurnitureItem) -> "_Item | None":
        return next((p for p in self.items if p.item_id == item.anchor), None) if item.anchor else None

    def options(self, item: FurnitureItem, si: int, size: Size, anchor: "_Item | None") -> list[list[_Cand]]:
        """Candidate positions (a counter: sets of legs), best first; they depend only on the room and the anchor."""
        key = (id(item), si, anchor.cand.body.wkb if anchor else None)
        if key not in self.option_cache:
            if item.placement == "counter":
                self.option_cache[key] = self.counter_options(item, size)
            else:
                self.option_cache[key] = [[c] for c in sorted(self.candidates(item, size, anchor),
                                                              key=lambda c: c.score)]
        return self.option_cache[key]

    def check_all(self, item: FurnitureItem, size: Size, cands: list[_Cand], anchor) -> str | None:
        for k, c in enumerate(cands):
            why = self.check(item, size, c, anchor) if k == 0 else self.check_with(item, size, c, cands[:k])
            if why:
                return why
        return None

    def kth_valid(self, item: FurnitureItem, k: int) -> tuple | None:
        anchor = self.anchor_of(item)
        if item.anchor and anchor is None:
            return None
        seen: list[Polygon] = []  # the k alternatives are distinct places: each overlaps no earlier one by half
        for si, size in enumerate(item.sizes):
            for cands in self.options(item, si, size, anchor):
                body = cands[0].body
                if any(body.intersection(b).area > 0.5 * min(body.area, b.area) for b in seen):
                    continue
                if self.check_all(item, size, cands, anchor) is None:
                    if len(seen) == k:
                        return si, size, cands
                    seen.append(body)
        return None

    def replay(self, choices: list[tuple | None]) -> None:
        """Lay out the chosen positions as records, then say why each item left out fits nowhere among them."""
        self.items, self.counts, self.done, self.failed = [], Counter(), [], []
        for (e, item), choice in zip(self.units, choices):
            if choice is None:
                continue
            si, size, cands = choice
            legs = [self.commit(e.item, item, size, si, c, n=(k + 1 if len(cands) > 1 else None))
                    for k, c in enumerate(cands)]
            if item.placement == "counter":
                self.place_fixtures(item, size, legs)
        for (e, item), choice in zip(self.units, choices):
            if choice is not None:
                continue
            anchor = self.anchor_of(item)
            if item.anchor and anchor is None:
                label = self.rules.furniture.items[item.anchor].label.lower()
                self.fail(e, item, "anchor_missing", f"it goes by its {label}, which is not placed")
                continue
            reasons: Counter[str] = Counter()
            tried = []
            for si, size in enumerate(item.sizes):
                tried.append(f"{size.width_mm:g} x {size.depth_mm:g}")
                opts = self.options(item, si, size, anchor)
                if not opts:
                    reasons[self.no_candidate_reason(item, size)] += 1
                for cands in opts:
                    reasons[self.check_all(item, size, cands, anchor) or "kept for a higher priority item"] += 1
            top = "; ".join(f"{why} ({n} positions)" if n > 1 else why for why, n in reasons.most_common(3))
            self.fail(e, item, "no_fit", f"no position keeps the rules at any size ({', '.join(tried)} mm): {top}")

    def reachable_doors(self) -> list[Point]:
        """Doors whose approach point is in the room's passage before anything is placed (others cannot be kept)."""
        eroded = self.usable.buffer(-(self.circ / 2 - PASSAGE_TOL_MM), join_style="mitre")
        return [p for p in self.door_points if eroded.covers(SPoint(p))]

    def fail(self, e: SetEntry, item: FurnitureItem, status: str, reason: str) -> None:
        self.failed.append(dict(item=e.item, label=item.label, optional=e.optional, status=status, reason=reason))

    def no_candidate_reason(self, item: FurnitureItem, size: Size) -> str:
        if item.placement == "counter":
            st = item.stretch
            return f"no wall run of {st.min_mm:g} mm, nor an L with legs of {st.leg_min_mm:g} mm"
        if item.placement in ("against_wall", "head_against_wall", "corner"):
            return f"no wall face {size.width_mm:g} mm long"
        if item.placement == "opposite":
            o = item.opposite
            return f"no wall faces its {item.anchor} {o.min_mm:g}-{o.max_mm:g} mm from it"
        if item.placement == "beside":
            return f"no wall space beside its {item.anchor}"
        return "the room is too small"

    # ---------------------------------------------------------------- candidates

    def wall_cand(self, fi: int, s: float, width: float, depth: float, item: FurnitureItem) -> _Cand:
        f = self.faces[fi]
        along, inward = f.direction, f.inward
        p0 = _add(f.start, along, s)
        body = _rect(p0, along, inward, width, depth)
        zones = self.zones(item, p0, along, inward, width, depth, body)
        back = _add(p0, along, width / 2)
        cand = _Cand(body, along, inward, back, fi, s, width, depth, zones, 0.0)
        cand.score = self.score(item, cand)
        return cand

    def zones(self, item: FurnitureItem, p0: Point, along: Point, inward: Point, width: float, depth: float,
              body: Polygon) -> dict[str, list[Polygon]]:
        c = item.clearance
        z: dict[str, list[Polygon]] = {}
        if c.front_mm:
            z["front"] = [_rect(_add(p0, inward, depth), along, inward, width, c.front_mm)]
        if c.sides_mm:
            z["sides"] = [_rect(_add(p0, along, -c.sides_mm), along, inward, c.sides_mm, depth),
                          _rect(_add(p0, along, width), along, inward, c.sides_mm, depth)]
        if c.around_mm:
            z["around"] = [body.buffer(c.around_mm, join_style="mitre").difference(body)]
        return z

    def positions(self, length: float, width: float) -> list[float]:
        if width > length + 0.5:
            return []
        free = max(length - width, 0.0)
        out = {0.0, round(free, 1), round(free / 2, 1)}
        s = self.grid
        while s < free:
            out.add(round(s, 1))
            s += self.grid
        return sorted(out)

    def candidates(self, item: FurnitureItem, size: Size, anchor: _Item | None) -> list[_Cand]:
        W, D = size.width_mm, size.depth_mm
        out: list[_Cand] = []
        if item.placement in ("against_wall", "head_against_wall", "corner"):
            for fi in self.backable:
                L = self.faces[fi].length_mm
                for s in self.positions(L, W):
                    if item.placement == "corner" and not self.in_corner(fi, s, W):
                        continue
                    out.append(self.wall_cand(fi, s, W, D, item))
        elif item.placement == "beside":
            a = anchor.cand
            for s in (a.s - W, a.s + a.width):
                L = self.faces[a.face].length_mm
                if -0.5 <= s <= L - W + 0.5:
                    out.append(self.wall_cand(a.face, s, W, D, item))
        elif item.placement == "opposite":
            a = anchor.cand
            af = self.faces[a.face]
            for fi in self.backable:
                f = self.faces[fi]
                if fi == a.face or f.inward[0] * af.inward[0] + f.inward[1] * af.inward[1] > -0.999:
                    continue
                gap = (f.start[0] - af.start[0]) * af.inward[0] + (f.start[1] - af.start[1]) * af.inward[1]
                clear = gap - a.depth - D
                if not item.opposite.min_mm <= clear <= item.opposite.max_mm:
                    continue
                target = _along(f, a.back) - W / 2
                for s in self.positions(f.length_mm, W):
                    c = self.wall_cand(fi, s, W, D, item)
                    c.score = abs(s - target) / self.diag + c.score * 0.01
                    out.append(c)
        elif item.placement == "free":
            out = self.free_candidates(item, size)
        return out

    def free_candidates(self, item: FurnitureItem, size: Size) -> list[_Cand]:
        minx, miny, maxx, maxy = self.usable.bounds
        cx, cy = self.usable.centroid.x, self.usable.centroid.y
        out = []
        for along, W, D in (((1.0, 0.0), size.width_mm, size.depth_mm), ((0.0, 1.0), size.width_mm, size.depth_mm)):
            inward = (-along[1], along[0])
            ext_x = W if along[0] else D
            ext_y = D if along[0] else W
            x = minx
            while x + ext_x <= maxx + 0.5:
                y = miny
                while y + ext_y <= maxy + 0.5:
                    p0 = (x + ext_x, y) if not along[0] else (x, y)
                    body = _rect(p0, along, inward, W, D)
                    centre = (x + ext_x / 2, y + ext_y / 2)
                    zones = self.zones(item, p0, along, inward, W, D, body)
                    c = _Cand(body, along, inward, _add(p0, along, W / 2), None, 0.0, W, D, zones, 0.0)
                    c.score = math.dist(centre, (cx, cy)) / self.diag + self.score(item, c) * 0.01
                    out.append(c)
                    y += FREE_STEP_MM
                x += FREE_STEP_MM
        return out

    def in_corner(self, fi: int, s: float, width: float) -> bool:
        f = self.faces[fi]
        n = len(self.faces)
        if s <= 0.5 and self.faces[(fi - 1) % n].kind in BACKABLE:
            return _cross(self.faces[(fi - 1) % n].direction, f.direction) > 0.5
        if s + width >= f.length_mm - 0.5 and self.faces[(fi + 1) % n].kind in BACKABLE:
            return _cross(f.direction, self.faces[(fi + 1) % n].direction) > 0.5
        return False

    # ---------------------------------------------------------------- scoring

    def score(self, item: FurnitureItem, c: _Cand) -> float:
        prefs = item.prefer
        total = 0.0
        for i, p in enumerate(prefs):
            total += (2 ** (len(prefs) - i)) * self.pref_value(p, c)
        if c.face is not None:
            total += c.face * 1e-6 + c.s * 1e-9  # ties: the first face, the first position
        return total

    def pref_value(self, p: str, c: _Cand) -> float:
        f = self.faces[c.face] if c.face is not None else None
        centre = c.body.centroid
        if p in ("window_wall", "no_window_wall", "under_window", "not_under_window"):
            overlap = f is not None and any(fi == c.face and s0 < c.s + c.width and s1 > c.s
                                            for _, _, fi, s0, s1 in self.windows)
            on_face = f is not None and any(fi == c.face for _, _, fi, _, _ in self.windows)
            if p in ("window_wall", "under_window"):
                return 0.0 if overlap else (0.5 if on_face else 1.0)
            return 1.0 if overlap else 0.0
        if p == "not_door_wall":
            return 1.0 if c.face in self.door_faces else 0.0
        if p == "longest_wall":
            return 1.0 - (f.length_mm / self.max_face if f else 0.0)
        if p == "corner":
            return 0.0 if c.face is not None and self.in_corner(c.face, c.s, c.width) else 1.0
        if p == "centre":
            if f is None:
                return centre.distance(self.usable.centroid) / self.diag
            return abs(c.s + c.width / 2 - f.length_mm / 2) / (f.length_mm / 2 + 1)
        if p in ("far_from_door", "near_door"):
            d = min((centre.distance(SPoint(q)) for q in self.door_centres), default=self.diag) / self.diag
            return 1.0 - d if p == "far_from_door" else d
        if p == "exterior_wall":
            return 0.0 if f is not None and f.outer else 1.0
        return 0.0

    # ---------------------------------------------------------------- checking

    def is_tall(self, item: FurnitureItem, size: Size, sill: float | None) -> bool:
        if item.tall is not None:
            return item.tall
        return sill is None or size.height_mm > sill

    def check(self, item: FurnitureItem, size: Size, c: _Cand, anchor: _Item | None) -> str | None:
        body = c.body
        if not self.usable_in.contains(body):
            return "it would stand outside the room or on a column"
        if item.under_door_swing:  # an open wet area may lie under a leaf's swing, never in a door's approach
            if self.approach_zone is not None and body.intersection(self.approach_zone).area > AREA_TOL:
                return "it would block a door's approach"
        elif self.keep is not None and self.keep_prep.intersects(body) and body.intersection(self.keep).area > AREA_TOL:
            return "it would block a door's swing or approach"
        for zone, sill, _, _, _ in self.windows:
            if self.is_tall(item, size, sill) and _overlap(body, zone):
                return "it is taller than a window's sill and would stand in front of the window"
        for p in self.items:
            if _overlap(body, p.cand.body):
                return f"it would overlap the {p.item.label.lower()}"
        for p in self.items:
            for kind, zs in p.cand.zones.items():
                if anchor is not None and p is anchor and kind == "sides":
                    continue  # a side table stands in its bed's side space by design
                if any(_overlap(body, z) for z in zs):
                    return f"it would stand in the {p.item.label.lower()}'s clearance"
        own = self.zone_check(item, c)
        if own:
            return own
        return self.circulation(c)

    def zone_check(self, item: FurnitureItem, c: _Cand) -> str | None:
        bodies = [p.cand.body for p in self.items]

        def clear(z: Polygon) -> bool:
            return self.usable_in.contains(z) and not any(_overlap(z, b) for b in bodies)

        cl = item.clearance
        for z in c.zones.get("front", []):
            if not clear(z):
                return f"the {cl.front_mm:g} mm in front of it would not be clear"
        sides = c.zones.get("sides", [])
        if sides and sum(clear(z) for z in sides) < cl.sides_required:
            return f"{'both its sides' if cl.sides_required == 2 else 'neither of its sides'} would have " \
                   f"{cl.sides_mm:g} mm clear"
        for z in c.zones.get("around", []):
            if not clear(z) or (self.swing_zone is not None and _overlap(z, self.swing_zone)):
                return f"it would not have {cl.around_mm:g} mm clear all round (or a door swings into it)"
        return None

    def circulation(self, c: _Cand | None, extra: list[Polygon] = ()) -> str | None:
        """After placing ``c``: the doors still reach each other, and every item's use space is reachable."""
        bodies = [p.cand.body for p in self.items] + ([c.body] if c else []) + list(extra)
        free = self.usable.difference(unary_union(bodies)) if bodies else self.usable
        passage = free.buffer(-(self.circ / 2 - PASSAGE_TOL_MM), join_style="mitre")
        parts = list(passage.geoms) if hasattr(passage, "geoms") else [passage]
        doors = self.active_doors
        if doors:
            hit = {i for q in doors for i, part in enumerate(parts) if part.covers(SPoint(q))}
            if len(hit) != 1 or any(not any(parts[i].covers(SPoint(q)) for i in hit) for q in doors):
                return f"it would leave no {self.circ:g} mm passage between the doors"
            reach = parts[hit.pop()]
        else:
            reach = max(parts, key=lambda g: g.area) if parts and not passage.is_empty else passage
        if reach.is_empty:
            return f"it would leave no {self.circ:g} mm passage in the room"
        reach = reach.buffer(self.circ / 2 + 1, join_style="mitre")
        uses = [(p.item, p.cand) for p in self.items] + ([(None, c)] if c else [])
        for it, cand in uses:
            zs = cand.zones.get("front") or cand.zones.get("around") or []
            if not zs and cand.zones.get("sides"):
                zs = cand.zones["sides"]
                if any(reach.intersection(z).area >= ACCESS_SHARE * z.area for z in zs):
                    continue
                return "it would cut off its own side space from the doors" if it is None else \
                    f"it would cut the {it.label.lower()} off from the doors"
            for z in zs:
                if reach.intersection(z).area < ACCESS_SHARE * z.area:
                    return "its use space would not be reachable from the doors" if it is None else \
                        f"it would cut the {it.label.lower()} off from the doors"
        return None

    # ---------------------------------------------------------------- committing

    def commit(self, item_id: str, item: FurnitureItem, size: Size, si: int, c: _Cand, *, host: str | None = None,
               elevation: float = 0.0, n: int | None = None) -> _Item:
        self.counts[item_id] += 1
        n = n or self.counts[item_id]
        placed = _Item(f"{item_id}-{n}", item_id, item, size, si, c)
        self.items.append(placed)
        fp = [_r(q) for q in list(c.body.exterior.coords)[:-1]]
        cen = c.body.centroid
        self.done.append(dict(item=item_id, n=n, width=c.width, depth=c.depth, height=size.height_mm, size_index=si,
                              insertion=_r(c.back), center=_r((cen.x, cen.y)), footprint=fp, facing=_deg(c.inward),
                              face=c.face, host=host, elevation=elevation))
        return placed

    # ---------------------------------------------------------------- the counter

    def counter_options(self, item: FurnitureItem, size: Size) -> list[list[_Cand]]:
        """Counter runs, best first: an L at an inside corner (legs of at least leg_min) or a straight run, the total
        within min..max, in the rule's shape order, longer first, then the preferences."""
        st = item.stretch
        D = size.depth_mm
        n = len(self.faces)
        options = []  # (rank, total, legs: [(face, s, length)])
        for rank, shape in enumerate(st.shapes):
            if shape == "straight":
                for fi in self.backable:
                    L = self.faces[fi].length_mm
                    for length in _lengths(min(L, st.max_mm), st.min_mm):
                        for s in sorted({0.0, round(L - length, 1)}):
                            options.append((rank, length, [(fi, s, length)]))
            else:
                for fi in self.backable:
                    fj = (fi + 1) % n
                    if fj not in self.backable or _cross(self.faces[fi].direction, self.faces[fj].direction) < 0.99:
                        continue  # legs meet at an inside corner of the room (a left turn going round it)
                    for la in _lengths(min(self.faces[fi].length_mm, st.max_mm), st.leg_min_mm):
                        for lb in _lengths(min(self.faces[fj].length_mm, st.max_mm), st.leg_min_mm):
                            total = la + lb - D
                            if st.min_mm <= total <= st.max_mm:
                                options.append((rank, total, [(fi, self.faces[fi].length_mm - la, la), (fj, 0.0, lb)]))
        scored = []
        for rank, total, legs in options:
            # an L's second leg starts past the first leg's depth: the corner square belongs to the first
            cands = [self.wall_cand(fi, s + (D if k else 0.0), length - (D if k else 0.0), D, item)
                     for k, (fi, s, length) in enumerate(legs)]
            pref = sum(c.score for c in cands) / len(cands)
            scored.append((rank * 100 + (1 - total / st.max_mm) * 10 + pref, cands))
        scored.sort(key=lambda t: t[0])
        return [c for _, c in scored]

    def check_with(self, item, size, c: _Cand, before: list[_Cand]) -> str | None:
        """Check a later counter leg as if the earlier legs were placed (they are joined: no clearance between)."""
        saved = list(self.items)
        try:
            for b in before:
                self.items.append(_Item("leg", "leg", item, size, 0, _Cand(b.body, b.along, b.inward, b.back, b.face,
                                                                            b.s, b.width, b.depth, {}, 0.0)))
            return self.check(item, size, c, None)
        finally:
            self.items[:] = saved

    def place_fixtures(self, counter: FurnitureItem, csize: Size, legs: list[_Item]) -> None:
        items = self.rules.furniture.items
        entry_of = {e.item: e for e in self.entries}
        on: list[tuple[str, _Cand]] = []
        for fx in counter.fixtures:
            item = items[fx]
            e = entry_of.get(fx) or SetEntry(item=fx)
            rule = item.on_counter
            best = None
            reasons: Counter[str] = Counter()
            for si, size in enumerate(item.sizes):
                cands = []
                for leg in legs:
                    lc = leg.cand
                    start = rule.min_from_end_mm if rule else 0.0
                    end = lc.width - size.width_mm - (rule.min_from_end_mm if rule else 0.0)
                    s = start
                    while s <= end + 0.5:
                        p0 = _add(_add(self.faces[lc.face].start, lc.along, lc.s + s), lc.inward,
                                  (csize.depth_mm - size.depth_mm) / 2)
                        body = _rect(p0, lc.along, lc.inward, size.width_mm, size.depth_mm)
                        c = _Cand(body, lc.along, lc.inward, _add(p0, lc.along, size.width_mm / 2), lc.face,
                                  lc.s + s, size.width_mm, size.depth_mm, {}, 0.0)
                        c.score = self.score(item, c)
                        cands.append((leg, c))
                        s += self.grid
                for leg, c in sorted(cands, key=lambda t: t[1].score):
                    if any(_overlap(c.body, o.body) for _, o in on):
                        reasons["it would overlap another fixture on the counter"] += 1
                        continue
                    far = rule.min_from_mm if rule else {}
                    near = [fid for fid, o in on if fid in far and c.body.distance(o.body) < far[fid] - 0.5]
                    if near:
                        reasons[f"it would be closer than {far[near[0]]:g} mm to the {items[near[0]].label.lower()}"] += 1
                        continue
                    best = (leg, c, size, si)
                    break
                if best:
                    break
            if best is None:
                why = "; ".join(r for r, _ in reasons.most_common(2)) or "the counter is too short for it"
                self.fail(e, item, "no_fit", f"no place on the counter: {why}")
                continue
            leg, c, size, si = best
            self.commit(fx, item, size, si, c, host=leg.key, elevation=csize.height_mm)
            self.items.pop()  # a fixture is part of its counter: not an obstacle for later items
            on.append((fx, c))


def problems(layout: FurnitureLayout, model: RoomModel, rules: FitoutRules) -> list[str]:
    """An audit of a layout against its rooms, independent of how it was solved (and for the fit-out self-check):
    items outside their room or on a column, items overlapping (a sink or hob on its counter excepted), items in a
    door leaf's swing, tall items in front of a window. Empty when the layout keeps the hard rules."""
    out = []
    rooms = model.by_key()
    depth = float(rules.furniture.layout.window_zone_mm.value)
    for key in dict.fromkeys(p.room_key for p in layout.placed):
        room = rooms[key]
        shape = room.shape()
        for ob in room.obstacles:
            shape = shape.difference(Polygon(ob))
        inside = shape.buffer(1.0, join_style="mitre")
        items = layout.in_room(key)
        bodies = [(p, Polygon(p.footprint)) for p in items]
        for p, b in bodies:
            if not inside.contains(b):
                out.append(f"{p.key}: outside its room or on a column")
            item = rules.furniture.items[p.item]
            for o in room.doors:
                if o.swing and not item.under_door_swing and _overlap(b, Polygon(o.swing)):
                    out.append(f"{p.key}: in the swing of door {o.key}")
            for o in room.windows:
                if o.face is None:
                    continue
                f = room.faces[o.face]
                p0 = o.left if _dot_sign(o.left, o.right, f.direction) else o.right
                zone = _rect(p0, f.direction, f.inward, o.width_mm, depth)
                tall = item.tall if item.tall is not None else (o.sill_mm is None or p.height_mm > o.sill_mm)
                if tall and _overlap(b, zone):
                    out.append(f"{p.key}: tall, in front of window {o.key}")
        for i, (p, b) in enumerate(bodies):
            for q, c in bodies[i + 1:]:
                if _on_counter_of(p, q) or _on_counter_of(q, p):
                    continue  # a sink or hob sits on its counter (on either leg of an L)
                if _overlap(b, c):
                    out.append(f"{p.key} overlaps {q.key}")
    return out


def _on_counter_of(fixture: Placed, other: Placed) -> bool:
    return bool(fixture.host) and not other.host and other.key.rsplit("-", 1)[0] == fixture.host.rsplit("-", 1)[0]


def _lengths(top: float, bottom: float) -> list[float]:
    out = []
    x = top
    while x >= bottom - 0.5:
        out.append(round(x, 1))
        x -= LENGTH_STEP_MM
    return out


def _cross(a: Point, b: Point) -> float:
    return a[0] * b[1] - a[1] * b[0]


def _along(f: Face, p: Point) -> float:
    d = f.direction
    return (p[0] - f.start[0]) * d[0] + (p[1] - f.start[1]) * d[1]


def _dot_sign(a: Point, b: Point, along: Point) -> bool:
    """True when ``a`` comes before ``b`` going along ``along``."""
    return (b[0] - a[0]) * along[0] + (b[1] - a[1]) * along[1] >= 0
