"""What the MEP layout (PRD Step 10, F4) is made of, and the shared ground its four systems stand on.

    from plinth.fitout import mep
    layout = mep.layout(plan, model, furniture_layout, ruleset, answers)   # an MepLayout (this module's records)

Why a record per device, run and circuit (and not Revit elements straight away): nothing is built before review
(PRD Step 10 rules), and F5 maps each record to one add-in command, so the records say exactly what those commands
take: a device is a family placed at a point (free, on a wall side at a height, on a ceiling), with the category and
keywords F1/F5 match a family type by, like the furniture records; a run is ``create_mep_run``'s polyline of 3D points
with the element (device, fixture or main) each end joins; a circuit is ``create_electrical_circuit``'s devices and
panel. The layout is laid out by rules and is an indicative coordination model (``NOTICE``): every output says so.

Keys are stable for the same plan, rules and answers: ``MEP-<room>-<what>-<n>`` for a device (``<room>`` is the room
key, or the level or "site" for what stands in no room), ``RUN-<scope>-<what>-<n>`` for a run, ``CKT-<panel>-<n>`` for
a circuit. A fixture placed by the furniture solver (WC, basin, shower, sink) keeps its ``FIT-...`` key: runs connect to
it by that key.

What a run must keep, so F5 can draw it (``addin/.../CreateMepRunHandler.cs``): 2 to 200 points, no leg turning back on
itself; an end joins a device's free connector, a curve's free end, or a straight pipe or duct "main" beside its
middle (at least 50 mm of main each side), the add-in extending the run from its end point to that main. Conduits
cannot tee (Revit breaks only pipes and ducts), so a conduit's ends name devices only. Here every end that tees into a
main stops ``TEE_GAP_MM`` short of the main's axis, square to it.

Shared ground (``Ctx``): the rooms to fit (a room sharing its space with another is fitted with the first, as the
furniture solver does), each storey's footprint (runs keep inside it), the ducts (shaft ops or rooms of class shaft,
kind from the caller, an answer, or asked), the roof (the level of the roof terraces), doors as boxes (runs keep clear
of them), the structure (columns and beams: runs step round them, ``detour``), wall spots along a room's faces
clear of doors, windows and tall furniture backs, ceiling heights from the
slab over each room, the questions asked and the assumptions taken.
"""

from __future__ import annotations

import math
from collections import Counter, defaultdict
from typing import Any, Iterable, Literal, Mapping

from pydantic import BaseModel, ConfigDict
from shapely.geometry import LineString, Point as SPoint, Polygon
from shapely.ops import nearest_points, unary_union

from plinth.fitout.furniture import FurnitureLayout, Placed
from plinth.fitout.rooms import Assumption, Face, Opening, Question, Room, RoomModel, type_size, type_thickness
from plinth.fitout.rules import FitoutRules, MepDevice, Size
from plinth.plan import BeamOp, BuildPlan, ColumnOp, FloorOp, FloorTypeOp, ShaftOp, StairOp, WallOp, WallTypeOp

Point = tuple[float, float]
P3 = tuple[float, float, float]

NOTICE = ("Indicative coordination model: the MEP layout comes from rules (NBC 2016 and typical Indian residential "
          "practice for the project's location; every rule value an unverified placeholder with its source), not from "
          "an engineer's design. An MEP engineer designs and checks the real systems.")

TEE_GAP_MM = 100.0  # a run teeing into a main stops this far from the main's axis (the add-in extends it)
CORNER_MM = 150.0  # wall devices keep this far from a face's ends
OPENING_MARGIN_MM = 100.0  # and this far beside a door or window
LOW_FURNITURE_MM = 600.0  # furniture this low (side table, TV unit, bed) does not hide a socket behind it
DEFAULT_DOOR_HEAD_MM = 2100.0
DEFAULT_SLAB_MM = 150.0
INSIDE_TOL_MM = 5.0
PIPE_CLEAR_MM = 25.0  # a pipe keeps this far off a column or beam (beyond its radius)
MAX_JOG_MM = 1500.0  # a run steps round a column or beam this far aside at most (else it is not drawn that way)
OPEN_CLASSES = ("terrace",)  # rooms open to the sky: no ceiling (lights on their walls, no fans)
OPEN_WALLS_MM = 1800.0  # a room with no slab over it whose walls are lower than this (a parapet) is open too

DeviceSystem = Literal["power", "lighting", "data", "hvac", "plumbing", "fire"]
RunSystem = Literal["cold_water", "hot_water", "soil", "waste", "vent", "rainwater", "power", "lighting", "fire"]
SYSTEM_LABELS = {"cold_water": "domestic cold water", "hot_water": "domestic hot water", "soil": "sanitary (soil)",
                 "waste": "sanitary (waste)", "vent": "vent", "rainwater": "rainwater (storm)", "power": "power",
                 "lighting": "lighting", "fire": "fire protection (wet)"}


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


# ------------------------------------------------------------------------------------------------ records

class Host(_Model):
    """Where a device is fixed: on a wall's side (``height_mm``: its centre above the level), on the ceiling
    (``height_mm``: the ceiling's height above the level; with no ceiling F5 places it facing down at that offset), on
    the floor, or outside a wall (the outdoor unit: ``side`` is the wall's outer side)."""

    kind: Literal["wall", "ceiling", "floor", "outside_wall"]
    wall_key: str | None = None
    side: Literal["left", "right"] | None = None
    height_mm: float = 0


class Device(_Model):
    key: str
    room_key: str | None  # None: stands in no room (outdoor unit outside its room's wall keeps its room)
    level: str
    flat: str | None = None
    system: DeviceSystem
    what: str  # the device catalogue key (rules: electrical/plumbing/hvac/fire devices)
    label: str
    category: str  # Revit category, for family matching (F1/F5)
    keywords: list[str]
    insertion: Point  # on the wall face for a wall device; the centre for others
    elevation_mm: float  # above the level: a wall device's centre, a ceiling device's ceiling, a floor device's base
    facing_deg: float  # the direction its front faces (into the room for a wall device), anticlockwise from +X
    rotation_deg: float  # facing - 270: for a family drawn facing -Y, about its insertion point
    host: Host
    size: Size | None = None
    load_w: float | None = None  # an electrical load: goes on a circuit
    rating: str | None = None  # "16 A", "1.5 TR", "25 L", "5400 L"
    capacity_l: float | None = None
    panel: bool = False  # a distribution board or meter board: circuits end at it
    source: str
    note: str | None = None


class Run(_Model):
    key: str
    kind: Literal["pipe", "duct", "conduit"]
    system: RunSystem
    level: str  # the reference level F5 gives create_mep_run
    points: list[P3]  # model mm, z absolute
    size_mm: float  # nominal diameter
    slope_pct: float | None = None
    connect_start: str | None = None  # a device or FIT- fixture key, or (pipes) a run key: tee into that main
    connect_end: str | None = None
    room_key: str | None = None
    flat: str | None = None
    source: str
    note: str | None = None

    @property
    def length_mm(self) -> float:
        return sum(math.dist(a, b) for a, b in zip(self.points, self.points[1:]))


class Circuit(_Model):
    key: str
    panel: str  # the distribution board's device key
    kind: Literal["lighting", "sockets", "power", "dedicated"]
    devices: list[str]
    load_w: float
    rating_a: int
    source: str


class Unplaced(_Model):
    what: str
    room_key: str | None = None
    level: str | None = None
    reason: str


class Unrouted(_Model):
    what: str
    from_key: str | None = None
    to_key: str | None = None
    level: str | None = None
    reason: str


class Provision(_Model):
    """One fire-safety provision of the building's height band (or a water/electrical provision): what was laid out
    for it, or why not."""

    id: str
    label: str
    status: Literal["placed", "sized", "not_laid_out"]
    devices: list[str] = []
    runs: list[str] = []
    note: str | None = None
    source: str


class WaterSizing(_Model):
    persons_per_flat: float
    flats: int
    persons: float
    demand_lpd: float
    overhead_l: dict[str, float] = {}  # tank key -> domestic litres it holds
    underground_l: float
    fire_terrace_l: float = 0
    fire_underground_l: float = 0
    source: str


class MepLayout(_Model):
    rule_set: str
    notice: str = NOTICE
    height_m: float | None = None  # building height for the fire band
    fire_band_max_m: float | None = None
    devices: list[Device] = []
    runs: list[Run] = []
    circuits: list[Circuit] = []
    unplaced: list[Unplaced] = []
    unrouted: list[Unrouted] = []
    provisions: list[Provision] = []
    water: WaterSizing | None = None
    questions: list[Question] = []
    assumptions: list[Assumption] = []

    def device(self, key: str) -> Device | None:
        return next((d for d in self.devices if d.key == key), None)

    def run(self, key: str) -> Run | None:
        return next((r for r in self.runs if r.key == key), None)

    def in_room(self, room_key: str) -> list[Device]:
        return [d for d in self.devices if d.room_key == room_key]

    def counts(self) -> dict[str, Counter]:
        """Per system: devices by what, runs by kind and system (the review page's summary)."""
        out: dict[str, Counter] = defaultdict(Counter)
        for d in self.devices:
            out[d.system][d.what] += 1
        for r in self.runs:
            out["runs"][f"{r.kind}:{r.system}"] += 1
        out["circuits"] = Counter(c.kind for c in self.circuits)
        return dict(out)

    def problems(self, model: RoomModel, furniture: FurnitureLayout) -> list[str]:
        from plinth.fitout.mep import problems
        return problems(self, model, furniture)


# ------------------------------------------------------------------------------------------------ geometry

def r1(x: float) -> float:
    return round(x, 1)


def pt(p: Iterable[float]) -> tuple:
    return tuple(r1(v) for v in p)


def add(a: Point, b: Point, k: float = 1.0) -> Point:
    return (a[0] + b[0] * k, a[1] + b[1] * k)


def sub(a: Point, b: Point) -> Point:
    return (a[0] - b[0], a[1] - b[1])


def dot(a: Point, b: Point) -> float:
    return a[0] * b[0] + a[1] * b[1]


def unit(v: Point) -> Point:
    L = math.hypot(*v) or 1.0
    return (v[0] / L, v[1] / L)


def deg(v: Point) -> float:
    return round(math.degrees(math.atan2(v[1], v[0])) % 360, 3) % 360


def along(f: Face, p: Point) -> float:
    return dot(sub(p, f.start), f.direction)


def clean(points: list[P3]) -> list[P3]:
    """Points as create_mep_run takes them: no repeated point, no point on a straight line, no leg turning back."""
    out: list[P3] = []
    for p in (pt(q) for q in points):
        if out and math.dist(out[-1], p) < 1.0:
            continue
        out.append(p)
        while len(out) >= 3:
            a, b, c = out[-3], out[-2], out[-1]
            u = [b[i] - a[i] for i in range(3)]
            v = [c[i] - b[i] for i in range(3)]
            lu, lv = math.sqrt(sum(x * x for x in u)), math.sqrt(sum(x * x for x in v))
            cos = sum(u[i] * v[i] for i in range(3)) / (lu * lv)
            if abs(cos) > 1 - 1e-9:  # straight on (drop the middle point) or turning back (go straight to c)
                out.pop(-2)
            else:
                break
    return out


_SHRUNK: dict[int, tuple] = {}


def _shrunk(poly: Polygon) -> tuple:
    """An obstacle's outline less 0.5 mm (touching it is clear) and its bounds, kept per outline."""
    got = _SHRUNK.get(id(poly))
    if got is None or got[0] is not poly:
        got = (poly, poly.buffer(-0.5), poly.bounds)
        _SHRUNK[id(poly)] = got
    return got[1], got[2]


def through(path: list[Point], obstacles: list[tuple[Polygon, str]]) -> list[str]:
    """The obstacles a plan path passes through (touching an outline is clear)."""
    out = []
    for a, b in zip(path, path[1:]):
        x0, x1 = sorted((a[0], b[0]))
        y0, y1 = sorted((a[1], b[1]))
        g = None
        for poly, key in obstacles:
            if key in out:
                continue
            s, (bx0, by0, bx1, by1) = _shrunk(poly)
            if bx0 > x1 or bx1 < x0 or by0 > y1 or by1 < y0:
                continue
            if g is None:
                g = LineString([a, b]) if math.dist(a, b) > 0.5 else SPoint(a)
            if s.intersects(g):
                out.append(key)
    return out


def detour(path: list[Point], obstacles: list[tuple[Polygon, str]], allowed=None, fixed_ends: float = 50.0
           ) -> list[Point]:
    """An axis-aligned plan path stepped round the obstacles it passes through: where a straight leg meets an
    obstacle (a column, a beam: ``Ctx.obstacles``), it jogs to the obstacle's nearer side and back onto its line beyond
    it, no more than ``MAX_JOG_MM`` aside, keeping clear of the other obstacles and inside ``allowed`` (a shape) when
    given. The ends stay as they are (and ``fixed_ends`` of each leg, so a run still leaves and arrives square); a leg
    that cannot step round stays as it was (``through`` still lists it)."""
    pts = [tuple(p) for p in path]
    shrunk = [(_shrunk(poly)[0], poly) for poly, _ in obstacles]

    def clear(seq: list[Point]) -> bool:
        for a, b in zip(seq, seq[1:]):
            if math.dist(a, b) < 0.5:
                continue
            g = LineString([a, b])
            if any(s.intersects(g) for s, _ in shrunk) or (allowed is not None and not allowed.covers(g)):
                return False
        return True

    for _ in range(4 * len(shrunk) + 4):
        changed = False
        for i in range(len(pts) - 1):
            a, b = pts[i], pts[i + 1]
            L = math.dist(a, b)
            if L < 1:
                continue
            seg = LineString([a, b])
            hit = [(s, p) for s, p in shrunk if s.intersects(seg)]
            if not hit:
                continue
            k = 0 if abs(b[1] - a[1]) < 0.5 else 1 if abs(b[0] - a[0]) < 0.5 else None
            if k is None or any(s.contains(SPoint(a)) or s.contains(SPoint(b)) for s, _ in hit):
                continue
            sgn = 1 if b[k] > a[k] else -1
            _, poly = min(hit, key=lambda sp: sgn * (sp[1].bounds[k] if sgn > 0 else sp[1].bounds[k + 2]))
            lo, hi = poly.bounds[k], poly.bounds[k + 2]
            enter, leave = (lo, hi) if sgn > 0 else (hi, lo)
            if sgn * (enter - a[k]) < fixed_ends or sgn * (b[k] - leave) < fixed_ends:
                continue
            o = 1 - k
            sides = sorted((poly.bounds[o], poly.bounds[o + 2]), key=lambda c: (abs(c - a[o]), c))
            for c in sides:
                if abs(c - a[o]) > MAX_JOG_MM:
                    continue

                def P(along_: float, across: float) -> Point:
                    return pt((along_, across) if k == 0 else (across, along_))

                new = [P(enter, a[o]), P(enter, c), P(leave, c), P(leave, a[o])]
                if clear([a, *new, b]):
                    pts[i + 1:i + 1] = new
                    changed = True
                    break
            if changed:
                break
        if not changed:
            break
    out: list[Point] = []
    for p in pts:
        if out and math.dist(out[-1], p) < 0.5:
            continue
        out.append(p)
        while len(out) >= 3 and abs((out[-2][0] - out[-3][0]) * (out[-1][1] - out[-2][1]) -
                                    (out[-2][1] - out[-3][1]) * (out[-1][0] - out[-2][0])) < 1e-6:
            out.pop(-2)
    return out


def manhattan(a: Point, b: Point, first: Literal["x", "y"]) -> list[Point]:
    """a -> b along the axes, ``first`` axis first."""
    mid = (b[0], a[1]) if first == "x" else (a[0], b[1])
    return [a, mid, b]


# ------------------------------------------------------------------------------------------------ the context

class Duct:
    """A vertical service duct: one outline through the storeys (shaft ops with the same outline, or rooms of class
    shaft). Pipes stand in a row along its long side, branches arrive square to the row."""

    def __init__(self, key: str, outline: list[Point], levels: list[str], spacing: float):
        self.key, self.outline, self.levels = key, outline, levels
        self.poly = Polygon(outline)
        minx, miny, maxx, maxy = self.poly.bounds
        self.centre = ((minx + maxx) / 2, (miny + maxy) / 2)
        self.long = "x" if maxx - minx >= maxy - miny else "y"
        self.long_len = max(maxx - minx, maxy - miny)
        self.spacing = spacing
        self.slots: list[str] = []  # what stands at each slot, in order along the long side

    def assign(self, slots: list[str]) -> None:
        """The pipes the duct holds, in order; closer than the rule's spacing when the duct is short."""
        self.slots = list(slots)
        if len(slots) > 1:
            self.spacing = min(self.spacing, max(self.long_len - 2 * 100, 0) / (len(slots) - 1))

    def slot(self, what: str) -> Point | None:
        """The plan position of a pipe in the row, centred on the duct; None when the duct holds no such pipe."""
        if what not in self.slots:
            return None
        i, n = self.slots.index(what), len(self.slots)
        off = (i - (n - 1) / 2) * self.spacing
        p = (self.centre[0] + off, self.centre[1]) if self.long == "x" else (self.centre[0], self.centre[1] + off)
        return pt(p)

    def approach(self, stack: Point, towards: Point) -> tuple[Point, Point]:
        """The point beside ``stack`` a branch from ``towards`` ends at (square to the row), and that direction."""
        if self.long == "x":
            n = (0.0, 1.0 if towards[1] >= stack[1] else -1.0)
        else:
            n = (1.0 if towards[0] >= stack[0] else -1.0, 0.0)
        return add(stack, n, TEE_GAP_MM), n


class Ctx:
    """Everything the four systems share while laying out: rooms, storeys, ducts, roof, records and keys."""

    def __init__(self, plan: BuildPlan, model: RoomModel, furniture: FurnitureLayout, rules: FitoutRules,
                 answers: Mapping[str, Any] | None, shaft_kinds: Mapping[str, str] | None):
        self.plan, self.model, self.furniture, self.rules = plan, model, furniture, rules
        self.answers = dict(answers or {})
        self.shaft_kinds = dict(shaft_kinds or {})
        self.by_key = model.by_key()
        self.levels = dict(model.levels)
        self.devices: list[Device] = []
        self.runs: list[Run] = []
        self.circuits: list[Circuit] = []
        self.unplaced: list[Unplaced] = []
        self.unrouted: list[Unrouted] = []
        self.provisions: list[Provision] = []
        self.water: WaterSizing | None = None
        self.questions: dict[str, Question] = {}
        self.assumptions: dict[str, Assumption] = {}
        self.counter: Counter = Counter()
        self.notes: dict[str, str] = {}
        self.furn_by_room: dict[str, list[Placed]] = defaultdict(list)
        for p in furniture.placed:
            self.furn_by_room[p.room_key].append(p)
        # rooms to fit: shaped, and (sharing a space) the first by key with every sharer's classes
        self.fit_rooms: list[Room] = []
        self.room_classes: dict[str, list[str]] = {}
        for r in model.rooms:
            if r.polygon is None or r.status in ("not_enclosed", "in_wall", "waiting"):
                continue
            if r.status == "shared" and min([r.key, *r.shared_with]) != r.key:
                continue
            classes = list(dict.fromkeys(c for k in [r.key, *r.shared_with] for c in self.by_key[k].classes))
            if classes:
                self.fit_rooms.append(r)
                self.room_classes[r.key] = classes
        self.storeys = sorted({r.level for r in model.rooms}, key=lambda n: (self.levels[n], n))
        self.ops_floor = [op for op in plan.ops if isinstance(op, FloorOp)]
        self.floor_types = {op.name: op for op in plan.ops if isinstance(op, FloorTypeOp)}
        self.stairs = [op for op in plan.ops if isinstance(op, StairOp)]
        self.walls = {op.key: op for op in plan.ops if isinstance(op, WallOp)}
        self.wall_types = {op.name: op for op in plan.ops if isinstance(op, WallTypeOp)}
        self._footprints: dict[str, Any] = {}
        self._doors: dict[str, list[tuple[Polygon, float, float, str]]] = {}
        self.structure = self._structure()
        self.roof = self._roof()
        self.ducts = self._ducts()
        self.height_m, self.fire_band = self._height()

    def _height(self):
        """Building height for the fire band: the roof terrace above the lowest floor (an answer overrides)."""
        fire = self.rules.fire
        given = self.answer("fitout:building_height_m")
        try:
            h = float(given) if given is not None else None
        except (TypeError, ValueError):
            h = None
        if h is None and self.roof and self.storeys:
            h = (self.elev(self.roof) - self.elev(self.storeys[0])) / 1000
            self.assume("fitout:building_height", f"Building height {h:g} m for the fire provisions: the roof terrace "
                        f"({self.roof}) above the lowest floor ({self.storeys[0]}); NBC measures from the average "
                        "ground level, which the plan does not give (answer fitout:building_height_m to correct it)",
                        h, fire.height_measure.source)
        if h is None:
            return None, None
        return round(h, 3), fire.band(h)

    # ---------------------------------------------------------------- questions, assumptions, keys

    def ask(self, id_: str, topic: str, text: str, options: Iterable = (), instances: Iterable[str] = ()) -> None:
        q = self.questions.get(id_)
        inst = sorted(set((q.instances if q else []) + list(instances)))
        self.questions[id_] = Question(id=id_, topic=topic, text=text, options=list(options), instances=inst)

    def assume(self, id_: str, text: str, value: Any, source: str, instances: Iterable[str] = ()) -> None:
        a = self.assumptions.get(id_)
        inst = sorted(set((a.instances if a else []) + list(instances)))
        self.assumptions[id_] = Assumption(id=id_, text=text, value=value, source=source, instances=inst)

    def answer(self, id_: str) -> Any:
        return self.answers.get(id_)

    def key(self, scope: str, what: str) -> str:
        self.counter[("MEP", scope, what)] += 1
        return f"MEP-{scope}-{what}-{self.counter[('MEP', scope, what)]}"

    def run_key(self, scope: str, what: str) -> str:
        self.counter[("RUN", scope, what)] += 1
        return f"RUN-{scope}-{what}-{self.counter[('RUN', scope, what)]}"

    def spec(self, what: str) -> MepDevice:
        r = self.rules
        for cat in (r.electrical.devices, r.plumbing.devices, r.hvac.devices, r.fire.devices):
            if what in cat:
                return cat[what]
        raise KeyError(f"no MEP device {what!r} in the rule set")

    def load_of(self, what: str) -> float | None:
        s = self.spec(what)
        return float(self.rules.electrical.loads_w[s.load].value) if s.load else None

    # ---------------------------------------------------------------- placing

    def place(self, what: str, system: DeviceSystem, room: Room | None, level: str, insertion: Point,
              elevation: float, facing: Point | float, host: Host, *, scope: str | None = None,
              size: Size | None = None, rating: str | None = None, capacity_l: float | None = None,
              panel: bool = False, load: bool = True, note: str | None = None, source: str | None = None,
              flat: str | None = None, kind: str | None = None) -> Device:
        """``kind``: the catalogue entry whose label, category, keywords and size the device takes (a light's fitting,
        ``electrical.light_kinds``); the device stays a ``what`` (its load, its circuit)."""
        s = self.spec(kind or what)
        facing_deg = facing if isinstance(facing, (int, float)) else deg(facing)
        d = Device(key=self.key(scope or (room.key if room else level), what), room_key=room.key if room else None,
                   level=level, flat=flat if flat is not None else (room.flat if room else None), system=system,
                   what=what, label=s.label, category=s.category, keywords=list(s.keywords),
                   insertion=pt(insertion), elevation_mm=r1(elevation), facing_deg=round(facing_deg, 3),
                   rotation_deg=round((facing_deg - 270) % 360, 3) % 360, host=host, size=size or s.size,
                   load_w=self.load_of(what) if load else None, rating=rating, capacity_l=capacity_l, panel=panel,
                   source=source or s.source, note=note)
        self.devices.append(d)
        return d

    def in_room_devices(self, room_key: str, what: str | None = None) -> list[Device]:
        return [d for d in self.devices if d.room_key == room_key and (what is None or d.what == what)]

    def device(self, key: str) -> Device | None:
        return next((d for d in self.devices if d.key == key), None)

    def on_wall(self, what: str, system: DeviceSystem, room: Room, spot: "Spot", height: float, **kw) -> Device:
        f = room.faces[spot.face]
        return self.place(what, system, room, room.level, spot.point, height, f.inward,
                          Host(kind="wall", wall_key=f.wall_key, side=f.side, height_mm=r1(height)), **kw)

    def add_run(self, kind: str, system: str, level: str, points: list[P3], size: float, *, scope: str, what: str,
                start: str | None = None, end: str | None = None, slope: float | None = None,
                room: str | None = None, flat: str | None = None, source: str, note: str | None = None) -> Run | None:
        pts = clean(points)
        if len(pts) < 2:
            self.unrouted.append(Unrouted(what=what, from_key=start, to_key=end, level=level,
                                          reason="its ends coincide: nothing to draw"))
            return None
        run = Run(key=self.run_key(scope, what), kind=kind, system=system, level=level, points=pts, size_mm=size,
                  slope_pct=slope, connect_start=start, connect_end=end, room_key=room, flat=flat, source=source,
                  note=note)
        self.runs.append(run)
        return run

    def unplace(self, what: str, room: Room | None, level: str | None, reason: str) -> None:
        self.unplaced.append(Unplaced(what=what, room_key=room.key if room else None, level=level, reason=reason))

    def unroute(self, what: str, reason: str, *, start: str | None = None, end: str | None = None,
                level: str | None = None) -> None:
        self.unrouted.append(Unrouted(what=what, from_key=start, to_key=end, level=level, reason=reason))

    # ---------------------------------------------------------------- rooms and storeys

    def classes(self, room: Room) -> list[str]:
        return self.room_classes.get(room.key, room.classes)

    def rooms_of(self, *classes: str, level: str | None = None) -> list[Room]:
        return [r for r in self.fit_rooms if (level is None or r.level == level)
                and any(c in classes for c in self.classes(r))]

    def elev(self, level: str) -> float:
        return self.levels[level]

    def slab_above(self, room: Room) -> float:
        """Thickness of the slab over a room: the floor op on the next level up covering its point."""
        above = [n for n in self.levels if self.levels[n] > room.elevation_mm + 1]
        if above:
            nxt = min(above, key=lambda n: (self.levels[n], n))
            for op in self.ops_floor:
                if op.level == nxt and Polygon(op.points).covers(SPoint(room.point)):
                    t = self.floor_types[op.type].thickness_mm if op.type in self.floor_types else \
                        type_thickness(op.type)
                    if t:
                        return float(t)
        self.assume("fitout:slab_thickness", f"Slabs whose thickness the plan does not give are taken as "
                    f"{DEFAULT_SLAB_MM:g} mm (ceiling heights for lights and fans)", DEFAULT_SLAB_MM,
                    "Plinth placeholder: typical 150 mm residential RCC slab", [room.key])
        return DEFAULT_SLAB_MM

    def ceiling(self, room: Room) -> float:
        """Height of the room's ceiling (underside of the slab over it) above its level."""
        h = room.height_mm or 3000.0
        return h - self.slab_above(room)

    def wall_function(self, wall_key: str | None) -> str | None:
        """"exterior" or "interior": the wall's type's function in the plan (its ``wall_type`` op, else the word in
        the type's name); None when the plan does not say."""
        w = self.walls.get(wall_key or "")
        if w is None:
            return None
        t = self.wall_types.get(w.type or "")
        if t is not None:
            return t.function
        name = (w.type or "").lower()
        return "interior" if "interior" in name else "exterior" if "exterior" in name else None

    def exterior(self, f: Face) -> bool:
        """A face on the building's outer skin: a wall face with the outside beyond it (``outer``), on a wall the plan
        does not type as interior. A recess or court between two wings drawn with interior walls is outside the
        rooms but not the building's face: an outdoor unit or an outside stack does not hang there unless answered."""
        return f.kind == "wall" and f.outer and self.wall_function(f.wall_key) != "interior"

    def wall_top(self, wall_key: str | None, level: str) -> float | None:
        """Height of a wall's top above ``level`` (a parapet's 900 mm, a storey wall's storey height)."""
        w = self.walls.get(wall_key or "")
        if w is None:
            return None
        if w.height_mm is not None:
            top = self.levels[w.level] + w.base_offset_mm + w.height_mm
        elif w.top_level in self.levels:
            top = self.levels[w.top_level] + w.top_offset_mm
        else:
            return None
        return top - self.levels[level]

    def outdoors(self, room: Room) -> bool:
        """The room is open to the sky: a roof terrace (``OPEN_CLASSES``), or a room no slab of a higher level
        covers whose walls are a parapet (lower than ``OPEN_WALLS_MM``). It has no ceiling for a light or a fan."""
        if any(c in OPEN_CLASSES for c in self.classes(room)):
            return True
        if room.height_mm is not None and room.height_mm >= OPEN_WALLS_MM:
            return False
        return not any(self.levels[op.level] > room.elevation_mm + 1 and Polygon(op.points).covers(SPoint(room.point))
                       for op in self.ops_floor)

    def light_kind(self, room: Room) -> str | None:
        """The light fitting the room takes (``electrical.light_kinds``): the outdoor kind in an open room, else that
        of its first class listed; None: the generic light."""
        lk = self.rules.electrical.light_kinds
        if lk is None:
            return None
        if self.outdoors(room):
            return lk.outdoor
        return next((lk.classes[c] for c in self.classes(room) if c in lk.classes), None)

    def storey_height(self, level: str) -> float:
        hs = Counter(round(r.height_mm) for r in self.model.on_level(level) if r.height_mm)
        return float(hs.most_common(1)[0][0]) if hs else 3000.0

    def footprint(self, level: str):
        """The storey's plan inside its outer walls: rooms with their walls (and the ducts and openings in them)."""
        if level not in self._footprints:
            parts = [Polygon(r.polygon).buffer(400, join_style="mitre") for r in self.model.on_level(level)
                     if r.polygon]
            parts += [d.poly.buffer(400, join_style="mitre") for d in self.ducts]
            shape = unary_union(parts).buffer(-150, join_style="mitre") if parts else Polygon()
            self._footprints[level] = shape.buffer(INSIDE_TOL_MM)
        return self._footprints[level]

    def inside(self, level: str, path: list[Point]) -> bool:
        shape = self.footprint(level)
        if len(path) == 1:
            return shape.covers(SPoint(path[0]))
        return shape.covers(LineString(path))

    def doors(self, level: str) -> list[tuple[Polygon, float, float, str]]:
        """Each door on a level as (box through its wall, bottom z, head z, key), absolute z."""
        if level not in self._doors:
            seen: dict[str, tuple[Polygon, float, float, str]] = {}
            z = self.levels[level]
            for r in self.model.on_level(level):
                for o in r.doors:
                    if o.key in seen or o.face is None:
                        continue
                    f = r.faces[o.face]
                    t = f.thickness_mm or 250
                    box_ = Polygon([o.left, o.right, add(o.right, f.inward, -t), add(o.left, f.inward, -t)])
                    seen[o.key] = (box_, z, z + (o.height_mm or DEFAULT_DOOR_HEAD_MM) + 50, o.key)
            self._doors[level] = list(seen.values())
        return self._doors[level]

    def _structure(self) -> list[tuple[Polygon, float, float, str]]:
        """Every column and beam as (plan outline, bottom z, top z, key): a column's rectangle from its type's size
        ("RCC Column 250x500": 250 along its rotation), from its base to its top; a beam's along its axis, its width
        and depth from its type ("RCC Beam 250x400"), hanging below its level. Members whose size the type does not
        give are left out."""
        out = []
        for op in self.plan.ops:
            if isinstance(op, ColumnOp) and op.level in self.levels and op.top_level in self.levels:
                b, h = type_size(op.type)
                if not b or not h:
                    continue
                a = math.radians(op.rotation_deg)
                u, v = (math.cos(a), math.sin(a)), (-math.sin(a), math.cos(a))
                corners = [add(add(op.point, u, sx * b / 2), v, sy * h / 2)
                           for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
                out.append((Polygon(corners), self.levels[op.level] + op.base_offset_mm,
                            self.levels[op.top_level] + op.top_offset_mm, op.key))
            elif isinstance(op, BeamOp) and op.level in self.levels:
                b, h = type_size(op.type)
                if not b or not h:
                    continue
                d = unit(sub(op.end, op.start))
                n = (-d[1], d[0])
                corners = [add(op.start, n, b / 2), add(op.end, n, b / 2), add(op.end, n, -b / 2),
                           add(op.start, n, -b / 2)]
                z = self.levels[op.level]
                out.append((Polygon(corners), z - h, z, op.key))
        return out

    def obstacles(self, zlo: float, zhi: float, size: float = 0.0) -> list[tuple[Polygon, str]]:
        """The columns and beams a run between heights ``zlo`` and ``zhi`` (absolute) meets, each outline grown by
        the run's radius (``size`` / 2) and ``PIPE_CLEAR_MM``."""
        key_ = (round(zlo), round(zhi), round(size))
        cache = self.__dict__.setdefault("_obstacles", {})
        if key_ not in cache:
            grow = size / 2 + PIPE_CLEAR_MM
            cache[key_] = [(poly.buffer(grow, join_style="mitre"), key) for poly, z0, z1, key in self.structure
                           if zhi > z0 + 1 and zlo < z1 - 1]
        return cache[key_]

    def crossings(self, points: list[P3]) -> list[str]:
        """Doors a polyline passes through (in plan and within the door's height)."""
        out = []
        for a, b in zip(points, points[1:]):
            zlo, zhi = min(a[2], b[2]), max(a[2], b[2])
            line = LineString([a[:2], b[:2]]) if math.dist(a[:2], b[:2]) > 0.5 else SPoint(a[:2])
            for level in self.storeys:
                for box_, z0, z1, key in self.doors(level):
                    if zhi > z0 and zlo < z1 and box_.buffer(-2).intersects(line) and key not in out:
                        out.append(key)
        return out

    # ---------------------------------------------------------------- roof and ducts

    def _roof(self) -> str | None:
        terr = [r for r in self.fit_rooms if "terrace" in self.classes(r)]
        if terr:
            return max((r.level for r in terr), key=lambda n: (self.levels[n], n))
        self.ask("fitout:roof_level", "fitout_roof", "Which level is the building's roof? No room of the plan is a "
                 "roof terrace, so the overhead tank, rainwater outlets and stack vents have no roof to stand on.",
                 options=self.storeys)
        given = self.answer("fitout:roof_level")
        return given if given in self.levels else None

    def roof_terraces(self) -> list[Room]:
        return [r for r in self.fit_rooms if self.roof and r.level == self.roof and "terrace" in self.classes(r)]

    def shaft_kind(self, ops: list[ShaftOp]) -> str | None:
        for op in ops:
            if op.key in self.shaft_kinds:
                return self.shaft_kinds[op.key]
        qid = f"fitout:shaft_kind:{ops[0].key}"
        given = self.answer(qid)
        if given in ("duct", "lift", "stair", "other"):
            return given
        poly = Polygon(ops[0].points)
        for st in self.stairs:
            pts = [q for run in st.runs for q in (run.start, run.end)]
            if pts and Polygon(poly).buffer(st.width_mm).contains(SPoint(pts[0])):
                return "stair"
        self.ask(qid, "fitout_shaft_kind", "What is the vertical shaft at "
                 f"({poly.centroid.x:.0f}, {poly.centroid.y:.0f})? The plan does not say whether it is a service duct "
                 "(for soil, waste, vent and water pipes), a lift or a stair.",
                 options=["duct", "lift", "stair", "other"], instances=[op.key for op in ops])
        return None

    def _ducts(self) -> list[Duct]:
        groups: dict[tuple, list[ShaftOp]] = defaultdict(list)
        for op in self.plan.ops:
            if isinstance(op, ShaftOp):
                groups[tuple(sorted(pt(p) for p in op.points))].append(op)
        ducts: list[Duct] = []
        spacing = self.rules.plumbing.stacks.spacing_mm
        for outline, ops in sorted(groups.items(), key=lambda kv: kv[1][0].key):
            if self.shaft_kind(ops) == "duct":
                levels = sorted({op.base_level for op in ops}, key=lambda n: self.levels[n])
                ducts.append(Duct(ops[0].key, [tuple(p) for p in ops[0].points], levels, spacing))
        for r in self.fit_rooms:
            if "shaft" in self.classes(r) and not any(d.poly.buffer(50).contains(SPoint(r.point)) for d in ducts):
                ducts.append(Duct(r.key, list(r.polygon), [r.level], spacing))
        return ducts

    def duct_beyond(self, room: Room, f: Face, s: float) -> Duct | None:
        """The duct on the far side of a room's wall face at ``s`` along it (an open-to-sky duct ventilates)."""
        q = add(add(f.start, f.direction, s), f.inward, -((f.thickness_mm or 250) + 100))
        return next((d for d in self.ducts if d.poly.covers(SPoint(q))), None)


# ------------------------------------------------------------------------------------------------ wall spots

class Spot:
    __slots__ = ("face", "s", "point")

    def __init__(self, face: int, s: float, point: Point):
        self.face, self.s, self.point = face, s, point


def opening_span(room: Room, o: Opening) -> tuple[float, float, float, float]:
    """(s0, s1) along its face and (bottom, top) above the floor of a door or window."""
    f = room.faces[o.face]
    s0, s1 = sorted((along(f, o.left), along(f, o.right)))
    if o.kind == "door":
        return s0, s1, 0.0, (o.height_mm or DEFAULT_DOOR_HEAD_MM)
    sill = o.sill_mm if o.sill_mm is not None else 0.0
    head = o.head_mm if o.head_mm is not None else sill + (o.height_mm or 1200)
    return s0, s1, sill, head


def wall_spots(ctx: Ctx, room: Room, width: float, bottom: float, top: float, *, step: float = 100.0,
               faces: Iterable[int] | None = None, furniture: bool = True, skip: list[tuple[int, float, float]] = (),
               ) -> list[Spot]:
    """Positions along the room's wall faces where a device ``width`` wide, from ``bottom`` to ``top`` above the
    floor, keeps clear of the face's ends, its doors and windows (at those heights) and the backs of furniture taller
    than ``LOW_FURNITURE_MM`` (at those heights); ``skip``: (face, s0, s1) spans already taken."""
    out: list[Spot] = []
    idx = list(faces) if faces is not None else range(len(room.faces))
    for fi in idx:
        f = room.faces[fi]
        if f.kind != "wall" or f.length_mm < width + 2 * CORNER_MM:
            continue
        blocks: list[tuple[float, float]] = []
        for o in room.openings:
            if o.face != fi:
                continue
            s0, s1, b, t = opening_span(room, o)
            if t > bottom and b < top:
                blocks.append((s0 - OPENING_MARGIN_MM, s1 + OPENING_MARGIN_MM))
        if furniture:
            for p in ctx.furn_by_room.get(room.key, []):
                low = p.height_mm + p.elevation_mm <= LOW_FURNITURE_MM
                if (p.wall_key, p.wall_side) != (f.wall_key, f.side) or low:
                    continue
                if abs(dot(sub(p.insertion, f.start), f.inward)) > 5:
                    continue  # its back is on another face of the same wall
                if p.elevation_mm + p.height_mm + 50 <= bottom or p.elevation_mm >= top:
                    continue
                c = along(f, p.insertion)
                blocks.append((c - p.width_mm / 2, c + p.width_mm / 2))
        blocks += [(s0, s1) for fj, s0, s1 in [*skip, *taken(ctx, room, bottom, top)] if fj == fi]
        s = CORNER_MM + width / 2
        while s <= f.length_mm - CORNER_MM - width / 2 + 1e-6:
            if not any(s + width / 2 > b0 and s - width / 2 < b1 for b0, b1 in blocks):
                out.append(Spot(fi, s, pt(add(f.start, f.direction, s))))
            s += step
    return out


def taken(ctx: Ctx, room: Room, bottom: float, top: float) -> list[tuple[int, float, float]]:
    """Spans of the room's faces already holding a wall device at heights overlapping ``bottom``-``top``."""
    out = []
    for d in ctx.devices:
        if d.room_key != room.key or d.host.kind != "wall":
            continue
        w, h = (d.size.width_mm, d.size.height_mm) if d.size else (100.0, 100.0)
        if d.elevation_mm + h / 2 + 50 <= bottom or d.elevation_mm - h / 2 - 50 >= top:
            continue
        for fi, f in enumerate(room.faces):
            if (f.wall_key, f.side) == (d.host.wall_key, d.host.side) and \
                    abs(dot(sub(d.insertion, f.start), f.inward)) < 5:
                s = along(f, d.insertion)
                if -1 <= s <= f.length_mm + 1:
                    out.append((fi, s - w / 2 - OPENING_MARGIN_MM, s + w / 2 + OPENING_MARGIN_MM))
    return out


def common_core(ctx: Ctx) -> Polygon | None:
    """Where the common circulation (lobby, stair) stands on every storey with flats: risers go up through it."""
    storeys = [s for s in ctx.storeys if any(r.flat for r in ctx.model.on_level(s))]
    shape = None
    for s in storeys:
        here = [Polygon(r.polygon) for r in ctx.fit_rooms if r.level == s and not r.flat
                and any(c in ("lobby", "stair") for c in ctx.classes(r))]
        if not here:
            return None
        u = unary_union(here)
        shape = u if shape is None else shape.intersection(u)
    if shape is None or shape.is_empty:
        return None
    inner = shape.buffer(-200, join_style="mitre")
    return inner if not inner.is_empty and inner.area > 0 else None


def core_points(core: Polygon, near: Point) -> list[Point]:
    """Corners of the common core, nearest to ``near`` first (risers stand in its corners)."""
    geoms = list(core.geoms) if hasattr(core, "geoms") else [core]
    pts = [pt(c) for g in geoms for c in list(g.exterior.coords)[:-1]]
    return sorted(dict.fromkeys(pts), key=lambda p: (math.dist(p, near), p))


def nearest_spot(spots: list[Spot], target: Point, taken: list[Point] = (), min_gap: float = 0.0) -> Spot | None:
    ok = [sp for sp in spots if all(math.dist(sp.point, q) >= min_gap for q in taken)]
    return min(ok, key=lambda sp: (math.dist(sp.point, target), sp.face, sp.s), default=None)


def spread(spots: list[Spot], n: int, start_from: list[Point], prefer: list[Point] = (),
           min_gap: float = 600.0) -> list[Spot]:
    """``n`` spots: first the nearest to each preferred point, then each next the farthest from those chosen (and
    from ``start_from``: the doors), so points spread round the room."""
    chosen: list[Spot] = []
    for q in prefer:
        if len(chosen) >= n:
            break
        sp = nearest_spot(spots, q, [c.point for c in chosen], min_gap)
        if sp is not None and math.dist(sp.point, q) <= 1500:
            chosen.append(sp)
    while len(chosen) < n:
        refs = [c.point for c in chosen] or list(start_from)
        best = None
        for sp in spots:
            if any(sp is c for c in chosen):
                continue
            d = min((math.dist(sp.point, q) for q in refs), default=0.0)
            if chosen and d < min_gap:
                continue
            key = (-d, sp.face, sp.s)
            if best is None or key < best[0]:
                best = (key, sp)
        if best is None:
            break
        chosen.append(best[1])
    return chosen


def ceiling_points(room: Room, n: int, avoid: list[Point] = (), gap: float = 600.0) -> list[Point]:
    """``n`` points on a grid over the room (rows across its short side), each moved inside the room when the grid
    cell's centre falls outside an L-shaped room, and off any ``avoid`` point (a fan) along the room's long side."""
    if n <= 0:
        return []
    shape = Polygon(room.polygon, room.holes)
    for ob in room.obstacles:
        shape = shape.difference(Polygon(ob))
    inner = shape.buffer(-300, join_style="mitre")
    if inner.is_empty:
        inner = shape
    minx, miny, maxx, maxy = shape.bounds
    W, H = maxx - minx, maxy - miny
    long_x = W >= H
    L, S = (W, H) if long_x else (H, W)
    rows = max(1, min(n, round(math.sqrt(n * S / L)) if L else 1))
    cols = math.ceil(n / rows)
    pts: list[Point] = []
    for i in range(rows):
        in_row = cols if i < rows - 1 else n - cols * (rows - 1)
        for j in range(in_row):
            a = (j + 0.5) / in_row * L
            b = (i + 0.5) / rows * S
            p = (minx + a, miny + b) if long_x else (minx + b, miny + a)
            pts.append(p)
    out = []
    step = (L / 4, 0.0) if long_x else (0.0, L / 4)
    for p in pts:
        if not inner.covers(SPoint(p)):
            p = nearest_points(inner, SPoint(p))[0].coords[0]
        if any(math.dist(p, q) < gap for q in avoid):
            for k in (1, -1):
                q = add(p, step, k)
                if inner.covers(SPoint(q)) and not any(math.dist(q, a) < gap for a in avoid):
                    p = q
                    break
        out.append(pt(p))
    return out


def centre_of(room: Room) -> Point:
    shape = Polygon(room.polygon, room.holes)
    c = shape.centroid
    if shape.buffer(-300).covers(c):
        return pt((c.x, c.y))
    return pt(shape.representative_point().coords[0])


def face_point(f: Face, s: float, out: float = 0.0) -> Point:
    """The point ``s`` along a face, ``out`` into the room."""
    return add(add(f.start, f.direction, s), f.inward, out)
