"""Self-check of an imported build (PRD Step 9, stage 8 / T9): the Revit model read back and compared, element by element,
with the build plan and with the recognised building (the drawings).

    from b2m.imported import selfcheck
    report = selfcheck.compare(plan_result, building, created, readback, answers=answers)
    report.ok                     # True when no difference is left unexplained ("done" means zero)
    selfcheck.write(report, run_directory)    # selfcheck.json + selfcheck.html

    service.start(plan_result.plan, title, after_build=selfcheck.after_build_selfcheck(plan_result, building, answers))

``plan_result`` is the ``ImportPlan`` the build ran (its ``sources`` tie every op key to its sheet, element and storey);
``created`` is the run store's ``{op key (or "key#n"): element id}``; ``readback`` is what the add-in's
``read_back_elements`` returned, by element id (``read_back`` makes the calls).

Three stages are compared, each difference saying which:
- ``plan_to_model``: every planned op is in the model (missing -> difference), every created element belongs to an op
  (extra -> difference), and every value the op sets is what Revit holds.
- ``drawing_to_plan``: every value the drawing gives is what the plan carries (registration applied: rotation, then
  translation). A plan value that departs from the drawing because the user answered a question is a difference
  *explained* by that answer.
- ``drawing_to_model``: values only the drawing and the model have (room areas against their drawn boundary and their
  written size).
- ``accounting``: every recognised element is planned, or listed in the plan's ``not_modelled``, or accounted for by
  a documented rule (a grid on the line of a built grid, a room merged into another by an answer, a section cut line,
  a typical column section, a section level at a built level's elevation...); an element nothing accounts for is a
  difference.

Comparison rules per kind (tolerances in ``Tolerances``, all configurable; areas pass within the larger of the absolute
and the relative tolerance):
- levels: name; elevation (level tolerance) against the plan and the section that gave it.
- grids: name; end points against the plan; the drawn grid lies on the planned line (merged grids are longer).
- walls: centreline end points (positions), thickness (the wall type's thickness against Revit's width), base level and
  offset, top level and offset or unconnected height, height (sizes).
- columns: centre (position), section b x h from the type (sizes), rotation (angles, modulo 180 deg; 90 for square
  sections), base and top levels and offsets.
- beams: end points, section, reference level.
- slabs (floors, projections, foundation slabs): outline corners (positions, matched one to one) and outline area
  (areas) from Revit's sketch, level, height offset, thickness of the type.
- doors and windows: host wall, centre along the host wall from its start, width and height of the type, sill
  (windows), Type Mark, facing and hand flips, level.
- rooms: name, number, department, level, placement point; area against the drawn boundary and against the written
  size ("3500 X 4000"); a room merged from several labels, or split off a shared space by a separation line, has
  no drawn boundary and must just be enclosed. A label merged into a room is accounted for by the answer.
- stairs: levels, risers, riser height (storey height / risers), tread, width of every run.
- railings: hosted on their stair. Shafts: outline, levels, offsets. Room separation lines: every segment.
Type names are not compared: the add-in may reuse an existing type of the same build-up under another name; what
defines the type (thickness, section, size, mark) is compared on every element that uses it.
"""

from __future__ import annotations

import fnmatch
import html
import json
import math
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any, Callable, Iterable, Literal, Mapping

from pydantic import BaseModel, ConfigDict

from b2m.cad.building import Evidence, RecognisedBuilding, Registration
from b2m.imported import split_space
from b2m.imported.to_plan import ImportPlan
from b2m.plan import KEY_SEPARATOR, TYPE_OPS, BuildPlan
from b2m.revit.commands import CATEGORY, RAILINGS, op_key

JSON_FILE, HTML_FILE = "selfcheck.json", "selfcheck.html"
READ_BACK_COMMAND = "read_back_elements"
READ_BACK_MAX_IDS = 5000
M2_PER_MM2 = 1e-6

Point = tuple[float, float]
Stage = Literal["plan_to_model", "drawing_to_plan", "drawing_to_model", "accounting"]

KIND = {"level": "level", "grid": "grid", "wall": "wall", "column": "column", "beam": "beam", "floor": "slab",
        "foundation_slab": "slab", "door": "opening", "window": "opening", "room": "room", "stair": "stair",
        "railing": "railing", "shaft": "shaft", "room_separator": "room_separator"}
# Which recognised elements an op of each kind is planned from (its source element; "<id>.wall" is the wall across a
# door's gap, "<id>.well" a stair's slab opening, "<id>.railing" its railing).
RECOGNISED_KINDS = {"level": ("levels",), "grid": ("grids",), "wall": ("walls", "openings"),
                    "column": ("columns", "plan_columns"), "beam": ("beams",), "floor": ("outline", "projections"),
                    "foundation_slab": ("footings",), "door": ("openings",), "window": ("openings",), "room": ("rooms",),
                    "stair": ("stairs",), "railing": ("stairs",), "shaft": ("shafts", "stairs"),
                    "room_separator": ("rooms",)}
# The level question each kind of section level belongs to (as b2m.imported.to_plan asks it: "level:<id>").
LEVEL_QUESTION = {"roof": "roof", "foundation": "foundation", "parapet_top": "parapet_top", "ground": "ground",
                  "plinth": "plinth", "other": "roof_structure"}
LEVEL_NAME_QUESTION = {"Roof": "roof", "Foundation": "foundation", "Parapet Top": "parapet_top", "GL": "ground",
                       "Plinth": "plinth", "Upper Roof": "roof_structure"}


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Tolerances(_Model):
    position_mm: float = 1.0
    size_mm: float = 1.0
    area_m2: float = 0.01
    area_pct: float = 0.5  # an area passes within max(area_m2, area_pct % of the expected area)
    level_mm: float = 1.0
    angle_deg: float = 0.01

    def area(self, expected: float) -> float:
        return max(self.area_m2, abs(expected) * self.area_pct / 100)


class Explanation(_Model):
    """A known, documented reason for differences. Every matcher given must match (``op_key`` and ``element`` take
    shell patterns: "*-wall-12-*"); ``element`` is "<sheet>#<element id>"."""

    reason: str
    kind: str | None = None
    field: str | None = None
    stage: str | None = None
    op_key: str | None = None
    element: str | None = None

    def matches(self, d: "Difference") -> bool:
        ref = f"{d.sheet}#{d.element}" if d.sheet and d.element else None
        return ((self.kind is None or self.kind == d.kind) and (self.field is None or self.field == d.field)
                and (self.stage is None or self.stage == d.stage)
                and (self.op_key is None or (d.op_key is not None and fnmatch.fnmatchcase(d.op_key, self.op_key)))
                and (self.element is None or (ref is not None and fnmatch.fnmatchcase(ref, self.element))))


class Difference(_Model):
    kind: str
    stage: Stage
    field: str
    op_key: str | None = None
    element_id: int | None = None  # Revit element id
    sheet: str | None = None  # evidence: the sheet and the recognised element the op came from
    element: str | None = None
    storey: str | None = None
    handles: list[str] = []
    expected: Any = None
    actual: Any = None
    delta: float | None = None
    tolerance: float | None = None
    unit: str | None = None
    severity: Literal["error", "warning"] = "error"
    explained: bool = False
    reason: str | None = None


class KindSummary(_Model):
    ops: int = 0  # planned ops (or recognised elements, for accounting) compared
    fields: int = 0  # values compared
    unexplained: int = 0
    explained: int = 0


class Report(_Model):
    ok: bool
    unexplained: int
    explained: int
    tolerances: Tolerances
    kinds: dict[str, KindSummary] = {}
    accounting: dict[str, int] = {}
    elements_read: int = 0
    differences: list[Difference] = []
    not_modelled: list[dict[str, Any]] = []
    review_url: str | None = None

    def summary(self) -> dict[str, Any]:
        return {"ok": self.ok, "unexplained": self.unexplained, "explained": self.explained,
                "kinds": {k: v.model_dump() for k, v in self.kinds.items()}, "accounting": self.accounting,
                "elements_read": self.elements_read}


# ------------------------------------------------------------------------------------------------ geometry

def transform(reg: Registration | None) -> Callable[[Point], Point]:
    """Sheet coordinates -> building frame (rotation counter-clockwise, then translation), as the plan uses them."""
    if reg is None:
        return lambda p: (float(p[0]), float(p[1]))
    r = math.radians(reg.rotation_deg)
    c, s = math.cos(r), math.sin(r)
    tx, ty = reg.translation
    return lambda p: (c * p[0] - s * p[1] + tx, s * p[0] + c * p[1] + ty)


def polygon_area_m2(points: list[Point]) -> float:
    pts = _open(points)
    twice = sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1] for i in range(len(pts)))
    return abs(twice) / 2 * M2_PER_MM2


def _open(points: list[Point]) -> list[Point]:
    pts = [(float(p[0]), float(p[1])) for p in points]
    return pts[:-1] if len(pts) > 1 and _dist(pts[0], pts[-1]) < 1e-9 else pts


def _dist(a: Point, b: Point) -> float:
    return math.hypot(a[0] - b[0], a[1] - b[1])


def _corner_mismatch(expected: list[Point], actual: list[Point]) -> float:
    """Worst distance when every expected corner is matched to its own nearest actual corner; inf when the counts
    differ."""
    e, a = _open(expected), _open(actual)
    if len(e) != len(a):
        return math.inf
    free = list(a)
    worst = 0.0
    for p in e:
        i = min(range(len(free)), key=lambda j: _dist(p, free[j]))
        worst = max(worst, _dist(p, free[i]))
        free.pop(i)
    return worst


def _line_offset(p: Point, a: Point, b: Point) -> tuple[float, float, float]:
    """(distance of p from the infinite line a-b, position along it from a, the line's length)."""
    length = _dist(a, b)
    if length == 0:
        return _dist(p, a), 0.0, 0.0
    ux, uy = (b[0] - a[0]) / length, (b[1] - a[1]) / length
    dx, dy = p[0] - a[0], p[1] - a[1]
    return abs(dx * uy - dy * ux), dx * ux + dy * uy, length


def _angle_delta(a: float, b: float, period: float) -> float:
    d = (a - b) % period
    return min(d, period - d)


def _xy(value: Any) -> Point | None:
    if isinstance(value, Mapping) and value.get("x") is not None and value.get("y") is not None:
        return float(value["x"]), float(value["y"])
    if isinstance(value, (list, tuple)) and len(value) >= 2:
        return float(value[0]), float(value[1])
    return None


def _num(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else None


# ------------------------------------------------------------------------------------------------ recognised index

SEPARATOR_JOIN_MM = 300.0  # how far Revit may lengthen a separation line's end into a wall


class _Recognised:
    """Every recognised element by (sheet, id), with its kind, the sheet's registration and its evidence."""

    def __init__(self, b: RecognisedBuilding):
        self.b = b
        self.regs = {r.sheet: r for r in b.registrations if r.frame == "plan"}
        self.items: dict[tuple[str, str], list[tuple[str, Any]]] = defaultdict(list)
        self.plans = {p.sheet: p for p in b.plans}
        for p in b.plans:
            for kind in ("grids", "walls", "columns", "openings", "rooms", "stairs", "shafts", "projections", "falls"):
                for e in getattr(p, kind):
                    self.items[(p.sheet, e.id)].append((kind, e))
            for m in p.section_marks:
                self.items[(p.sheet, m.name)].append(("section_marks", m))
            if p.outline:
                self.items[(p.sheet, "outline")].append(("outline", p))
        for f in b.foundations:
            for kind in ("footings", "plan_columns", "column_sections"):
                for e in getattr(f, kind):
                    self.items[(f.sheet, e.id)].append((kind, e))
        for bl in b.beam_layouts:
            for kind in ("beams", "grids"):
                for e in getattr(bl, kind):
                    self.items[(bl.sheet, e.id)].append((kind, e))
        for s in b.sections:
            for lv in s.levels:
                self.items[(s.sheet, lv.name)].append(("levels", lv))

    def get(self, sheet: str | None, element: str | None, kinds: Iterable[str]) -> Any:
        if not sheet or not element:
            return None
        kinds = tuple(kinds)
        for candidate in (element, element.split(".", 1)[0]):
            for kind, e in self.items.get((sheet, candidate), []):
                if kind in kinds:
                    return e
        return None

    def xf(self, sheet: str) -> Callable[[Point], Point]:
        return transform(self.regs.get(sheet))

    def walled_islands_m2(self, sheet: str, boundary: list[Point]) -> float:
        """The footprint (walls and what they enclose) of walls standing wholly inside a room's drawn boundary and
        joined into blocks, e.g. a duct on a roof terrace. A drawn boundary goes round them; Revit's room does not
        include them. Single free-standing walls count only their own body."""
        from shapely.geometry import LineString, Polygon
        from shapely.ops import unary_union

        p = self.plans.get(sheet)
        if p is None or len(boundary) < 3:
            return 0.0
        room = Polygon(boundary)
        if not room.is_valid:
            return 0.0
        bodies = []
        for w in p.walls:
            line = LineString([w.start, w.end])
            if line.length and room.contains(line):
                bodies.append(line.buffer(w.thickness_mm / 2, cap_style="flat", join_style="mitre"))
        if not bodies:
            return 0.0
        mass = unary_union(bodies).buffer(1, join_style="mitre").buffer(-1, join_style="mitre")
        blocks = mass.geoms if hasattr(mass, "geoms") else [mass]
        filled = unary_union([Polygon(b.exterior) for b in blocks if b.geom_type == "Polygon"])
        return filled.intersection(room).area / 1e6

    def rotation(self, sheet: str) -> float:
        reg = self.regs.get(sheet)
        return reg.rotation_deg if reg else 0.0

    def groups(self, sheet: str) -> dict[str, list[str]]:
        """Rooms that share one space (finding id -> room ids), as the plan conversion groups them."""
        p = self.plans.get(sheet)
        if p is None:
            return {}
        handles = {r.id: set(r.evidence.handles) for r in p.rooms}
        out = {}
        for f in p.findings:
            if f.topic == "rooms_share_space":
                members = [r.id for r in p.rooms if handles[r.id] & set(f.evidence.handles)]
                if members:
                    out[f.id] = members
        return out


# ------------------------------------------------------------------------------------------------ comparison

class _Comparer:
    def __init__(self, result: ImportPlan, building: RecognisedBuilding, created: Mapping[str, int],
                 readback: Mapping[int, Mapping[str, Any]], answers: Mapping[str, Any], tol: Tolerances,
                 explanations: list[Explanation]):
        if result.plan is None:
            raise ValueError("the import plan has no build plan (inputs are missing): nothing was built to check")
        self.result, self.plan = result, result.plan
        self.by_key = self.plan.by_key()
        self.rec = _Recognised(building)
        self.created = dict(created)
        self.readback = readback
        self.answers = answers
        self.tol = tol
        self.explanations = explanations
        self.diffs: list[Difference] = []
        self.kinds: dict[str, KindSummary] = defaultdict(KindSummary)
        self.elevation = {op.name: op.elevation_mm for op in self.plan.ops if op.op == "level"}
        self.wall_types = {op.name: op.thickness_mm for op in self.plan.ops if op.op == "wall_type"}
        self.floor_types = {op.name: op.thickness_mm for op in self.plan.ops if op.op == "floor_type"}
        self.opening_types = {(op.category, op.name): op for op in self.plan.ops if op.op == "opening_type"}
        std = result.standards
        self.frames = {f.name: (f.b_mm, f.h_mm) for f in std.columns + std.beams}
        self.floor_use = {f.name: f.use for f in std.floors}
        self.not_modelled_refs = {e: n for n in result.not_modelled for e in n.elements}

    # -------------------------------------------------------------------------------------------- recording

    def _context(self, key: str | None) -> dict[str, Any]:
        source = self.result.sources.get(key) if key else None
        if source is None:
            return {}
        handles: list[str] = []
        for _, e in self.rec.items.get((source.sheet or "", (source.element or "").split(".", 1)[0]), []):
            ev = getattr(e, "evidence", None)
            if isinstance(ev, Evidence):
                handles = list(ev.handles)
                break
        return {"sheet": source.sheet, "element": source.element, "storey": source.storey, "handles": handles}

    def add(self, kind: str, stage: Stage, field: str, expected: Any, actual: Any, *, key: str | None = None,
            element_id: int | None = None, delta: float | None = None, tolerance: float | None = None,
            unit: str | None = None, severity: str = "error", answer_keys: Iterable[str] = (),
            reason: str | None = None, context: dict[str, Any] | None = None) -> None:
        d = Difference(kind=kind, stage=stage, field=field, op_key=key, element_id=element_id,
                       expected=_jsonable(expected), actual=_jsonable(actual),
                       delta=None if delta is None or not math.isfinite(delta) else round(delta, 4),
                       tolerance=tolerance, unit=unit, severity=severity,
                       **(context if context is not None else self._context(key)))
        if reason:
            d.explained, d.reason = True, reason
        for answer in answer_keys:
            if answer in self.answers and self.answers[answer] is not None:
                d.explained, d.reason = True, f"answered question {answer} = {self.answers[answer]!r}"
                break
        if not d.explained:
            for e in self.explanations:
                if e.matches(d):
                    d.explained, d.reason = True, e.reason
                    break
        self.diffs.append(d)

    def count(self, kind: str) -> None:
        self.kinds[kind].fields += 1

    def number(self, kind: str, stage: Stage, field: str, expected: Any, actual: Any, tolerance: float, unit: str,
               **kw) -> None:
        if expected is None:
            return
        self.count(kind)
        a = _num(actual)
        if a is None:
            self.add(kind, stage, field, expected, actual, tolerance=tolerance, unit=unit, **kw)
            return
        delta = a - float(expected)
        if abs(delta) > tolerance + 1e-9:
            self.add(kind, stage, field, expected, actual, delta=delta, tolerance=tolerance, unit=unit, **kw)

    def point(self, kind: str, stage: Stage, field: str, expected: Point | None, actual: Point | None, **kw) -> None:
        if expected is None:
            return
        self.count(kind)
        if actual is None:
            self.add(kind, stage, field, expected, None, tolerance=self.tol.position_mm, unit="mm", **kw)
            return
        delta = _dist(expected, actual)
        if delta > self.tol.position_mm + 1e-9:
            self.add(kind, stage, field, expected, actual, delta=delta, tolerance=self.tol.position_mm, unit="mm", **kw)

    def same(self, kind: str, stage: Stage, field: str, expected: Any, actual: Any, **kw) -> None:
        if expected is None:
            return
        self.count(kind)
        if expected != actual:
            self.add(kind, stage, field, expected, actual, **kw)

    def area(self, kind: str, stage: Stage, field: str, expected: float | None, actual: Any, **kw) -> None:
        if expected is None:
            return
        self.count(kind)
        tolerance = round(self.tol.area(expected), 6)
        a = _num(actual)
        delta = None if a is None else a - expected
        if delta is None or abs(delta) > tolerance + 1e-12:
            self.add(kind, stage, field, round(expected, 4), actual, delta=delta, tolerance=tolerance, unit="m2", **kw)

    def corners(self, kind: str, stage: Stage, field: str, expected: list[Point] | None, actual: list[Point] | None,
                **kw) -> None:
        if not expected:
            return
        self.count(kind)
        delta = _corner_mismatch(expected, actual) if actual else math.inf
        if delta > self.tol.position_mm + 1e-9:
            self.add(kind, stage, field, [list(p) for p in _open(expected)],
                     [list(p) for p in _open(actual)] if actual else None, delta=delta,
                     tolerance=self.tol.position_mm, unit="mm", **kw)

    # -------------------------------------------------------------------------------------------- run

    def run(self) -> None:
        self.presence()
        for op in self.plan.ops:
            if op.op in TYPE_OPS:
                continue
            kind = KIND[op.op]
            self.kinds[kind].ops += 1
            getattr(self, f"op_{op.op}")(op)
        self.accounting()

    def record(self, key: str) -> tuple[int | None, Mapping[str, Any] | None]:
        element_id = self.created.get(key)
        if element_id is None:
            return None, None
        rec = self.readback.get(int(element_id))
        return int(element_id), (rec if rec and rec.get("exists", True) else None)

    def presence(self) -> None:
        planned = {op.key: op for op in self.plan.ops}
        for op in self.plan.ops:
            if op.op in TYPE_OPS:
                continue
            kind = KIND[op.op]
            key = op.key
            if op.op == "railing" and op.stair_key and key not in self.created \
                    and f"{op.stair_key}{KEY_SEPARATOR}2" in self.created:
                continue  # the stair came with its own railings; they stand for this op (see Runner._direct)
            self.count(kind)
            element_id = self.created.get(key)
            if element_id is None:
                self.add(kind, "plan_to_model", "presence", "built", "not built (no element id recorded)", key=key)
                continue
            rec = self.readback.get(int(element_id))
            if rec is None or rec.get("exists") is False:
                self.add(kind, "plan_to_model", "presence", f"element {element_id}",
                         "not in the model" if rec is not None else "not read back", key=key, element_id=int(element_id))
                continue
            self.same(kind, "plan_to_model", "category", CATEGORY[op.op], rec.get("category"), key=key,
                      element_id=int(element_id))
        for created_key, element_id in self.created.items():
            base = op_key(created_key)
            op = planned.get(base)
            if op is None:
                self.add("extra", "plan_to_model", "presence", "no element", f"element {element_id} ({created_key})",
                         key=created_key, element_id=int(element_id), context={})
                continue
            if op.op in TYPE_OPS or created_key == base:
                continue
            # Further elements of one op: a stair's railings, a room separator's segments.
            rec = self.readback.get(int(element_id))
            category = rec.get("category") if rec else None
            if op.op == "stair":
                self.same("railing", "plan_to_model", "category", RAILINGS, category, key=created_key,
                          element_id=int(element_id))
            elif op.op == "room_separator":
                continue
            else:
                self.add(KIND[op.op], "plan_to_model", "presence", "one element", f"also element {element_id}",
                         key=created_key, element_id=int(element_id))
        ids = {int(v) for v in self.created.values()}
        for element_id, rec in self.readback.items():
            if int(element_id) not in ids:
                self.add("extra", "plan_to_model", "presence", "no element",
                         f"element {element_id} ({rec.get('category')}) read back but made by no op",
                         element_id=int(element_id), context={})

    # -------------------------------------------------------------------------------------------- per op

    def op_level(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        if rec is not None:
            data = rec.get("level_element") or {}
            self.same("level", "plan_to_model", "name", op.name, data.get("name"), **kw)
            self.number("level", "plan_to_model", "elevation", op.elevation_mm, data.get("elevation_mm"),
                        self.tol.level_mm, "mm", **kw)
        source = self.result.sources.get(op.key)
        lv = self.rec.get(source.sheet, source.element, ["levels"]) if source else None
        if lv is not None:
            question = (f"level:storey-{source.storey}" if op.name not in LEVEL_NAME_QUESTION and source.storey
                        else f"level:{LEVEL_NAME_QUESTION.get(op.name, '')}")
            self.number("level", "drawing_to_plan", "elevation", lv.elevation_mm, op.elevation_mm, self.tol.level_mm,
                        "mm", key=op.key, answer_keys=[question])

    def op_grid(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        if rec is not None:
            data = rec.get("grid") or {}
            self.same("grid", "plan_to_model", "name", op.name, data.get("name"), **kw)
            self.point("grid", "plan_to_model", "start", op.start, _xy(data.get("start")), **kw)
            self.point("grid", "plan_to_model", "end", op.end, _xy(data.get("end")), **kw)
        source = self.result.sources.get(op.key)
        g = self.rec.get(source.sheet, source.element, ["grids"]) if source else None
        if g is not None:
            t = self.rec.xf(source.sheet)
            for field, p in (("drawn start on the grid line", t(g.start)), ("drawn end on the grid line", t(g.end))):
                off, along, length = _line_offset(p, op.start, op.end)
                self.count("grid")
                outside = max(0.0, -along, along - length)
                if max(off, outside) > self.tol.position_mm + 1e-9:
                    self.add("grid", "drawing_to_plan", field, [round(v, 2) for v in p], [list(op.start), list(op.end)],
                             delta=max(off, outside), tolerance=self.tol.position_mm, unit="mm", key=op.key)
            if g.name:
                self.same("grid", "drawing_to_plan", "name", g.name, op.name, key=op.key)

    def op_wall(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        thickness = self.wall_types.get(op.type) if op.type else None
        base_e = self.elevation.get(op.level)
        if op.top_level:
            top_e = self.elevation.get(op.top_level)
            height = None if top_e is None or base_e is None else top_e + op.top_offset_mm - base_e - op.base_offset_mm
        else:
            height = op.height_mm
        if rec is not None:
            loc, data = rec.get("location") or {}, rec.get("wall") or {}
            self.point("wall", "plan_to_model", "start", op.start, _xy(loc.get("start")), **kw)
            self.point("wall", "plan_to_model", "end", op.end, _xy(loc.get("end")), **kw)
            self.number("wall", "plan_to_model", "thickness", thickness, data.get("width_mm"), self.tol.size_mm, "mm", **kw)
            self.same("wall", "plan_to_model", "base level", op.level, data.get("base_level"), **kw)
            self.number("wall", "plan_to_model", "base offset", op.base_offset_mm, data.get("base_offset_mm"),
                        self.tol.level_mm, "mm", **kw)
            self.count("wall")
            if op.top_level != data.get("top_level"):
                self.add("wall", "plan_to_model", "top level", op.top_level or "unconnected",
                         data.get("top_level") or "unconnected", **kw)
            if op.top_level:
                self.number("wall", "plan_to_model", "top offset", op.top_offset_mm, data.get("top_offset_mm"),
                            self.tol.level_mm, "mm", **kw)
            self.number("wall", "plan_to_model", "height", height, data.get("height_mm"), self.tol.size_mm, "mm", **kw)
        source = self.result.sources.get(op.key)
        w = self.rec.get(source.sheet, source.element, ["walls"]) if source and "." not in (source.element or "") else None
        if w is not None:
            t = self.rec.xf(source.sheet)
            self.point("wall", "drawing_to_plan", "start", t(w.start), op.start, key=op.key)
            self.point("wall", "drawing_to_plan", "end", t(w.end), op.end, key=op.key)
            self.number("wall", "drawing_to_plan", "thickness", w.thickness_mm, thickness, self.tol.size_mm, "mm",
                        key=op.key)
            if w.height_mm is not None:
                self.number("wall", "drawing_to_plan", "height", w.height_mm, height, self.tol.size_mm, "mm", key=op.key,
                            answer_keys=["boundary_wall_height", "parapet_height"])

    def _frame_section(self, name: str) -> tuple[float, float] | None:
        return self.frames.get(name)

    def op_column(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        section = self._frame_section(op.type)
        period = 90.0 if section and abs(section[0] - section[1]) < 1e-9 else 180.0
        if rec is not None:
            loc, data = rec.get("location") or {}, rec.get("frame") or {}
            self.point("column", "plan_to_model", "centre", op.point, _xy(loc.get("point")), **kw)
            if section:
                self.number("column", "plan_to_model", "section b", section[0], data.get("b_mm"), self.tol.size_mm, "mm", **kw)
                self.number("column", "plan_to_model", "section h", section[1], data.get("h_mm"), self.tol.size_mm, "mm", **kw)
            self._rotation("plan_to_model", op.rotation_deg, loc.get("rotation_deg"), period, **kw)
            self.same("column", "plan_to_model", "base level", op.level, data.get("base_level"), **kw)
            self.same("column", "plan_to_model", "top level", op.top_level, data.get("top_level"), **kw)
            self.number("column", "plan_to_model", "base offset", op.base_offset_mm, data.get("base_offset_mm"),
                        self.tol.level_mm, "mm", **kw)
            self.number("column", "plan_to_model", "top offset", op.top_offset_mm, data.get("top_offset_mm"),
                        self.tol.level_mm, "mm", **kw)
        source = self.result.sources.get(op.key)
        c = self.rec.get(source.sheet, source.element, ["columns", "plan_columns"]) if source else None
        if c is not None:
            self.point("column", "drawing_to_plan", "centre", self.rec.xf(source.sheet)(c.center), op.point, key=op.key)
            if section:
                b, h = sorted((c.width_mm, c.depth_mm))
                self.number("column", "drawing_to_plan", "section b", b, section[0], self.tol.size_mm, "mm", key=op.key)
                self.number("column", "drawing_to_plan", "section h", h, section[1], self.tol.size_mm, "mm", key=op.key)
            drawn = c.rotation_deg + self.rec.rotation(source.sheet) + (90 if c.width_mm > c.depth_mm else 0)
            self._rotation("drawing_to_plan", drawn, op.rotation_deg, period, key=op.key)

    def _rotation(self, stage: Stage, expected: float, actual: Any, period: float, **kw) -> None:
        self.count("column")
        a = _num(actual)
        delta = None if a is None else _angle_delta(a, expected, period)
        if delta is None or delta > self.tol.angle_deg + 1e-9:
            self.add("column", stage, f"rotation (mod {period:g} deg)", round(expected % period, 6), actual, delta=delta,
                     tolerance=self.tol.angle_deg, unit="deg", **kw)

    def op_beam(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        section = self._frame_section(op.type)
        if rec is not None:
            loc, data = rec.get("location") or {}, rec.get("frame") or {}
            self.point("beam", "plan_to_model", "start", op.start, _xy(loc.get("start")), **kw)
            self.point("beam", "plan_to_model", "end", op.end, _xy(loc.get("end")), **kw)
            if section:
                self.number("beam", "plan_to_model", "section b", section[0], data.get("b_mm"), self.tol.size_mm, "mm", **kw)
                self.number("beam", "plan_to_model", "section h", section[1], data.get("h_mm"), self.tol.size_mm, "mm", **kw)
            self.same("beam", "plan_to_model", "reference level", op.level, data.get("reference_level") or rec.get("level"),
                      **kw)
        source = self.result.sources.get(op.key)
        bm = self.rec.get(source.sheet, source.element, ["beams"]) if source else None
        if bm is not None:
            t = self.rec.xf(source.sheet)
            self.point("beam", "drawing_to_plan", "start", t(bm.start), op.start, key=op.key)
            self.point("beam", "drawing_to_plan", "end", t(bm.end), op.end, key=op.key)
            if section:
                self.number("beam", "drawing_to_plan", "width", bm.width_mm, section[0], self.tol.size_mm, "mm",
                            key=op.key, answer_keys=[f"beam_width:{source.sheet}"])
                self.number("beam", "drawing_to_plan", "depth", bm.depth_mm, section[1], self.tol.size_mm, "mm",
                            key=op.key, answer_keys=[f"beam_depth:{source.sheet}"])

    def op_floor(self, op) -> None:
        self._slab(op)

    def op_foundation_slab(self, op) -> None:
        self._slab(op)

    def _slab(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        thickness = self.floor_types.get(op.type) if op.type else None
        points = [tuple(p) for p in op.points]
        if rec is not None:
            data = rec.get("floor") or {}
            loops = [[_xy(q) for q in loop] for loop in data.get("outline") or []]
            loops = [[q for q in loop if q is not None] for loop in loops]
            outer = max(loops, key=polygon_area_m2) if loops else None
            self.corners("slab", "plan_to_model", "outline corners", points, outer, **kw)
            sketch_area = (polygon_area_m2(outer) - sum(polygon_area_m2(lp) for lp in loops if lp is not outer)) if outer else None
            self.area("slab", "plan_to_model", "outline area", polygon_area_m2(points), sketch_area, **kw)
            self.same("slab", "plan_to_model", "level", op.level, rec.get("level"), **kw)
            self.number("slab", "plan_to_model", "height offset", op.offset_mm, data.get("height_offset_mm"),
                        self.tol.level_mm, "mm", **kw)
            self.number("slab", "plan_to_model", "thickness", thickness, data.get("thickness_mm"), self.tol.size_mm, "mm",
                        **kw)
        source = self.result.sources.get(op.key)
        if source is None or not source.sheet:
            return
        t = self.rec.xf(source.sheet)
        if source.element == "outline":
            plan = self.rec.plans.get(source.sheet)
            if plan is not None and plan.outline:
                self.corners("slab", "drawing_to_plan", "outline corners", [t(q) for q in plan.outline], points, key=op.key)
        else:
            e = self.rec.get(source.sheet, source.element, ["projections", "footings"])
            if e is not None and hasattr(e, "outline"):
                self.corners("slab", "drawing_to_plan", "outline corners", [t(q) for q in e.outline], points, key=op.key)
            elif e is not None:  # a footing: its rectangle
                hw, hl = e.width_mm / 2, e.length_mm / 2
                cx, cy = e.center
                rect = [t(q) for q in ((cx - hw, cy - hl), (cx + hw, cy - hl), (cx + hw, cy + hl), (cx - hw, cy + hl))]
                self.corners("slab", "drawing_to_plan", "outline corners", rect, points, key=op.key)
                self.number("slab", "drawing_to_plan", "thickness", e.depth_mm, thickness, self.tol.size_mm, "mm",
                            key=op.key, answer_keys=[f"footing_depth:{source.sheet}:{e.id}"])
        use = self.floor_use.get(op.type or "")
        drawn = self._drawn_floor_thickness(use)
        if drawn is not None:
            self.number("slab", "drawing_to_plan", "thickness", drawn, thickness, self.tol.size_mm, "mm", key=op.key,
                        answer_keys=["slab_thickness"])

    def _drawn_floor_thickness(self, use: str | None) -> float | None:
        """The thickness the sections give a floor of this use: the slab thickness (when every section agrees) for
        slabs, the build-up's layers for the ground floor."""
        sections = self.rec.b.sections
        if use == "slab":
            values = {s.slab_thickness_mm for s in sections if s.slab_thickness_mm is not None}
            return values.pop() if len(values) == 1 else None
        if use == "ground":
            layers = next((s.foundation_layers for s in sections if s.foundation_layers), [])
            return sum(th for _, th in layers) if layers else None
        return None

    def op_door(self, op) -> None:
        self._opening(op, "door")

    def op_window(self, op) -> None:
        self._opening(op, "window")

    def _opening(self, op, category: str) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        type_op = self.opening_types.get((category, op.type))
        host = self.by_key[op.host_key]
        if rec is not None:
            data = rec.get("opening") or {}
            self.same("opening", "plan_to_model", "host wall", self.created.get(op.host_key), data.get("host_id"), **kw)
            self.number("opening", "plan_to_model", "centre along wall", op.offset_mm, data.get("host_offset_mm"),
                        self.tol.position_mm, "mm", **kw)
            if type_op is not None:
                self.number("opening", "plan_to_model", "width", type_op.width_mm, data.get("width_mm"), self.tol.size_mm,
                            "mm", **kw)
                self.number("opening", "plan_to_model", "height", type_op.height_mm, data.get("height_mm"),
                            self.tol.size_mm, "mm", **kw)
                if type_op.type_mark:
                    self.same("opening", "plan_to_model", "mark", type_op.type_mark, rec.get("type_mark"), **kw)
            if category == "window":
                self.number("opening", "plan_to_model", "sill", op.sill_mm, data.get("sill_mm"), self.tol.size_mm, "mm",
                            **kw)
            self.same("opening", "plan_to_model", "facing flipped", op.flip_facing, data.get("facing_flipped"), **kw)
            self.same("opening", "plan_to_model", "hand flipped", op.flip_hand, data.get("hand_flipped"), **kw)
            self.same("opening", "plan_to_model", "level", host.level, rec.get("level"), **kw)
        source = self.result.sources.get(op.key)
        o = self.rec.get(source.sheet, source.element, ["openings"]) if source else None
        if o is None:
            return
        t = self.rec.xf(source.sheet)
        _, along, _ = _line_offset(t(o.center), host.start, host.end)
        self.number("opening", "drawing_to_plan", "centre along wall", along, op.offset_mm, self.tol.position_mm, "mm",
                    key=op.key)
        if type_op is not None:
            self.number("opening", "drawing_to_plan", "width", o.width_mm, type_op.width_mm, self.tol.size_mm, "mm",
                        key=op.key, answer_keys=[f"{category}_width:{op.type}"])
            self.number("opening", "drawing_to_plan", "height", o.height_mm, type_op.height_mm, self.tol.size_mm, "mm",
                        key=op.key, answer_keys=[f"{category}_height:{op.type}"])
            if o.mark_normalised:
                self.same("opening", "drawing_to_plan", "mark", o.mark_normalised, type_op.type_mark, key=op.key,
                          answer_keys=[f"opening_mark:{source.sheet}:{o.id}"])
        if category == "window":
            self.number("opening", "drawing_to_plan", "sill", o.sill_mm, op.sill_mm, self.tol.size_mm, "mm", key=op.key,
                        answer_keys=[f"window_sill:{op.type}"])

    def op_room(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        data = (rec or {}).get("room") or {}
        if rec is not None:
            self.same("room", "plan_to_model", "name", op.name, data.get("name"), **kw)
            self.same("room", "plan_to_model", "number", op.number, data.get("number"), **kw)
            self.same("room", "plan_to_model", "department", op.flat, data.get("department"), **kw)
            self.same("room", "plan_to_model", "level", op.level, data.get("level") or rec.get("level"), **kw)
            self.point("room", "plan_to_model", "placement point", op.point, _xy((rec.get("location") or {}).get("point")),
                       **kw)
        source = self.result.sources.get(op.key)
        r = self.rec.get(source.sheet, source.element, ["rooms"]) if source else None
        if r is None:
            return
        group = next(((fid, members) for fid, members in self.rec.groups(source.sheet).items() if r.id in members), None)
        answers = [f"rooms:{source.sheet}:{group[0]}"] if group else []
        self.same("room", "drawing_to_plan", "name", r.name, op.name, key=op.key, answer_keys=answers)
        self.point("room", "drawing_to_plan", "placement point", self.rec.xf(source.sheet)(r.point), op.point, key=op.key)
        if rec is None:
            return
        drawn = r.area_m2 if r.area_m2 is not None else (polygon_area_m2(r.boundary) if r.boundary else None)
        if drawn is not None and r.boundary:
            drawn = round(drawn - self.rec.walled_islands_m2(source.sheet, r.boundary), 4)
        if group is None and drawn is not None:
            self.area("room", "drawing_to_model", "area vs drawn boundary", drawn, data.get("area_m2"), **kw)
        else:
            self.count("room")
            if not (_num(data.get("area_m2")) or 0) > 0:
                self.add("room", "drawing_to_model", "enclosed", "area > 0", data.get("area_m2"), **kw)
        if group is None and r.labelled_size_mm:
            w, l = r.labelled_size_mm
            self.area("room", "drawing_to_model", "area vs written size", w * l * M2_PER_MM2, data.get("area_m2"),
                      severity="warning", **kw)

    def op_stair(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        risers = sum(r.risers for r in op.runs)
        base_e, top_e = self.elevation.get(op.base_level), self.elevation.get(op.top_level)
        riser = None if base_e is None or top_e is None else (top_e - base_e - op.base_offset_mm) / risers
        if rec is not None:
            data = rec.get("stair") or {}
            self.same("stair", "plan_to_model", "base level", op.base_level, data.get("base_level"), **kw)
            self.same("stair", "plan_to_model", "top level", op.top_level, data.get("top_level"), **kw)
            self.number("stair", "plan_to_model", "base offset", op.base_offset_mm, data.get("base_offset_mm"),
                        self.tol.level_mm, "mm", **kw)
            self.same("stair", "plan_to_model", "risers", risers, data.get("risers"), **kw)
            self.number("stair", "plan_to_model", "riser height", riser, data.get("riser_height_mm"), self.tol.size_mm,
                        "mm", **kw)
            self.number("stair", "plan_to_model", "tread", op.tread_mm, data.get("tread_mm"), self.tol.size_mm, "mm", **kw)
            widths = data.get("run_widths_mm")
            self.same("stair", "plan_to_model", "runs", len(op.runs), len(widths) if isinstance(widths, list) else None,
                      **kw)
            for i, w in enumerate(widths if isinstance(widths, list) else []):
                self.number("stair", "plan_to_model", f"run {i + 1} width", op.width_mm, w, self.tol.size_mm, "mm", **kw)
        source = self.result.sources.get(op.key)
        st = self.rec.get(source.sheet, source.element, ["stairs"]) if source else None
        if st is not None:
            ref = f"{source.sheet}:{st.id}"
            self.number("stair", "drawing_to_plan", "width", st.width_mm, op.width_mm, self.tol.size_mm, "mm",
                        key=op.key, answer_keys=[f"stair_width:{ref}"])
            self.number("stair", "drawing_to_plan", "tread", st.tread_mm, op.tread_mm, self.tol.size_mm, "mm",
                        key=op.key, answer_keys=[f"stair_tread:{ref}"])
            if st.risers_per_flight:
                self.same("stair", "drawing_to_plan", "risers", sum(st.risers_per_flight), risers, key=op.key,
                          answer_keys=[f"stair_risers:{ref}-{source.storey}"])

    def op_railing(self, op) -> None:
        key = op.key if op.key in self.created else f"{op.stair_key}{KEY_SEPARATOR}2" if op.stair_key else op.key
        element_id, rec = self.record(key)
        if rec is not None and op.stair_key:
            self.same("railing", "plan_to_model", "host stair", self.created.get(op.stair_key),
                      (rec.get("railing") or {}).get("host_id"), key=op.key, element_id=element_id)

    def op_shaft(self, op) -> None:
        element_id, rec = self.record(op.key)
        kw = {"key": op.key, "element_id": element_id}
        points = [tuple(p) for p in op.points]
        if rec is not None:
            data = rec.get("shaft") or {}
            raw = data.get("outline") or []
            if raw and isinstance(raw[0], dict):
                raw = [raw]  # add-ins before the loop-shape fix sent one flat loop of points
            loops = [[q for q in (_xy(v) for v in loop) if q is not None] for loop in raw]
            outer = max(loops, key=polygon_area_m2) if loops else None
            self.corners("shaft", "plan_to_model", "outline corners", points, outer, **kw)
            self.same("shaft", "plan_to_model", "base level", op.base_level, data.get("base_level"), **kw)
            self.same("shaft", "plan_to_model", "top level", op.top_level, data.get("top_level"), **kw)
            self.number("shaft", "plan_to_model", "base offset", op.base_offset_mm, data.get("base_offset_mm"),
                        self.tol.level_mm, "mm", **kw)
            self.number("shaft", "plan_to_model", "top offset", op.top_offset_mm, data.get("top_offset_mm"),
                        self.tol.level_mm, "mm", **kw)
        source = self.result.sources.get(op.key)
        e = self.rec.get(source.sheet, source.element, ["shafts", "stairs"]) if source else None
        if e is not None:
            self.corners("shaft", "drawing_to_plan", "outline corners", [self.rec.xf(source.sheet)(q) for q in e.outline],
                         points, key=op.key)

    def separator_end(self, field: str, end: Point, other: Point, actual: Point | None, **kw) -> None:
        """A separation line's end. Revit lengthens an end that lies in a wall along the line (it closes the room
        the same way): moved only outward along the line, by no more than a thick wall, it is explained."""
        if actual is not None:
            length = _dist(other, end)
            u = ((end[0] - other[0]) / length, (end[1] - other[1]) / length) if length else (0.0, 0.0)
            v = (actual[0] - end[0], actual[1] - end[1])
            along, side = v[0] * u[0] + v[1] * u[1], abs(-v[0] * u[1] + v[1] * u[0])
            if side <= self.tol.position_mm and self.tol.position_mm < along <= SEPARATOR_JOIN_MM:
                self.count("room_separator")
                self.add("room_separator", "plan_to_model", field, end, actual, delta=round(along, 1),
                         tolerance=self.tol.position_mm, unit="mm",
                         reason=f"Revit lengthened the line {along:.1f} mm along itself into the wall it meets "
                                "(not moved sideways): the room closes the same", **kw)
                return
        self.point("room_separator", "plan_to_model", field, end, actual, **kw)

    def op_room_separator(self, op) -> None:
        segments = list(zip(op.points, op.points[1:])) + ([(op.points[-1], op.points[0])] if op.close_loop else [])
        for i, (a, b) in enumerate(segments):
            key = op.key if i == 0 else f"{op.key}{KEY_SEPARATOR}{i + 1}"
            element_id, rec = self.record(key)
            kw = {"key": key, "element_id": element_id}
            if element_id is None:
                if i > 0:  # the first segment is the op's own key: presence reports it
                    self.count("room_separator")
                    self.add("room_separator", "plan_to_model", f"segment {i + 1}", "built", "not built", **kw)
                continue
            if rec is None:
                continue  # reported by presence (first segment) or below
            curve = rec.get("curve") or rec.get("location") or {}
            self.separator_end(f"segment {i + 1} start", tuple(a), tuple(b), _xy(curve.get("start")), **kw)
            self.separator_end(f"segment {i + 1} end", tuple(b), tuple(a), _xy(curve.get("end")), **kw)
            self.same("room_separator", "plan_to_model", "level", op.level, rec.get("level"), **kw)

    # -------------------------------------------------------------------------------------------- accounting

    def accounting(self) -> None:
        planned: set[tuple[str, str, str]] = set()  # (sheet, element id, recognised kind)
        for key, s in self.result.sources.items():
            op = self.by_key.get(key)
            if op is None or not s.sheet or not s.element:
                continue
            for element in {s.element, s.element.split(".", 1)[0]}:
                for kind in RECOGNISED_KINDS.get(op.op, ()):
                    planned.add((s.sheet, element, kind))
        grids = [op for op in self.plan.ops if op.op == "grid"]
        stats = Counter()
        room_keys = {(s.sheet, s.element): k for k, s in self.result.sources.items()
                     if self.by_key.get(k) is not None and self.by_key[k].op == "room"}
        for (sheet, element_id), entries in sorted(self.rec.items.items()):
            for kind, e in entries:
                ref = f"{sheet}#{element_id}"
                stats["recognised"] += 1
                self.kinds[f"recognised {kind}"].ops += 1
                if (sheet, element_id, kind) in planned:
                    stats["planned"] += 1
                    continue
                if ref in self.not_modelled_refs:
                    stats["not_modelled"] += 1
                    continue
                reason = self._accounted(kind, sheet, e, grids, room_keys)
                stats["explained" if reason else "unaccounted"] += 1
                self.kinds[f"recognised {kind}"].fields += 1
                context = {"sheet": sheet, "element": element_id,
                           "handles": list(e.evidence.handles) if isinstance(getattr(e, "evidence", None), Evidence) else []}
                self.add(f"recognised {kind}", "accounting", "planned", "planned or listed as not modelled",
                         "neither", reason=reason, context=context)
        self.stats = dict(stats)

    def _accounted(self, kind: str, sheet: str, e: Any, grids: list, room_keys: dict) -> str | None:
        """The documented rule that accounts for a recognised element the plan does not name, or None."""
        tol = self.tol
        if kind == "grids":
            t = self.rec.xf(sheet)
            a, b = t(e.start), t(e.end)
            for g in grids:
                if all(_line_offset(p, g.start, g.end)[0] <= tol.position_mm + 1e-9 for p in (a, b)):
                    return f"lies on the line of grid {g.name} ({g.key})"
            return None
        if kind == "rooms":
            for fid, members in self.rec.groups(sheet).items():
                if e.id in members:
                    answer = f"rooms:{sheet}:{fid}"
                    rooms = {r.id: r for r in self.rec.plans[sheet].rooms}
                    apart, together = split_space.split_members([rooms[m] for m in members], self.answers.get(answer))
                    together = [r.id for r in together]
                    built = [m for m in together if (sheet, m) in room_keys]
                    if e.id in together and built and answer in self.answers:
                        return (f"one room with {', '.join(m for m in together if m != e.id)}, built from {built[0]}, "
                                f"as answered ({answer})")
            return None
        if kind == "section_marks":
            return "a section cut line: drawing annotation for the section views, not a model element"
        if kind == "column_sections":
            b, h = sorted((e.width_mm, e.depth_mm))
            match = next((f.name for f in self.result.standards.columns
                          if abs(f.b_mm - b) <= tol.size_mm and abs(f.h_mm - h) <= tol.size_mm), None)
            return f"a typical column section, built as column type {match}" if match else None
        if kind == "levels":
            return self._level_accounted(e)
        return None

    def _level_accounted(self, lv) -> str | None:
        tol = self.tol.level_mm
        for name, elevation in self.elevation.items():
            if abs(elevation - lv.elevation_mm) <= tol:
                return f"the same elevation as level {name} ({elevation:g} mm)"
        floor = self._storey_floor(lv.storey)
        if lv.kind in ("sill", "lintel") and floor is not None:
            rel = lv.elevation_mm - floor
            std = self.result.standards
            for o in std.windows + (std.doors if lv.kind == "lintel" else []):
                if not o.count or o.height_mm is None:
                    continue
                sill = o.sill_mm or 0.0
                value = sill if lv.kind == "sill" else sill + o.height_mm
                if abs(value - rel) <= tol:
                    what = "sill" if lv.kind == "sill" else "head"
                    return f"the {what} of {o.category} type {o.name} ({value:g} mm above storey {lv.storey}'s floor)"
            return None
        if lv.kind == "foundation":
            for op in self.plan.ops:
                if op.op == "foundation_slab" and op.type in self.floor_types:
                    bottom = self.elevation[op.level] + op.offset_mm - self.floor_types[op.type]
                    if abs(bottom - lv.elevation_mm) <= tol:
                        return f"the bottom of the footings ({op.level} - {self.floor_types[op.type]:g} mm)"
            return None
        question = f"level:storey-{lv.storey}" if lv.kind in ("floor", "plinth") and lv.storey is not None \
            else f"level:{LEVEL_QUESTION.get(lv.kind, lv.kind)}"
        if question in self.answers and self.answers[question] is not None:
            return f"the sections disagree; answered question {question} = {self.answers[question]!r}"
        return None

    def _storey_floor(self, storey: int | None) -> float | None:
        if storey is None:
            return None
        for key, source in self.result.sources.items():
            op = self.by_key.get(key)
            if op is not None and op.op == "level" and source.storey == str(storey):
                return op.elevation_mm
        return None


def _jsonable(value: Any) -> Any:
    if isinstance(value, tuple):
        return [_jsonable(v) for v in value]
    if isinstance(value, list):
        return [_jsonable(v) for v in value]
    if isinstance(value, float):
        return round(value, 4)
    return value


def compare(plan_result: ImportPlan, recognised_building: RecognisedBuilding, created_ids: Mapping[str, int],
            readback: Mapping[Any, Mapping[str, Any]] | Iterable[Mapping[str, Any]], *,
            answers: Mapping[str, Any] | None = None, tolerances: Tolerances | Mapping[str, float] | None = None,
            explanations: Iterable[Explanation | Mapping[str, Any]] = (), review_url: str | None = None) -> Report:
    """Compare the read-back model with the plan and the drawings. ``answers`` are the user's answers the plan was
    made with (they explain the plan's departures from the drawing); ``explanations`` are further documented
    reasons for known differences."""
    tol = tolerances if isinstance(tolerances, Tolerances) else Tolerances(**dict(tolerances or {}))
    rb = _index(readback)
    expl = [e if isinstance(e, Explanation) else Explanation(**e) for e in explanations]
    c = _Comparer(plan_result, recognised_building, created_ids, rb, dict(answers or {}), tol, expl)
    c.run()
    for d in c.diffs:
        summary = c.kinds[d.kind]
        if d.explained:
            summary.explained += 1
        else:
            summary.unexplained += 1
    unexplained = sum(1 for d in c.diffs if not d.explained)
    accounting = {k: c.stats.get(k, 0) for k in ("recognised", "planned", "not_modelled", "explained", "unaccounted")}
    return Report(ok=unexplained == 0, unexplained=unexplained, explained=len(c.diffs) - unexplained, tolerances=tol,
                  kinds=dict(sorted(c.kinds.items())), accounting=accounting, elements_read=len(rb),
                  differences=c.diffs, not_modelled=[n.model_dump() for n in plan_result.not_modelled],
                  review_url=review_url)


def _index(readback: Mapping[Any, Mapping[str, Any]] | Iterable[Mapping[str, Any]]) -> dict[int, Mapping[str, Any]]:
    if isinstance(readback, Mapping):
        return {int(k): v for k, v in readback.items()}
    return {int(r["id"]): r for r in readback}


# ------------------------------------------------------------------------------------------------ read-back

def read_back(revit, element_ids: Iterable[int]) -> dict[int, dict[str, Any]]:
    """Every element's read-back record (``read_back_elements``), by id: 5000 ids per call, paged under the add-in's
    response cap."""
    ids = list(dict.fromkeys(int(i) for i in element_ids))
    out: dict[int, dict[str, Any]] = {}
    for at in range(0, len(ids), READ_BACK_MAX_IDS):
        chunk, start = ids[at:at + READ_BACK_MAX_IDS], 0
        while True:
            data = revit.call(READ_BACK_COMMAND, {"element_ids": chunk, "start": start})
            for record in data["elements"]:
                out[int(record["id"])] = record
            following = data.get("next_start")
            if following is None:
                break
            if following <= start:
                raise RuntimeError(f"{READ_BACK_COMMAND} did not advance (start {start}, next_start {following})")
            start = following
    return out


def element_ids(plan: BuildPlan, created: Mapping[str, int]) -> list[int]:
    """The model elements a run created (types the plan's pre-steps made are not read back)."""
    by_key = plan.by_key()
    return [int(v) for k, v in created.items() if op_key(k) not in by_key or by_key[op_key(k)].op not in TYPE_OPS]


def after_build_selfcheck(plan_result: ImportPlan, recognised_building: RecognisedBuilding,
                          answers: Mapping[str, Any] | None = None, *,
                          tolerances: Tolerances | Mapping[str, float] | None = None,
                          explanations: Iterable[Explanation | Mapping[str, Any]] = (),
                          review_url: str | None = None):
    """A BuildService.start(after_build=...) hook: reads the built elements back, compares, writes selfcheck.json and
    selfcheck.html into the run folder and returns the summary (stored as the run's ``checks``)."""
    explanations = list(explanations)

    def hook(revit, run, created: dict[str, int]) -> dict[str, Any]:
        readback = read_back(revit, element_ids(plan_result.plan, created))
        report = compare(plan_result, recognised_building, created, readback, answers=answers, tolerances=tolerances,
                         explanations=explanations, review_url=review_url)
        files = write(report, run.dir)
        return {"selfcheck": report.summary(), "files": [p.name for p in files]}

    return hook


# ------------------------------------------------------------------------------------------------ output

def write(report: Report, directory: Path) -> list[Path]:
    directory = Path(directory)
    paths = []
    for name, text in ((JSON_FILE, json.dumps(report.model_dump(mode="json"), indent=1, ensure_ascii=False)),
                       (HTML_FILE, to_html(report))):
        path, temp = directory / name, directory / f".{name}.tmp"
        temp.write_text(text, encoding="utf-8")
        temp.replace(path)
        paths.append(path)
    return paths


_STAGES = {"plan_to_model": "plan → model", "drawing_to_plan": "drawing → plan", "drawing_to_model": "drawing → model",
           "accounting": "accounting"}


def to_html(report: Report) -> str:
    e = html.escape

    def value(v: Any) -> str:
        return "" if v is None else e(json.dumps(v, ensure_ascii=False) if not isinstance(v, str) else v)

    def evidence(d: Difference) -> str:
        if not d.sheet:
            return ""
        text = e(d.sheet) + (f" · {e(d.element)}" if d.element else "") + (f" · storey {e(d.storey)}" if d.storey else "")
        handles = f"<div class=h>handles {e(', '.join(d.handles))}</div>" if d.handles else ""
        if report.review_url:
            text = f'<a href="{e(report.review_url)}">{text}</a>'
        return text + handles

    by_kind: dict[str, list[Difference]] = defaultdict(list)
    for d in report.differences:
        by_kind[d.kind].append(d)
    rows = "".join(
        f"<tr><td>{e(k)}</td><td>{s.ops}</td><td>{s.fields}</td>"
        f"<td class={'bad' if s.unexplained else 'good'}>{s.unexplained}</td><td>{s.explained}</td></tr>"
        for k, s in report.kinds.items())
    sections = []
    for kind in sorted(by_kind, key=lambda k: (k.startswith("recognised"), k)):
        items = sorted(by_kind[kind], key=lambda d: (d.explained, d.stage, d.op_key or "", d.field))
        body = "".join(
            f"<tr class={'expl' if d.explained else 'unexpl'}><td>{e(_STAGES[d.stage])}</td><td>{e(d.op_key or '')}</td>"
            f"<td>{'' if d.element_id is None else d.element_id}</td><td>{e(d.field)}</td><td>{value(d.expected)}</td>"
            f"<td>{value(d.actual)}</td><td>{'' if d.delta is None else d.delta}"
            f"{'' if d.tolerance is None else f' (± {d.tolerance:g} {e(d.unit or '')})'}</td><td>{e(d.severity)}</td>"
            f"<td>{e(d.reason) if d.explained else '<b>unexplained</b>'}</td><td>{evidence(d)}</td></tr>"
            for d in items)
        sections.append(
            f"<h2>{e(kind)} <small>{sum(not d.explained for d in items)} unexplained, "
            f"{sum(d.explained for d in items)} explained</small></h2><table><tr><th>stage</th><th>op</th><th>element</th>"
            f"<th>field</th><th>expected</th><th>actual</th><th>delta</th><th>severity</th><th>explanation</th>"
            f"<th>evidence</th></tr>{body}</table>")
    nm = "".join(f"<li>{e(n['what'])}: {e(n['reason'])} ({len(n['elements'])})</li>" for n in report.not_modelled)
    tol = report.tolerances
    acc = report.accounting
    verdict = ("No unexplained differences." if report.ok else f"{report.unexplained} unexplained difference"
               f"{'s' if report.unexplained != 1 else ''}.")
    return f"""<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content="width=device-width">
<title>B2M self-check</title><style>
:root{{--fg:#1d2330;--bg:#fff;--muted:#667;--line:#dde;--bad:#b3261e;--good:#1b6e3a;--soft:#f5f6f8}}
@media (prefers-color-scheme:dark){{:root{{--fg:#e6e8ee;--bg:#15171c;--muted:#99a;--line:#333844;--bad:#ff8a80;--good:#7bd88f;--soft:#1e2128}}}}
body{{font:14px/1.45 system-ui,sans-serif;color:var(--fg);background:var(--bg);margin:24px auto;max-width:1400px;padding:0 16px}}
h1{{font-size:22px}} h2{{font-size:17px;margin-top:28px}} small{{color:var(--muted);font-weight:normal}}
table{{border-collapse:collapse;width:100%;font-size:13px}} td,th{{border-bottom:1px solid var(--line);padding:4px 6px;
text-align:left;vertical-align:top}} th{{background:var(--soft)}} .bad,.unexpl td:nth-child(9){{color:var(--bad)}}
.good{{color:var(--good)}} .expl{{color:var(--muted)}} .h{{color:var(--muted);font-size:12px;word-break:break-all}}
.verdict{{font-size:16px;font-weight:600}}</style></head><body>
<h1>Self-check: the model against the plan and the drawings</h1>
<p class="verdict {'good' if report.ok else 'bad'}">{e(verdict)} {report.explained} explained. {report.elements_read} elements read back.</p>
<p>Tolerances: positions {tol.position_mm:g} mm, sizes {tol.size_mm:g} mm, levels {tol.level_mm:g} mm, areas
{tol.area_m2:g} m² or {tol.area_pct:g} % (the larger), angles {tol.angle_deg:g}°.</p>
<p>Recognised elements: {acc.get('recognised', 0)}; planned {acc.get('planned', 0)}, not modelled (listed)
{acc.get('not_modelled', 0)}, accounted by a rule {acc.get('explained', 0)}, unaccounted {acc.get('unaccounted', 0)}.</p>
<table><tr><th>kind</th><th>compared</th><th>values</th><th>unexplained</th><th>explained</th></tr>{rows}</table>
{''.join(sections)}
<h2>Not modelled (listed by the plan)</h2><ul>{nm}</ul>
</body></html>"""
