"""The rooms of a build plan, as fit-out needs them (PRD Step 10, F2 "Rooms").

    from plinth.fitout import rooms, rules
    model = rooms.build(plan, rules.load("west-bengal"), answers)   # answers: {"fitout:...": value}
    for room in model.rooms: room.classes, room.polygon, room.faces, room.openings, room.flat
    model.questions                                                  # what the plan does not tell (asked, never guessed)

Why from the build plan alone: a model made from a brief and one made from DWGs both end as a ``BuildPlan``, so the
fit-out of either reads the same ops (walls with their types, doors and windows with their types and flips, rooms with
their points, room separation lines) and needs nothing from Revit or the drawings. The furniture solver (F3) and the
MEP layout (F4) both work on this model.

How each part is found:
- Class: from the room's name by the rule set (``FitoutRules.classify``); a name no class matches is asked, once per
  name (``fitout:room_class:<normalised name>``; answer a class id, a list of them, or "none").
- Polygon: per level, every wall standing on it becomes its rectangle (centreline +- thickness / 2, shifted when the
  wall's location line is a face: Revit keeps a wall's exterior on the left of start -> end); the union of the
  rectangles (closed by 25 mm, so rounding or a near miss between wall ends leaves no slit, as Revit joins them) is cut out of a box round the level, room
  separation lines cut the rest, and a room is the piece holding its point. That is the room Revit computes from the
  same walls (doors do not break a boundary), and the area the drawings label. A piece that reaches the box's edge is
  not enclosed (no polygon); two room points in one piece share it (the first by key is furnished with both rooms'
  classes). Columns are not room bounding here: where one stands out of its wall into the room it is an obstacle.
- Thickness: the wall's type's ``wall_type`` op, else the number in its type name ("Plinth 250mm Exterior", "Brick 230");
  otherwise asked (``fitout:wall_thickness:<type>``) and the level's rooms wait.
- Faces: the polygon's edges (anticlockwise, the room on their left), each split where it leaves one wall for the next,
  with the wall's key and the side of the wall (left/right of its start -> end) the room is on; an edge across a
  wall's end is "wall_end", one on a room separation line "separator", others "open". ``outer``: beyond the wall lies no enclosed space (outside).
- Doors: position on the host wall from ``offset_mm``; width from the ``opening_type`` op, else the type name
  ("Door 900x2100", "door-1025-sliding"), else the rule set's ``unknown_door_width_mm`` (listed as an assumption and
  asked). Leaf side and hinge come from the flips, read with the convention ``plinth.imported.to_plan`` writes them in
  (DEFAULT_OPENS_LEFT / DEFAULT_HINGE_AT_END): unflipped, the leaf opens to the wall's right of start -> end with its
  hinge at the end-side jamb; ``flip_facing`` moves the leaf to the left, ``flip_hand`` the hinge to the start side.
  The swing is a quarter disc of radius = width about the hinge on the face of the side it opens to. The plan's door
  op says nothing else, so a single leaf is assumed; a sliding door (type hint or name) has no swing.
- Windows: position and width as doors; sill from the op, head = sill + the type's height.
- Flat: the room op's ``flat`` (to_plan's unit, the engine's flat letter). Without one: a stair, lift, shaft, terrace
  ... (classes not in a flat) are common; any other room takes the flat of the rooms it reaches through doors, room
  separation lines and open edges without passing a common room or a lobby, when that is one flat; a lobby is then
  common when it holds or opens onto a stair or lift, or into rooms of more than one flat, else it is of the one flat
  it opens into. On a level with flats, a room left without one is asked (``fitout:flat:<room key>``, answer a flat
  or "common"); a level without any flat (a stilt floor) asks nothing.

Points are plan millimetres; areas m²; elevations mm. The model is deterministic: the same plan, rules and answers give
the same rooms, faces and openings in the same order.
"""

from __future__ import annotations

import math
import re
from collections import Counter, defaultdict
from typing import Any, Literal, Mapping

from pydantic import BaseModel, ConfigDict
from shapely import prepared
from shapely.geometry import LineString, MultiPolygon, Point as SPoint, Polygon, box
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

from plinth.fitout.rules import FitoutRules, normalise
from plinth.imported.to_plan import DEFAULT_HINGE_AT_END, DEFAULT_OPENS_LEFT
from plinth.plan import BuildPlan, ColumnOp, DoorOp, LevelOp, OpeningTypeOp, RoomOp, RoomSeparatorOp, WallOp, \
    WallTypeOp, WindowOp

Point = tuple[float, float]
Answers = Mapping[str, Any]

CLOSE_MM = 25.0  # wall bodies are closed by this much: slits under 50 mm between wall ends close (Revit joins them)
SEPARATOR_HALF_MM = 0.01  # a room separation line cuts the free space this wide (area lost: nil)
FACE_TOL_MM = 3.0  # an edge lies on a wall face (or a separation line) within this
PROBE_MM = 50.0  # beyond a face by this much to see what lies there
BOX_MARGIN_MM = 10_000.0
MIN_PIECE_MM = 1.0  # face pieces shorter than this are dropped
BASE_TOL_MM = 300.0  # a wall stands on a level when its base is within this above the level
ARC_SEGMENTS = 12


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class Question(_Model):
    """Something the plan does not tell; ``id`` is the answer key (fit-out answers are ``fitout:...``). Shaped like the
    import's stage questions (``plinth.imported.stage_questions.merge`` items) so F6 asks them like any other."""

    id: str
    topic: str
    text: str
    options: list[Any] = []
    instances: list[str] = []  # op keys it concerns


class Assumption(_Model):
    """A value taken from the rule set where the plan gives none; listed back for review."""

    id: str
    text: str
    value: Any
    source: str
    instances: list[str] = []


class Face(_Model):
    """A straight piece of a room's boundary, anticlockwise round the room (the room on its left)."""

    start: Point
    end: Point
    kind: Literal["wall", "wall_end", "separator", "open"]
    wall_key: str | None = None
    side: Literal["left", "right"] | None = None  # which side of its wall (seen from the wall's start) the room is on
    thickness_mm: float | None = None
    outer: bool = False  # beyond this face is outside the building

    @property
    def length_mm(self) -> float:
        return math.dist(self.start, self.end)

    @property
    def direction(self) -> Point:
        L = self.length_mm or 1.0
        return ((self.end[0] - self.start[0]) / L, (self.end[1] - self.start[1]) / L)

    @property
    def inward(self) -> Point:
        """Unit normal pointing into the room."""
        d = self.direction
        return (-d[1], d[0])


class Opening(_Model):
    """A door or window in a wall bounding a room, seen from that room: points lie on the room's face of the wall."""

    key: str
    kind: Literal["door", "window"]
    type: str
    wall_key: str
    face: int | None  # index into the room's faces (None: the opening's wall is not on the room's polygon)
    width_mm: float
    height_mm: float | None = None
    center: Point  # middle of the opening on the room's face
    along: Point  # unit vector along the wall (its start -> end)
    left: Point  # jamb towards the wall's start, on the room's face
    right: Point  # jamb towards the wall's end, on the room's face
    sill_mm: float | None = None  # windows
    head_mm: float | None = None  # windows: sill + height
    sliding: bool = False  # doors
    hinge: Point | None = None  # hinged doors: the hinge jamb, on the room's face
    latch: Point | None = None  # hinged doors: the latch (lock) jamb, on the room's face
    swings_in: bool = False  # the leaf swings into this room
    swing: list[Point] | None = None  # when it swings in: the leaf's sweep (quarter disc), as a polygon
    other_room: str | None = None  # the room on the wall's other side (None: outside or none)
    width_assumed: bool = False


class Room(_Model):
    key: str
    name: str
    number: str | None = None
    level: str
    elevation_mm: float
    height_mm: float | None = None  # floor to the top of its walls
    point: Point
    classes: list[str] = []  # from the name (or an answer); [] while asked
    status: Literal["ok", "not_enclosed", "shared", "in_wall", "waiting"] = "ok"
    polygon: list[Point] | None = None  # outer ring, anticlockwise, not closed
    holes: list[list[Point]] = []
    area_m2: float | None = None
    faces: list[Face] = []
    openings: list[Opening] = []
    obstacles: list[list[Point]] = []  # column parts standing in the room
    shared_with: list[str] = []  # other rooms whose point lies in the same space
    flat: str | None = None
    flat_source: Literal["plan", "connected", "common", "answer"] | None = None
    neighbours: list[str] = []  # rooms reached through a door, a separation line or an open edge

    @property
    def cls(self) -> str | None:
        return self.classes[0] if self.classes else None

    @property
    def doors(self) -> list[Opening]:
        return [o for o in self.openings if o.kind == "door"]

    @property
    def windows(self) -> list[Opening]:
        return [o for o in self.openings if o.kind == "window"]

    def shape(self) -> Polygon | None:
        return Polygon(self.polygon, self.holes) if self.polygon else None


class RoomModel(_Model):
    rule_set: str
    levels: dict[str, float]  # name -> elevation
    rooms: list[Room]
    questions: list[Question] = []
    assumptions: list[Assumption] = []

    def by_key(self) -> dict[str, Room]:
        return {r.key: r for r in self.rooms}

    def on_level(self, level: str) -> list[Room]:
        return [r for r in self.rooms if r.level == level]

    def flats(self) -> dict[str, list[str]]:
        """Flat -> its rooms' keys, by level then key."""
        out: dict[str, list[str]] = defaultdict(list)
        for r in self.rooms:
            if r.flat:
                out[r.flat].append(r.key)
        return dict(out)


# ------------------------------------------------------------------------------------------------ helpers

def _num(text: str | None, pattern: str) -> float | None:
    m = re.search(pattern, text or "", re.I)
    return float(m.group(1)) if m else None


def type_thickness(name: str | None) -> float | None:
    """Wall thickness from a type name: "Plinth 250mm Exterior" -> 250, "Brick 230" -> 230."""
    if not name:
        return None
    t = _num(name, r"(\d+(?:\.\d+)?)\s*mm\b")
    if t is None:
        nums = re.findall(r"(?<![\d.x])(\d{2,4}(?:\.\d+)?)(?![\d.]|\s*[x×])", name, re.I)
        t = float(nums[-1]) if len(nums) == 1 else None
    return t if t and t > 0 else None


def type_size(name: str | None) -> tuple[float | None, float | None]:
    """Opening width and height from a type name: "Door 900x2100" -> (900, 2100), "door-1025-sliding" -> (1025, None)."""
    m = re.search(r"(\d{3,4})\s*[x×]\s*(\d{3,4})", name or "", re.I)
    if m:
        return float(m.group(1)), float(m.group(2))
    return _num(name, r"(?:door|window|dr|win)[\s_-]*(\d{3,4})\b"), None


def _sub(a: Point, b: Point) -> Point:
    return (a[0] - b[0], a[1] - b[1])


def _add(a: Point, b: Point, k: float = 1.0) -> Point:
    return (a[0] + b[0] * k, a[1] + b[1] * k)


def _dot(a: Point, b: Point) -> float:
    return a[0] * b[0] + a[1] * b[1]


def _cross(a: Point, b: Point) -> float:
    return a[0] * b[1] - a[1] * b[0]


def _r(p: Point) -> Point:
    return (round(p[0], 2), round(p[1], 2))


def quarter_disc(hinge: Point, towards: Point, out: Point, radius: float) -> list[Point]:
    """The sweep of a leaf hinged at ``hinge``, closed along unit ``towards`` (to the latch), opening to unit ``out``."""
    a0 = math.atan2(towards[1], towards[0])
    a1 = math.atan2(out[1], out[0])
    delta = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi  # +-90 degrees
    pts = [hinge]
    for i in range(ARC_SEGMENTS + 1):
        a = a0 + delta * i / ARC_SEGMENTS
        pts.append((hinge[0] + radius * math.cos(a), hinge[1] + radius * math.sin(a)))
    return [_r(p) for p in pts]


class _Wall:
    def __init__(self, op: WallOp, thickness: float):
        self.op, self.key, self.t = op, op.key, thickness
        s, e = op.start, op.end
        L = math.dist(s, e)
        self.length = L
        self.u = ((e[0] - s[0]) / L, (e[1] - s[1]) / L) if L else (1.0, 0.0)
        self.n = (-self.u[1], self.u[0])  # left of start -> end
        shift = {"finish_face_exterior": -1, "core_face_exterior": -1,
                 "finish_face_interior": 1, "core_face_interior": 1}.get(op.location_line, 0)
        # Revit keeps a wall's exterior on the left of start -> end: with an exterior face as location line the body
        # lies to the right of the drawn line, with an interior face to its left.
        self.s = _add(s, self.n, shift * thickness / 2)
        self.e = _add(e, self.n, shift * thickness / 2)

    def rect(self) -> Polygon:
        h = self.t / 2
        return Polygon([_add(self.s, self.n, h), _add(self.e, self.n, h), _add(self.e, self.n, -h),
                        _add(self.s, self.n, -h)])

    def at(self, offset: float) -> Point:
        return _add(self.s, self.u, offset)


def _corner_fills(walls: list[_Wall]) -> list[Polygon]:
    """The pieces Revit's join adds where two walls' free ends meet at an L corner without touching.

    Revit joins two non-parallel walls whose ends lie near each other (here: each end within the other wall's
    thickness plus 50 mm of where the centrelines cross), extending both to the corner. Without that piece a room
    leaks through the corner (the G+4 sample's live plan: a wall put across a flat's entrance door gap ended 125 mm
    short of the wall it turns into, and the lobby and two flats read as one 125 m2 space where Revit has three rooms
    of 31.8, 46.7 and 46.7 m2)."""
    out: list[Polygon] = []
    for i, a in enumerate(walls):
        for b in walls[i + 1:]:
            cross = a.u[0] * b.u[1] - a.u[1] * b.u[0]
            if abs(cross) < 0.5:  # parallel or nearly: no L corner
                continue
            dx, dy = b.s[0] - a.s[0], b.s[1] - a.s[1]
            ta = (dx * b.u[1] - dy * b.u[0]) / cross  # where the centrelines cross, along each wall
            tb = (dx * a.u[1] - dy * a.u[0]) / cross
            beyond_a = -ta if ta < 0 else (ta - a.length if ta > a.length else 0.0)
            beyond_b = -tb if tb < 0 else (tb - b.length if tb > b.length else 0.0)
            if beyond_a == 0 and beyond_b == 0:
                continue  # crossing inside both walls: they already meet
            if beyond_a > b.t + 50 or beyond_b > a.t + 50:
                continue
            corner = _add(a.s, a.u, ta)
            for w, t_w, other in ((a, ta, b), (b, tb, a)):
                if 0 <= t_w <= w.length:
                    continue  # this wall already reaches the corner
                end = w.s if t_w < 0 else w.e
                out_dir = (-w.u[0], -w.u[1]) if t_w < 0 else w.u
                far = _add(corner, out_dir, other.t / 2)
                h = w.t / 2
                out.append(Polygon([_add(end, w.n, h), _add(far, w.n, h), _add(far, w.n, -h), _add(end, w.n, -h)]))
    return [g for g in out if g.is_valid and g.area > 1.0]


# ------------------------------------------------------------------------------------------------ the builder

def build(plan: BuildPlan, rules: FitoutRules, answers: Answers | None = None) -> RoomModel:
    return _Builder(plan, rules, dict(answers or {})).run()


class _Builder:
    def __init__(self, plan: BuildPlan, rules: FitoutRules, answers: dict[str, Any]):
        self.plan, self.rules, self.answers = plan, rules, answers
        ops = plan.ops
        self.levels = {op.name: op.elevation_mm for op in ops if isinstance(op, LevelOp)}
        self.wall_types = {op.name: op for op in ops if isinstance(op, WallTypeOp)}
        self.opening_types = {(op.category, op.name): op for op in ops if isinstance(op, OpeningTypeOp)}
        self.walls_all = [op for op in ops if isinstance(op, WallOp)]
        self.hosted = [op for op in ops if isinstance(op, (DoorOp, WindowOp))]
        self.room_ops = [op for op in ops if isinstance(op, RoomOp)]
        self.separators = [op for op in ops if isinstance(op, RoomSeparatorOp)]
        self.columns = [op for op in ops if isinstance(op, ColumnOp)]
        self.questions: dict[str, Question] = {}
        self.assumptions: dict[str, Assumption] = {}

    # ---------------------------------------------------------------- questions and assumptions

    def ask(self, id_: str, topic: str, text: str, options=(), instances=()) -> None:
        q = self.questions.get(id_)
        inst = sorted(set((q.instances if q else []) + list(instances)))
        self.questions[id_] = Question(id=id_, topic=topic, text=text, options=list(options), instances=inst)

    def assume(self, id_: str, text: str, value: Any, source: str, instances=()) -> None:
        a = self.assumptions.get(id_)
        inst = sorted(set((a.instances if a else []) + list(instances)))
        self.assumptions[id_] = Assumption(id=id_, text=text, value=value, source=source, instances=inst)

    # ---------------------------------------------------------------- values from the plan

    def thickness(self, wall: WallOp) -> float | None:
        name = wall.type
        if name in self.wall_types:
            return self.wall_types[name].thickness_mm
        t = type_thickness(name)
        if t is not None:
            return t
        id_ = f"fitout:wall_thickness:{name or '(default type)'}"
        answer = self.answers.get(id_)
        try:
            if answer is not None and float(answer) > 0:
                return float(answer)
        except (TypeError, ValueError):
            pass
        self.ask(id_, "fitout_wall_thickness",
                 f"How thick (mm) are the walls of type {name or '(the default wall type)'}? The plan neither makes "
                 "the type nor names its thickness, and room shapes need it.", instances=[wall.key])
        return None

    def base_top(self, w: WallOp) -> tuple[float, float]:
        base = self.levels[w.level] + w.base_offset_mm
        top = self.levels[w.top_level] + w.top_offset_mm if w.top_level else base + (w.height_mm or 0)
        return base, top

    def opening_size(self, op: DoorOp | WindowOp) -> tuple[float | None, float | None, bool]:
        """(width, height, sliding) of a door or window from its type."""
        category = op.op
        t = self.opening_types.get((category, op.type))
        name_w, name_h = type_size(op.type)
        width = t.width_mm if t else name_w
        height = t.height_mm if t else name_h
        sliding = bool(t and t.hint == "sliding") or "sliding" in (op.type or "").lower()
        return width, height, sliding

    def classes_of(self, op: RoomOp) -> list[str]:
        found = self.rules.classify(op.name)
        if found:
            return found
        id_ = f"fitout:room_class:{normalise(op.name) or op.name}"
        answer = self.answers.get(id_)
        if answer is not None:
            given = [answer] if isinstance(answer, str) else list(answer)
            if given == ["none"]:
                return []
            known = [c for c in given if c in self.rules.rooms]
            if known:
                return known
        self.ask(id_, "fitout_room_class",
                 f"What kind of room is \"{op.name}\"? Its name matches none of the rule set's room classes, so Plinth "
                 "cannot tell what to furnish it with (answer a class, several, or none).",
                 options=[*self.rules.rooms, "none"], instances=[op.key])
        return []

    # ---------------------------------------------------------------- run

    def run(self) -> RoomModel:
        rooms: list[Room] = []
        by_level: dict[str, list[RoomOp]] = defaultdict(list)
        for op in self.room_ops:
            by_level[op.level].append(op)
        for level in sorted(by_level, key=lambda n: (self.levels[n], n)):
            rooms += self.level_rooms(level, by_level[level])
        rooms = self.assign_flats(rooms)
        order = {op.key: i for i, op in enumerate(self.room_ops)}
        rooms.sort(key=lambda r: (self.levels[r.level], order[r.key]))
        return RoomModel(rule_set=self.rules.id, levels=dict(self.levels), rooms=rooms,
                         questions=sorted(self.questions.values(), key=lambda q: q.id),
                         assumptions=sorted(self.assumptions.values(), key=lambda a: a.id))

    def level_walls(self, level: str) -> list[WallOp]:
        z = self.levels[level]
        out = []
        for w in self.walls_all:
            base, top = self.base_top(w)
            if base <= z + BASE_TOL_MM and top > z + BASE_TOL_MM and base >= z - 1:
                out.append(w)
            elif base < z - 1 and top > z + BASE_TOL_MM:
                out.append(w)  # a wall rising through this level from below
        return out

    def level_rooms(self, level: str, ops: list[RoomOp]) -> list[Room]:
        z = self.levels[level]
        walls: list[_Wall] = []
        waiting = False
        for w in self.level_walls(level):
            t = self.thickness(w)
            if t is None:
                waiting = True
            elif w.start != w.end:
                walls.append(_Wall(w, t))
        base = {op.key: dict(key=op.key, name=op.name, number=op.number, level=level, elevation_mm=z,
                             point=_r(op.point), classes=self.classes_of(op)) for op in ops}
        if waiting or not walls:
            return [Room(**base[op.key], status="waiting" if waiting else "not_enclosed") for op in ops]

        rects = [w.rect() for w in walls] + _corner_fills(walls)
        mass = unary_union(rects)
        mass = mass.buffer(CLOSE_MM, join_style="mitre").buffer(-CLOSE_MM, join_style="mitre")
        minx, miny, maxx, maxy = mass.bounds
        frame = box(minx - BOX_MARGIN_MM, miny - BOX_MARGIN_MM, maxx + BOX_MARGIN_MM, maxy + BOX_MARGIN_MM)
        free = frame.difference(mass)
        seps = [LineString(s.points + ([s.points[0]] if s.close_loop else [])) for s in self.separators
                if s.level == level]
        if seps:
            free = free.difference(unary_union(seps).buffer(SEPARATOR_HALF_MM, cap_style="flat"))
        pieces = list(free.geoms) if isinstance(free, MultiPolygon) else [free]
        pieces = [p for p in pieces if p.area > 1.0]
        outside = [p for p in pieces if p.bounds == frame.bounds or p.touches(frame.exterior)
                   or p.intersects(frame.exterior)]
        outside_union = unary_union(outside) if outside else Polygon()
        prepared_pieces = [(i, prepared.prep(p)) for i, p in enumerate(pieces)]

        def piece_at(q: Point) -> int | None:
            pt = SPoint(q)
            return next((i for i, pp in prepared_pieces if pp.covers(pt)), None)

        outside_ids = {i for i, p in enumerate(pieces) if any(p is o for o in outside)}
        holders: dict[int, list[str]] = defaultdict(list)
        where: dict[str, int | None] = {}
        for op in ops:
            i = piece_at(op.point)
            where[op.key] = i
            if i is not None and i not in outside_ids:
                holders[i].append(op.key)

        level_seps = [s for s in seps]
        heights = {w.key: self.base_top(w.op)[1] - z for w in walls}
        wall_by_key = {w.key: w for w in walls}
        room_of_piece = {i: keys[0] for i, keys in holders.items()}

        def room_at(q: Point) -> str | None:
            i = piece_at(q)
            return room_of_piece.get(i) if i is not None else None

        def is_outside(q: Point) -> bool:
            i = piece_at(q)
            return i is not None and i in outside_ids

        out: list[Room] = []
        for op in ops:
            i = where[op.key]
            if i is None:
                out.append(Room(**base[op.key], status="in_wall"))
                continue
            if i in outside_ids:
                out.append(Room(**base[op.key], status="not_enclosed"))
                continue
            shape = orient(pieces[i], 1.0)
            ring = _canonical(list(shape.exterior.coords)[:-1])
            holes = [[_r(p) for p in h.coords[:-1]] for h in shape.interiors]
            faces = self.faces(ring, walls, level_seps, is_outside, mass)
            shared = [k for k in holders[i] if k != op.key]
            obstacles = self.obstacles(level, shape, mass)
            hs = Counter(round(heights[f.wall_key]) for f in faces if f.wall_key in heights)
            height = float(hs.most_common(1)[0][0]) if hs else None
            out.append(Room(**base[op.key], status="shared" if shared else "ok", polygon=[_r(p) for p in ring],
                            holes=holes, area_m2=round(shape.area / 1e6, 4), faces=faces, obstacles=obstacles,
                            shared_with=shared, height_mm=height))
        # openings, now that every room's faces are known
        rooms = {r.key: r for r in out}
        openings: dict[str, list[Opening]] = defaultdict(list)
        links: dict[str, set[str]] = defaultdict(set)
        for op in self.hosted:
            w = wall_by_key.get(op.host_key)
            if w is None:
                continue
            for room_key, o in self.opening_views(op, w, room_at, rooms):
                openings[room_key].append(o)
                if o.kind == "door" and o.other_room:
                    links[room_key].add(o.other_room)
        for r in out:
            for f in r.faces:
                if f.kind in ("separator", "open"):
                    mid = ((f.start[0] + f.end[0]) / 2, (f.start[1] + f.end[1]) / 2)
                    other = room_at(_add(mid, f.inward, -PROBE_MM))
                    if other and other != r.key:
                        links[r.key].add(other)
                        links[other].add(r.key)
        return [r.model_copy(update={"openings": openings.get(r.key, []),
                                     "neighbours": sorted(links.get(r.key, set()) - {r.key})}) for r in out]

    # ---------------------------------------------------------------- faces

    def faces(self, ring: list[Point], walls: list[_Wall], seps: list[LineString], is_outside, mass) -> list[Face]:
        pieces: list[Face] = []
        n = len(ring)
        for k in range(n):
            a, b = ring[k], ring[(k + 1) % n]
            L = math.dist(a, b)
            if L < MIN_PIECE_MM:
                continue
            v = ((b[0] - a[0]) / L, (b[1] - a[1]) / L)
            spans: list[tuple[float, float, _Wall, str]] = []
            for w in walls:
                if abs(_cross(w.u, v)) > 1e-3:
                    continue
                da, db = _dot(_sub(a, w.s), w.n), _dot(_sub(b, w.s), w.n)
                if abs(abs(da) - w.t / 2) > FACE_TOL_MM or abs(da - db) > FACE_TOL_MM:
                    continue
                t0, t1 = sorted((_dot(_sub(w.s, a), v), _dot(_sub(w.e, a), v)))
                t0, t1 = max(t0, 0.0), min(t1, L)
                if t1 - t0 > MIN_PIECE_MM:
                    spans.append((t0, t1, w, "left" if da > 0 else "right"))
            spans.sort(key=lambda s: (s[0], s[2].key))
            cuts = sorted({0.0, L, *(x for s in spans for x in s[:2])})
            for c0, c1 in zip(cuts, cuts[1:]):
                if c1 - c0 < MIN_PIECE_MM:
                    continue
                mid = (c0 + c1) / 2
                hit = next((s for s in spans if s[0] - 1e-6 <= mid <= s[1] + 1e-6), None)
                p0, p1 = _add(a, v, c0), _add(a, v, c1)
                inward = (-v[1], v[0])
                mpt = _add(a, v, mid)
                if hit:
                    w, side = hit[2], hit[3]
                    probe = _add(mpt, inward, -(w.t + PROBE_MM))
                    if mass.covers(SPoint(probe)):
                        probe = _add(mpt, inward, -(w.t + 6 * PROBE_MM))
                    face = Face(start=_r(p0), end=_r(p1), kind="wall", wall_key=w.key, side=side, thickness_mm=w.t,
                                outer=is_outside(probe))
                elif (end := self.wall_end_at(walls, p0, p1)) is not None:
                    face = Face(start=_r(p0), end=_r(p1), kind="wall_end", wall_key=end.key, thickness_mm=end.t,
                                outer=is_outside(_add(mpt, inward, -PROBE_MM)))
                else:
                    on_sep = any(sep.distance(SPoint(mpt)) <= FACE_TOL_MM for sep in seps)
                    face = Face(start=_r(p0), end=_r(p1), kind="separator" if on_sep else "open",
                                outer=is_outside(_add(mpt, inward, -PROBE_MM)))
                prev = pieces[-1] if pieces else None
                if prev and (prev.kind, prev.wall_key, prev.side) == (face.kind, face.wall_key, face.side) \
                        and abs(_cross(prev.direction, face.direction)) < 1e-6 and prev.end == face.start:
                    pieces[-1] = prev.model_copy(update={"end": face.end})
                else:
                    pieces.append(face)
        if len(pieces) > 1:
            first, last = pieces[0], pieces[-1]
            if (first.kind, first.wall_key, first.side) == (last.kind, last.wall_key, last.side) \
                    and abs(_cross(first.direction, last.direction)) < 1e-6 and last.end == first.start:
                pieces[0] = first.model_copy(update={"start": last.start})
                pieces.pop()
        return pieces

    @staticmethod
    def wall_end_at(walls: list[_Wall], p0: Point, p1: Point) -> _Wall | None:
        """The wall whose end (the short side of its rectangle) the edge p0 -> p1 lies on."""
        for w in walls:
            for end in (w.s, w.e):
                if all(abs(_dot(_sub(q, end), w.u)) <= FACE_TOL_MM and abs(_dot(_sub(q, end), w.n)) <= w.t / 2 + FACE_TOL_MM
                       for q in (p0, p1)):
                    return w
        return None

    def obstacles(self, level: str, shape: Polygon, mass) -> list[list[Point]]:
        z = self.levels[level]
        out = []
        for c in self.columns:
            base = self.levels[c.level] + c.base_offset_mm
            top = self.levels[c.top_level] + c.top_offset_mm
            if not (base <= z + BASE_TOL_MM and top > z + BASE_TOL_MM):
                continue
            m = re.search(r"(\d+(?:\.\d+)?)\s*[x×]\s*(\d+(?:\.\d+)?)", c.type or "", re.I)
            if not m:
                continue
            b, h = float(m.group(1)), float(m.group(2))
            a = math.radians(c.rotation_deg)
            ux, uy = math.cos(a), math.sin(a)
            corners = [(c.point[0] + sx * b / 2 * ux - sy * h / 2 * uy, c.point[1] + sx * b / 2 * uy + sy * h / 2 * ux)
                       for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
            part = Polygon(corners).intersection(shape)
            if part.area > 100:  # more than 1 cm² stands in the room
                geoms = part.geoms if hasattr(part, "geoms") else [part]
                for g in geoms:
                    if isinstance(g, Polygon) and g.area > 100:
                        out.append(_canonical(list(orient(g, 1.0).exterior.coords)[:-1]))
        return out

    # ---------------------------------------------------------------- openings

    def opening_views(self, op: DoorOp | WindowOp, w: _Wall, room_at, rooms: dict[str, Room]):
        width, height, sliding = self.opening_size(op)
        assumed = False
        if width is None:
            if op.op == "door":
                width = float(self.rules.furniture.layout.unknown_door_width_mm.value)
                assumed = True
                self.assume(f"fitout:door_width:{op.type}", f"Doors of type {op.type} are taken as {width:g} mm wide "
                            "(the plan does not give their width)", width,
                            self.rules.furniture.layout.unknown_door_width_mm.source, [op.key])
            self.ask(f"fitout:{op.op}_width:{op.type}", f"fitout_{op.op}_width",
                     f"How wide (mm) are the {op.op}s of type {op.type}? The plan does not say.", instances=[op.key])
            if width is None:
                return []
        c = w.at(op.offset_mm)
        half = w.t / 2
        sides = {"left": room_at(_add(c, w.n, half + PROBE_MM)), "right": room_at(_add(c, w.n, -(half + PROBE_MM)))}
        opens_left = op.flip_facing != DEFAULT_OPENS_LEFT
        hinge_at_end = op.flip_hand != DEFAULT_HINGE_AT_END
        leaf_side = "left" if opens_left else "right"
        out = []
        for side, room_key in sides.items():
            if room_key is None or room_key not in rooms:
                continue
            sgn = 1 if side == "left" else -1
            face_c = _add(c, w.n, sgn * half)
            jl, jr = _add(face_c, w.u, -width / 2), _add(face_c, w.u, width / 2)
            room = rooms[room_key]
            face = next((i for i, f in enumerate(room.faces) if f.wall_key == w.key and f.side == side
                         and _on_segment(face_c, f.start, f.end)), None)
            other = sides["right" if side == "left" else "left"]
            data: dict[str, Any] = dict(key=op.key, kind=op.op, type=op.type, wall_key=w.key, face=face,
                                        width_mm=width, height_mm=height, center=_r(face_c), along=_r(w.u),
                                        left=_r(jl), right=_r(jr), other_room=other if other != room_key else None,
                                        width_assumed=assumed)
            if op.op == "window":
                data["sill_mm"] = op.sill_mm
                data["head_mm"] = (op.sill_mm or 0) + height if height is not None and op.sill_mm is not None else None
            else:
                data["sliding"] = sliding
                if not sliding:
                    hinge, latch = (jr, jl) if hinge_at_end else (jl, jr)
                    data["hinge"], data["latch"] = _r(hinge), _r(latch)
                    if side == leaf_side:
                        towards = ((latch[0] - hinge[0]) / width, (latch[1] - hinge[1]) / width)
                        data["swings_in"] = True
                        data["swing"] = quarter_disc(hinge, towards, (sgn * w.n[0], sgn * w.n[1]), width)
            out.append((room_key, Opening(**data)))
        return out

    # ---------------------------------------------------------------- flats

    def assign_flats(self, rooms: list[Room]) -> list[Room]:
        by_key = {r.key: r for r in rooms}
        ops = {op.key: op for op in self.room_ops}
        cls = self.rules.rooms

        def not_in_flat(r: Room) -> bool:
            return any(not cls[c].in_flat or cls[c].flat_boundary for c in r.classes)

        def core(r: Room) -> bool:
            return any(cls[c].flat_boundary and not cls[c].in_flat for c in r.classes)

        def lobby(r: Room) -> bool:
            return any(cls[c].boundary_near_core for c in r.classes)

        level_flats: dict[str, list[str]] = defaultdict(list)
        for r in rooms:
            f = ops[r.key].flat
            if f and f not in level_flats[r.level]:
                level_flats[r.level].append(f)
        resolved: dict[str, tuple[str | None, str | None]] = {}
        unresolved: dict[str, set[str]] = {}
        # first every room the plan places, the rooms outside flats, then the rooms reached through doors and open
        # edges without passing a common room or a lobby (a lobby may be common: decided after, from what it opens into)
        for r in rooms:
            answer = self.answers.get(f"fitout:flat:{r.key}")
            if ops[r.key].flat:
                resolved[r.key] = (ops[r.key].flat, "plan")
            elif answer is not None:
                resolved[r.key] = (None, "common") if answer == "common" else (str(answer), "answer")
            elif not_in_flat(r):
                resolved[r.key] = (None, "common")
        for r in rooms:
            if r.key in resolved or lobby(r):
                continue
            found: set[str] = set()
            seen, todo = {r.key}, [r.key]
            while todo:
                for nk in by_key[todo.pop()].neighbours:
                    if nk in seen or nk not in by_key:
                        continue
                    seen.add(nk)
                    if ops[nk].flat:
                        found.add(ops[nk].flat)
                    elif not not_in_flat(by_key[nk]) and not lobby(by_key[nk]):
                        todo.append(nk)
            if len(found) == 1:
                resolved[r.key] = (found.pop(), "connected")
            else:
                unresolved[r.key] = found
        for r in rooms:
            if r.key in resolved or not lobby(r):
                continue
            near = [by_key[n] for n in r.neighbours if n in by_key]
            flats = {resolved[n.key][0] for n in near if n.key in resolved and resolved[n.key][0]}
            if any(core(n) for n in near) or len(flats) > 1:
                resolved[r.key] = (None, "common")
            elif len(flats) == 1:
                resolved[r.key] = (flats.pop(), "connected")
            else:
                unresolved[r.key] = set()
        for key, found in unresolved.items():
            r = by_key[key]
            resolved[key] = (None, None)
            if level_flats.get(r.level):
                self.ask(f"fitout:flat:{key}", "fitout_flat",
                         f"Which flat is \"{r.name}\" on {r.level} part of? The plan gives it none and its doors reach "
                         f"{'several flats' if found else 'no room of a flat'} (answer a flat, or common).",
                         options=[*sorted(level_flats[r.level]), "common"], instances=[key])
        return [r.model_copy(update={"flat": resolved[r.key][0], "flat_source": resolved[r.key][1]}) for r in rooms]


def _canonical(ring: list[Point]) -> list[Point]:
    """The same ring started at its lowest-then-leftmost corner, so equal rooms list equal rings."""
    ring = [_r(p) for p in ring]
    i = min(range(len(ring)), key=lambda k: (ring[k][1], ring[k][0]))
    return ring[i:] + ring[:i]


def _on_segment(p: Point, a: Point, b: Point, tol: float = FACE_TOL_MM) -> bool:
    L = math.dist(a, b)
    if L == 0:
        return False
    v = ((b[0] - a[0]) / L, (b[1] - a[1]) / L)
    t = _dot(_sub(p, a), v)
    return -tol <= t <= L + tol and abs(_cross(v, _sub(p, a))) <= tol
