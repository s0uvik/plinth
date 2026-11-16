"""Recognised building -> build plan (PRD Step 9, T6).

    from b2m.imported import to_plan
    result = to_plan(building, answers)          # answers: {missing id: value}, e.g. project.inputs.values
    if result.plan is None: result.missing       # what the drawings do not give: T5 turns these into questions

No guessing. Every value comes from the recognised building (the drawings) or from an answer. When a value an element
needs is absent, the conversion refuses: ``plan`` is None and ``missing`` lists each value with the elements it
affects and the options the drawings suggest; nothing is filled in silently. What has no command or no rule yet is
listed in ``not_modelled``, never dropped. The result is deterministic: the same building and answers give the same
plan.

Op keys are "<sheet>-<element id>-<storey>" ("2.PLAN 1st to 4th.dxf-wall-12-3"; storey "roof" on the roof plan and
"foundation" on the footing plan), and ``sources`` gives each op key's sheet, element id and storey, so the
self-check (T9) can compare the built model with the recognised element. Ops that come from no single element
(levels, types, slab outlines) carry the evidence they came from instead.

Mapping rules (each in its own method below):
- Levels from the sections (registered to one datum): storey floors ("Ground Floor", "Floor 1".."Floor n"), "Roof",
  "Parapet Top", "GL"/"Plinth" when apart from a floor, "Foundation" (top of the footings) and "Upper Roof" (what
  stands on the roof). A plan's storeys (typical plan: 1-4) replicate its elements per storey.
- Grids from the floor plans, merged where collinear; drawn names kept, others numbered (1, 2.. across x) and
  lettered (A, B.. up y).
- Walls per storey, base the storey's level, top the next level; wall type per (thickness, exterior/interior) made
  sure of by an ensure_wall_type pre-step; parapets with their height (drawing, else sections), boundary walls with
  theirs (drawing, else asked); walls on the roof plan that are not parapets rise to "Upper Roof".
- Columns per storey from the plan's hatches, type "RCC Column b x h" (b <= h, rotated 90 deg when wider in x);
  footing-plan columns from the foundation top to the lowest floor; roof-plan columns to "Upper Roof".
- Beams from beam layouts at their storey's level (tie beams at the plinth); depth from the layout or asked.
- Slabs per floor level from the plan outline (roof slab from the roof plan's outline); the lowest floor's build-up
  (PCC, BFS...) as type layers when the sections give it; balconies/verandahs as slabs at floor level; chajjas,
  canopies and ledges at the head of the opening they shade (offset = head + thickness); shafts (lifts, ducts) cut
  the slab of the storey they are drawn on; a stair cuts the slab it climbs to; footings as foundation slabs.
- Doors and windows by mark (unmarked: by size), width from the plan (asked when a mark is drawn at several
  widths), height and sill from the drawing or asked; hosted on their wall at the plan position, flipped per swing
  and hinge; a door in no wall is asked about (skip, or a wall across the gap).
- Rooms with the name as written, number "<storey>-<unit>-<n>" ("<storey>-<n>" outside units), department = unit;
  rooms sharing one space and rooms not enclosed are asked about (one room, separate with lines, or skip).
- Stairs per storey from the plan: risers from the plan's count when within the rule set's riser limit (else
  asked), tread and width from the plan, landing end asked, first flight from the up arrow; a railing on each.
"""

from __future__ import annotations

import math
from collections import Counter, defaultdict
from typing import Any, Iterable, Literal, Mapping

from pydantic import BaseModel, ConfigDict
from shapely.geometry import LineString, Point as SPoint, Polygon

from b2m.cad.building import (Column, Opening, PlanRecognition, Projection, RecognisedBuilding, Registration, Room,
                              Stair, Wall)
from b2m.imported.standards import (BEAM_FAMILY, COLUMN_FAMILY, FloorStandard, FrameStandard, OpeningStandard,
                                    ProjectStandards, WallStandard, beam_type_name, column_type_name, floor_type_name,
                                    wall_type_name)
from b2m.plan import BuildPlan, TypeLayer
from b2m.rules import rule_set

Point = tuple[float, float]
Answers = Mapping[str, Any]

ROOF, FOUNDATION = "roof", "foundation"  # storey tokens in op keys
SHAFT_MARGIN = 50  # a shaft reaches this far above and below the slab it cuts, so it cuts it cleanly
SIDE_PROBE = 300  # how far beside a wall's face a point is tested to tell the wall's inside from its outside
SHADE_REACH = 100  # a chajja shades the opening whose centre is within its wall's half thickness + this
# Door orientation as the add-in places it unflipped (create_point_based_element on a wall from start to end): the
# leaf opens to the wall's right of start->end, the hinge on the end side. flip_facing / flip_hand set the rest.
DEFAULT_OPENS_LEFT = False
DEFAULT_HINGE_AT_END = True


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Missing(_Model):
    """A value the plan needs that the drawings do not give. ``id`` is the answer key."""

    id: str
    topic: str
    text: str
    elements: list[str] = []  # "<sheet>#<element id>"
    options: list[Any] = []
    unit: str | None = None


class NotModelled(_Model):
    what: str
    reason: str
    elements: list[str] = []


class Source(_Model):
    sheet: str | None = None
    element: str | None = None  # recognised element id (or level name as written)
    storey: str | None = None
    note: str | None = None


class ImportPlan(_Model):
    plan: BuildPlan | None = None  # None while anything is missing
    missing: list[Missing] = []
    not_modelled: list[NotModelled] = []
    sources: dict[str, Source] = {}
    standards: ProjectStandards
    counts: dict[str, int] = {}  # "<op>@<level>" -> ops (when a plan was made)


def to_plan(building: RecognisedBuilding, answers: Answers | None = None, rules: str = "nbc2016") -> ImportPlan:
    return _Converter(building, dict(answers or {}), rules).run()


def op_counts(plan: BuildPlan) -> dict[str, int]:
    """Ops per kind and level ("wall@Floor 1"), for pinning and read-back."""
    out: Counter[str] = Counter()
    by_key = plan.by_key()
    for op in plan.ops:
        level = getattr(op, "level", None) or getattr(op, "base_level", None)
        if level is None and getattr(op, "host_key", None):
            level = by_key[op.host_key].level
        if level is None and getattr(op, "stair_key", None):
            level = by_key[op.stair_key].base_level
        out[f"{op.op}@{level}" if level else op.op] += 1
    return dict(sorted(out.items()))


# ------------------------------------------------------------------------------------------------ geometry helpers

def _xf(reg: Registration | None):
    if reg is None:
        return lambda p: (round(p[0], 2), round(p[1], 2))
    r = math.radians(reg.rotation_deg)
    c, s = math.cos(r), math.sin(r)
    tx, ty = reg.translation
    return lambda p: (round(c * p[0] - s * p[1] + tx, 2), round(s * p[0] + c * p[1] + ty, 2))


def _sub(a: Point, b: Point) -> Point:
    return (a[0] - b[0], a[1] - b[1])


def _dot(a: Point, b: Point) -> float:
    return a[0] * b[0] + a[1] * b[1]


def _unit(a: Point, b: Point) -> tuple[Point, float]:
    d = _sub(b, a)
    length = math.hypot(*d)
    return ((d[0] / length, d[1] / length) if length else (0.0, 0.0)), length


def _ref(sheet: str, element_id: str) -> str:
    return f"{sheet}#{element_id}"


def _num(value: Any) -> float | None:
    try:
        v = float(value)
    except (TypeError, ValueError):
        return None
    return v if v > 0 and math.isfinite(v) else None


class _Levels:
    def __init__(self):
        self.ops: list[dict] = []
        self.by_storey: dict[int, str] = {}
        self.elevation: dict[str, float] = {}
        self.roof: str | None = None
        self.parapet_top: str | None = None
        self.foundation: str | None = None
        self.upper_roof: str | None = None
        self.plinth: str | None = None

    def add(self, name: str, elevation: float, source: Source, sources: dict[str, Source]) -> str:
        key = f"LEVEL-{name}"
        self.ops.append({"op": "level", "key": key, "name": name, "elevation_mm": round(elevation, 1)})
        self.elevation[name] = elevation
        sources[key] = source
        return name

    def above(self, storey: int) -> str | None:
        if not self.by_storey:
            return None
        if storey + 1 in self.by_storey:
            return self.by_storey[storey + 1]
        if storey == max(self.by_storey) and self.roof:
            return self.roof
        return None


class _Converter:
    def __init__(self, building: RecognisedBuilding, answers: dict[str, Any], rules: str):
        self.b = building
        self.answers = answers
        self.rules = rules
        self.missing: dict[str, Missing] = {}
        self.not_modelled: list[NotModelled] = []
        self.sources: dict[str, Source] = {}
        self.ops: list[dict] = []
        self.levels = _Levels()
        self.regs = {r.sheet: r for r in building.registrations if r.frame == "plan"}
        self.vregs = {r.sheet: r for r in building.registrations if r.frame == "vertical"}
        self.walls: dict[tuple[float, str], WallStandard] = {}
        self.floors: dict[str, FloorStandard] = {}
        self.openings: dict[tuple[str, str], OpeningStandard] = {}
        self.frames: dict[tuple[str, str], FrameStandard] = {}
        self.notes: list[str] = []
        self.wall_keys: dict[tuple[str, str, str], str] = {}  # (sheet, wall id, storey) -> op key
        self.slab_levels: set[str] = set()
        self.slab_thickness: float | None = None

    # ---------------------------------------------------------------------------------------- bookkeeping

    def need(self, id_: str, topic: str, text: str, elements: Iterable[str] = (), options: Iterable[Any] = (),
             unit: str | None = None) -> Any:
        """The answer to ``id_``, or None after recording it as missing."""
        if id_ in self.answers and self.answers[id_] is not None:
            return self.answers[id_]
        m = self.missing.get(id_)
        if m is None:
            m = self.missing[id_] = Missing(id=id_, topic=topic, text=text, options=list(options), unit=unit)
        for e in elements:
            if e not in m.elements:
                m.elements.append(e)
        return None

    def need_mm(self, id_: str, topic: str, text: str, elements: Iterable[str] = (), options: Iterable[Any] = ()) -> float | None:
        value = self.need(id_, topic, text, elements, options, unit="mm")
        if value is None:
            return None
        v = _num(value)
        if v is None:
            self.missing[id_] = Missing(id=id_, topic=topic, text=f"{text} (the answer {value!r} is not a length in mm)",
                                        elements=list(elements), options=list(options), unit="mm")
        return v

    def skip(self, what: str, reason: str, elements: Iterable[str] = ()) -> None:
        for n in self.not_modelled:
            if n.what == what and n.reason == reason:
                n.elements += [e for e in elements if e not in n.elements]
                return
        self.not_modelled.append(NotModelled(what=what, reason=reason, elements=list(elements)))

    def emit(self, op: dict, source: Source) -> str:
        self.ops.append(op)
        self.sources[op["key"]] = source
        return op["key"]

    def xf(self, sheet: str):
        reg = self.regs.get(sheet)
        if reg is None and self.regs:
            self.need(f"registration:{sheet}", "registration",
                      f"{sheet} could not be placed on the building's grid; its elements cannot be positioned.",
                      [_ref(sheet, "sheet")])
        return _xf(reg)

    # ---------------------------------------------------------------------------------------- types

    def wall_type(self, thickness: float, function: str) -> str:
        key = (thickness, function)
        std = self.walls.get(key)
        if std is None:
            std = self.walls[key] = WallStandard(name=wall_type_name(thickness, function), thickness_mm=thickness,
                                                 function=function)
        std.count += 1
        return std.name

    def floor_type(self, thickness: float, use: str, layers: list[TypeLayer] | None = None,
                   layer_names: list[str] | None = None) -> str:
        name = floor_type_name(thickness, use, layer_names, layers)
        std = self.floors.get(name)
        if std is None:
            std = self.floors[name] = FloorStandard(name=name, use=use, thickness_mm=thickness,
                                                    foundation=use == "foundation", layers=layers,
                                                    layer_names=list(layer_names or []))
        std.count += 1
        return name

    def frame_type(self, category: str, b: float, h: float) -> tuple[str, str]:
        name = column_type_name(b, h) if category == "column" else beam_type_name(b, h)
        family = COLUMN_FAMILY if category == "column" else BEAM_FAMILY
        std = self.frames.get((category, name))
        if std is None:
            std = self.frames[(category, name)] = FrameStandard(name=name, category=category, family=family, b_mm=b, h_mm=h)
        std.count += 1
        return name, family

    # ---------------------------------------------------------------------------------------- run

    def run(self) -> ImportPlan:
        floor_plans = sorted((p for p in self.b.plans if p.role == "floor_plan"), key=lambda p: (min(p.storeys or [0]), p.sheet))
        roof_plans = [p for p in self.b.plans if p.role == "roof_plan"]
        self.make_levels(floor_plans, roof_plans)
        self.make_grids(floor_plans)
        self.slab_thickness = self.section_value("slab_thickness_mm", "slab_thickness", "the floor slab thickness")
        self.catalogue()
        storey_plan: dict[int, PlanRecognition] = {}
        for p in floor_plans:
            for s in p.storeys:
                if s in storey_plan:
                    self.need(f"storey_plan:{s}", "storey_plan",
                              f"Storey {s} is drawn on two plans ({storey_plan[s].sheet}, {p.sheet}). Which one is it?",
                              [_ref(p.sheet, "sheet")], [storey_plan[s].sheet, p.sheet])
                    continue
                storey_plan[s] = p
        for s in sorted(storey_plan):
            self.storey(storey_plan[s], s)
        for p in roof_plans:
            self.roof_plan(p)
        self.slabs(storey_plan, roof_plans)
        self.vertical_openings(storey_plan)
        self.foundations()
        self.beams()
        self.report_unmodelled()

        standards = ProjectStandards(
            walls=sorted(self.walls.values(), key=lambda w: (w.function, w.thickness_mm)),
            floors=sorted(self.floors.values(), key=lambda f: f.name),
            doors=sorted((o for o in self.openings.values() if o.category == "door"), key=lambda o: o.name),
            windows=sorted((o for o in self.openings.values() if o.category == "window"), key=lambda o: o.name),
            columns=sorted((f for f in self.frames.values() if f.category == "column"), key=lambda f: f.name),
            beams=sorted((f for f in self.frames.values() if f.category == "beam"), key=lambda f: f.name),
            notes=self.notes)
        missing = sorted(self.missing.values(), key=lambda m: m.id)
        if missing:
            return ImportPlan(missing=missing, not_modelled=self.not_modelled, standards=standards)
        ops = self.type_ops(standards) + self.levels.ops + self.ops
        plan = BuildPlan.model_validate({"ops": ops})
        sources = {op.key: self.sources[op.key] for op in plan.ops}
        return ImportPlan(plan=plan, not_modelled=self.not_modelled, sources=sources, standards=standards,
                          counts=op_counts(plan))

    def type_ops(self, standards: ProjectStandards) -> list[dict]:
        ops: list[dict] = []
        for w in standards.walls:
            ops.append({"op": "wall_type", "key": f"TYPE-wall-{w.name}", "name": w.name, "thickness_mm": w.thickness_mm,
                        "function": w.function})
        for f in standards.floors:
            op = {"op": "floor_type", "key": f"TYPE-floor-{f.name}", "name": f.name, "thickness_mm": f.thickness_mm,
                  "structural": f.structural, "foundation": f.foundation}
            if f.layers:
                op["layers"] = [layer.model_dump() for layer in f.layers]
            ops.append(op)
        for o in standards.doors + standards.windows:
            if not o.count:
                continue
            op = {"op": "opening_type", "key": f"TYPE-{o.category}-{o.name}", "name": o.name, "category": o.category,
                  "width_mm": o.width_mm, "height_mm": o.height_mm}
            if o.mark:
                op["type_mark"] = o.mark
            if o.hint:
                op["hint"] = o.hint
            ops.append(op)
        for op in ops:
            self.sources[op["key"]] = Source(note="project standards")
        return ops

    # ---------------------------------------------------------------------------------------- levels

    def section_levels(self):
        """(kind, storey) -> [(elevation, section sheet, name as written)], sections registered vertically first."""
        sections = [s for s in self.b.sections if not self.vregs or s.sheet in self.vregs]
        out: dict[tuple[str, int | None], list[tuple[float, str, str]]] = defaultdict(list)
        for s in sections:
            for lv in s.levels:
                out[(lv.kind, lv.storey if lv.kind in ("floor", "plinth") else None)].append((lv.elevation_mm, s.sheet, lv.name))
        return out

    def agreed(self, values: list[tuple[float, str, str]], id_: str, what: str) -> tuple[float, str, str] | None:
        elevations = sorted({round(v[0], 1) for v in values})
        if len(elevations) > 1 and elevations[-1] - elevations[0] > 1:
            answer = self.need_mm(f"level:{id_}", "level_conflict",
                                  f"The sections give {what} at different elevations: "
                                  f"{', '.join(f'{e:g}' for e in elevations)} mm. Which is right?",
                                  [_ref(v[1], v[2]) for v in values], elevations)
            return (answer, values[0][1], values[0][2]) if answer is not None else None
        return values[0]

    def make_levels(self, floor_plans: list[PlanRecognition], roof_plans: list[PlanRecognition]) -> None:
        sec = self.section_levels()
        lv = self.levels
        storeys = sorted({s for p in floor_plans for s in p.storeys})
        floors: dict[int, tuple[float, str, str]] = {}
        for storey in sorted({st for (k, st) in sec if k in ("floor", "plinth") and st is not None} | set(storeys)):
            values = sec.get(("floor", storey), []) + sec.get(("plinth", storey), [])
            if not values:
                if storey in storeys:
                    self.need_mm(f"level:storey-{storey}", "level",
                                 f"No section gives the floor level of storey {storey}. At what elevation is it (mm)?",
                                 [_ref(p.sheet, "sheet") for p in floor_plans if storey in p.storeys])
                continue
            got = self.agreed(values, f"storey-{storey}", f"the floor of storey {storey}")
            if got is not None:
                floors[storey] = got
        answered = {s: _num(self.answers.get(f"level:storey-{s}")) for s in storeys}
        for s, v in answered.items():
            if v is not None and s not in floors:
                floors[s] = (v, "", f"storey {s} (answer)")
        for s in sorted(floors):
            name = "Ground Floor" if s == 0 else f"Floor {s}" if s > 0 else f"Basement {-s}"
            e, sheet, written = floors[s]
            lv.by_storey[s] = lv.add(name, e, Source(sheet=sheet or None, element=written, storey=str(s)), self.sources)
        roof = sec.get(("roof", None), [])
        if roof:
            got = self.agreed(roof, "roof", "the roof")
            if got:
                lv.roof = lv.add("Roof", got[0], Source(sheet=got[1], element=got[2]), self.sources)
        elif floor_plans or roof_plans:
            v = self.need_mm("level:roof", "level", "No section gives the roof level. At what elevation is it (mm)?")
            if v is not None:
                lv.roof = lv.add("Roof", v, Source(note="answer"), self.sources)
        taken = {round(e, 1) for e in lv.elevation.values()}
        for kind, name in (("parapet_top", "Parapet Top"), ("ground", "GL")):
            values = sec.get((kind, None), [])
            if values:
                got = self.agreed(values, kind, name)
                if got and round(got[0], 1) not in taken:
                    lv.add(name, got[0], Source(sheet=got[1], element=got[2]), self.sources)
                    taken.add(round(got[0], 1))
                    if kind == "parapet_top":
                        lv.parapet_top = name
        plinth = [v for (k, st), vs in sec.items() if k == "plinth" for v in vs]
        if plinth:
            got = self.agreed(plinth, "plinth", "the plinth")
            if got and round(got[0], 1) not in taken:
                lv.plinth = lv.add("Plinth", got[0], Source(sheet=got[1], element=got[2]), self.sources)
                taken.add(round(got[0], 1))
            elif got:
                lv.plinth = next((n for n, e in lv.elevation.items() if round(e, 1) == round(got[0], 1)), None)
        foundation = sorted(sec.get(("foundation", None), []))
        if foundation and self.b.foundations:
            # The founding level and the top of the footings: the top is the higher one; with one level only, the
            # footings' depth tells which it is.
            top = foundation[-1] if len({round(v[0]) for v in foundation}) > 1 else None
            if top is None:
                v = self.need_mm("level:foundation", "level",
                                 "The sections give one foundation level only. At what elevation is the top of the "
                                 "footings (mm)?", [_ref(v[1], v[2]) for v in foundation], [foundation[0][0]])
                top = (v, "", "answer") if v is not None else None
            if top is not None:
                lv.foundation = lv.add("Foundation", top[0], Source(sheet=top[1] or None, element=top[2]), self.sources)
        roof_e = lv.elevation.get(lv.roof) if lv.roof else None
        above = sorted(v for v in sec.get(("other", None), []) if roof_e is not None and v[0] > roof_e + 1)
        if above:
            if len({round(v[0]) for v in above}) == 1:
                lv.upper_roof = lv.add("Upper Roof", above[0][0], Source(sheet=above[0][1], element=above[0][2]),
                                       self.sources)
            elif any(p.walls or p.columns for p in roof_plans):
                heights = sorted({round(v[0] - roof_e, 1) for v in above})
                v = self.need_mm("level:roof_structure", "level",
                                 "The sections dimension what stands on the roof (lift room, stair cover) at "
                                 f"{', '.join(f'{h:g}' for h in heights)} mm above the roof. How high is its top above "
                                 "the roof (mm)?", [_ref(v[1], v[2]) for v in above], heights)
                if v is not None:
                    lv.upper_roof = lv.add("Upper Roof", roof_e + v, Source(note="answer", element="level:roof_structure"),
                                           self.sources)

    # ---------------------------------------------------------------------------------------- grids

    def make_grids(self, floor_plans: list[PlanRecognition]) -> None:
        lines: list[dict] = []  # merged collinear lines
        for p in floor_plans:
            t = self.xf(p.sheet)
            for g in p.grids:
                a, b = t(g.start), t(g.end)
                u, length = _unit(a, b)
                if length == 0:
                    continue
                if u[0] < -1e-9 or (abs(u[0]) <= 1e-9 and u[1] < 0):
                    a, b, u = b, a, (-u[0], -u[1])
                n = (-u[1], u[0])
                for line in lines:
                    if abs(abs(_dot(line["u"], u)) - 1) < 1e-6 and abs(_dot(_sub(a, line["a"]), line["n"])) <= 1:
                        ts = [_dot(_sub(q, line["a"]), line["u"]) for q in (a, b)] + [0, line["len"]]
                        lo, hi = min(ts), max(ts)
                        origin = line["a"]
                        line["a"] = (round(origin[0] + line["u"][0] * lo, 2), round(origin[1] + line["u"][1] * lo, 2))
                        line["b"] = (round(origin[0] + line["u"][0] * hi, 2), round(origin[1] + line["u"][1] * hi, 2))
                        line["len"] = hi - lo
                        line["refs"].append((p.sheet, g.id))
                        line["name"] = line["name"] or g.name
                        break
                else:
                    lines.append({"a": a, "b": b, "u": u, "n": n, "len": length, "refs": [(p.sheet, g.id)], "name": g.name})
        used = {line["name"] for line in lines if line["name"]}
        vertical = sorted((ln for ln in lines if not ln["name"] and abs(ln["u"][1]) >= abs(ln["u"][0])),
                          key=lambda ln: (ln["a"][0], ln["a"][1]))
        horizontal = sorted((ln for ln in lines if not ln["name"] and abs(ln["u"][1]) < abs(ln["u"][0])),
                            key=lambda ln: (ln["a"][1], ln["a"][0]))
        numbers = (str(i) for i in range(1, 10000) if str(i) not in used)
        for ln in vertical:
            ln["name"] = next(numbers)
        letters = (name for name in _letters() if name not in used)
        for ln in horizontal:
            ln["name"] = next(letters)
        for ln in lines:
            sheet, gid = ln["refs"][0]
            key = f"{sheet}-{gid}"
            self.emit({"op": "grid", "key": key, "name": ln["name"], "start": ln["a"], "end": ln["b"]},
                      Source(sheet=sheet, element=gid, note=f"merged from {len(ln['refs'])} centre lines"
                             if len(ln["refs"]) > 1 else None))

    # ---------------------------------------------------------------------------------------- section values

    def section_value(self, field: str, id_: str, what: str) -> float | None:
        values = sorted({getattr(s, field) for s in self.b.sections if getattr(s, field) is not None})
        if len(values) == 1:
            return values[0]
        return self.need_mm(id_, id_, (f"The sections give {what} as {', '.join(f'{v:g}' for v in values)} mm. Which is it?"
                                       if values else f"No section gives {what}. How much is it (mm)?"),
                            [_ref(s.sheet, s.name) for s in self.b.sections], values)

    # ---------------------------------------------------------------------------------------- doors and windows

    def opening_key(self, sheet: str, o: Opening) -> tuple[str, str, str | None]:
        """(category, logical type name, mark) of an opening."""
        category = "door" if o.kind == "door" else "window"
        mark = self.answers.get(f"opening_mark:{sheet}:{o.id}") or o.mark_normalised
        if mark:
            return category, str(mark), str(mark)
        name = f"{category}-{o.width_mm:g}" + ("-sliding" if o.swing == "sliding" else "")
        return category, name, None

    def catalogue(self) -> None:
        """The door/window catalogue: every type with its width, height and sill (from the drawings or asked)."""
        drawn: dict[tuple[str, str], list[tuple[str, Opening]]] = defaultdict(list)
        for p in self.b.plans:
            for o in p.openings:
                if o.kind in ("door", "window", "ventilator"):
                    category, name, mark = self.opening_key(p.sheet, o)
                    drawn[(category, name)].append((p.sheet, o))
        sections = self.b.sections
        door_options = sorted({h for s in sections for h in s.door_heights_mm})
        sill_options = sorted({h for s in sections for h in s.window_sills_mm})
        head_options = sorted({h for s in sections for h in s.window_heads_mm})
        for (category, name), items in sorted(drawn.items()):
            refs = [_ref(sh, o.id) for sh, o in items]
            mark = name if any(self.opening_key(sh, o)[2] for sh, o in items) else None
            widths = Counter(o.width_mm for _, o in items)
            std = OpeningStandard(name=name, category=category, mark=mark,
                                  hint="sliding" if any(o.swing == "sliding" for _, o in items) else None)
            verb = "is" if mark else "are"
            label = f"{category} {name}" if mark else (
                f"the unmarked {'sliding ' if std.hint == 'sliding' else ''}{category}s {name.split('-')[1]} mm wide")
            if len(widths) == 1:
                std.width_mm = next(iter(widths))
            else:
                std.widths_drawn = {f"{w:g}": n for w, n in sorted(widths.items())}
                std.width_mm = self.need_mm(f"{category}_width:{name}", f"{category}_width",
                                            f"Openings marked {name} are drawn at different widths "
                                            f"({', '.join(f'{w:g} mm x{n}' for w, n in sorted(widths.items()))}). "
                                            f"A mark is one type: which width is {name}?", refs, sorted(widths))
            heights = {o.height_mm for _, o in items if o.height_mm is not None}
            if len(heights) == 1:
                std.height_mm = heights.pop()
            else:
                options = door_options if category == "door" else sorted(
                    {round(h - s) for h in head_options for s in sill_options if h > s})
                std.height_mm = self.need_mm(f"{category}_height:{name}", f"{category}_height",
                                             f"How high {verb} {label} (mm)? The drawings give no height.",
                                             refs, options)
            if category == "window":
                sills = {o.sill_mm for _, o in items if o.sill_mm is not None}
                if len(sills) == 1:
                    std.sill_mm = sills.pop()
                else:
                    std.sill_mm = self.need_mm(f"window_sill:{name}", "window_sill",
                                               f"At what sill height {verb} {label} (mm above the floor)? The drawings do "
                                               "not dimension it.", refs, sill_options)
            self.openings[(category, name)] = std

    def head_of(self, sheet: str, o: Opening) -> float | None:
        category, name, _ = self.opening_key(sheet, o)
        std = self.openings.get((category, name))
        if std is None or std.height_mm is None:
            return None
        return std.height_mm + (std.sill_mm or 0) if category == "window" else std.height_mm

    def place_opening(self, p: PlanRecognition, o: Opening, storey: str, level: str, top: str | None,
                      walls: dict[str, Wall], inside) -> None:
        sheet, t = p.sheet, self.xf(p.sheet)
        ref = _ref(sheet, o.id)
        if o.kind == "opening":
            self.skip("plain wall openings (no door or window)", "no wall-opening command in the build plan yet", [ref])
            return
        category, name, _ = self.opening_key(sheet, o)
        std = self.openings[(category, name)]
        host_wall = walls.get(o.wall_id) if o.wall_id else None
        if host_wall is not None:
            host_key = self.wall_keys.get((sheet, host_wall.id, storey))
            if host_key is None:
                return  # its wall waits for a missing value (listed with the wall)
            start, end = t(host_wall.start), t(host_wall.end)
            thickness = host_wall.thickness_mm
        else:
            answer = self.need(f"opening_host:{sheet}:{o.id}", "opening_host",
                               f"A {category} ({o.width_mm:g} mm) on {sheet} lies in no wall. Skip it, or put a wall "
                               "across its gap (give thickness_mm and function exterior/interior)?", [ref],
                               ["skip", {"mode": "new_wall", "thickness_mm": None, "function": "interior"}])
            if answer is None or answer == "skip" or (isinstance(answer, dict) and answer.get("mode") == "skip"):
                if answer is not None:
                    self.skip(f"{category}s in no wall", "the answer was to skip them", [ref])
                return
            thickness = _num(answer.get("thickness_mm")) if isinstance(answer, dict) else None
            function = answer.get("function") if isinstance(answer, dict) else None
            if o.hinge is None or thickness is None or function not in ("exterior", "interior") or top is None:
                self.missing[f"opening_host:{sheet}:{o.id}"] = Missing(
                    id=f"opening_host:{sheet}:{o.id}", topic="opening_host", elements=[ref],
                    text=f"The answer for the {category} in no wall needs mode new_wall, thickness_mm and function "
                         "(and the drawing a hinge to give the gap's direction).")
                return
            c, h = t(o.center), t(o.hinge)
            start, end = h, (round(2 * c[0] - h[0], 2), round(2 * c[1] - h[1], 2))
            host_key = self.emit({"op": "wall", "key": f"{sheet}-{o.id}.wall-{storey}", "level": level,
                                  "top_level": top, "type": self.wall_type(thickness, function), "start": start,
                                  "end": end}, Source(sheet=sheet, element=o.id, storey=storey,
                                                      note="wall across the gap of a door in no wall (answer)"))
        u, length = _unit(start, end)
        offset = round(_dot(_sub(t(o.center), start), u), 1)
        if not 0 <= offset <= length:
            self.skip(f"{category}s off their wall's length", "the opening lies beyond its wall's ends", [ref])
            return
        op: dict = {"op": category, "key": f"{sheet}-{o.id}-{storey}", "host_key": host_key, "type": name,
                    "offset_mm": offset}
        if category == "window":
            op["sill_mm"] = std.sill_mm
        else:
            flips = self.door_flips(p, o, start, end, thickness, inside)
            if flips is None:
                return
            op["flip_facing"], op["flip_hand"] = flips
        std.count += 1
        self.emit(op, Source(sheet=sheet, element=o.id, storey=storey))

    def door_flips(self, p: PlanRecognition, o: Opening, start: Point, end: Point, thickness: float,
                   inside) -> tuple[bool, bool] | None:
        """flip_facing/flip_hand from the drawn swing and hinge (see DEFAULT_OPENS_LEFT / DEFAULT_HINGE_AT_END)."""
        t = self.xf(p.sheet)
        if o.swing in (None, "none", "sliding") or o.hinge is None:
            return False, False
        u, _ = _unit(start, end)
        left = (-u[1], u[0])
        c, hinge = t(o.center), t(o.hinge)
        along = _dot(_sub(c, hinge), u)  # >0: the closed end lies towards the wall's end from the hinge
        if o.swing in ("left", "right"):
            opens_left = (along > 0) if o.swing == "left" else (along < 0)
        else:
            probe = thickness / 2 + SIDE_PROBE
            sides = [inside((c[0] + left[0] * probe, c[1] + left[1] * probe)),
                     inside((c[0] - left[0] * probe, c[1] - left[1] * probe))]
            if sides[0] == sides[1]:
                answer = self.need(f"door_swing:{p.sheet}:{o.id}", "door_swing",
                                   f"A door ({o.width_mm:g} mm, opening {o.swing}) on {p.sheet}: which side of its "
                                   "wall is inside cannot be told from the plan. Does it open to the wall's left or "
                                   "right (seen from the wall's start)?", [_ref(p.sheet, o.id)], ["left", "right"])
                if answer not in ("left", "right"):
                    return None
                opens_left = answer == "left"
            else:
                interior_left = sides[0]
                opens_left = interior_left if o.swing == "in" else not interior_left
        hinge_at_end = along < 0
        return opens_left != DEFAULT_OPENS_LEFT, hinge_at_end != DEFAULT_HINGE_AT_END

    # ---------------------------------------------------------------------------------------- storeys

    def inside_test(self, p: PlanRecognition):
        t = self.xf(p.sheet)
        polys = []
        if p.outline and len(p.outline) >= 3:
            polys.append(Polygon([t(q) for q in p.outline]))
        else:
            polys += [Polygon([t(q) for q in r.boundary]) for r in p.rooms if r.boundary and len(r.boundary) >= 3]
        return lambda q: any(poly.covers(SPoint(q)) for poly in polys)

    def wall_op(self, p: PlanRecognition, w: Wall, storey: str, level: str, top: str | None, roof: bool) -> None:
        sheet, t = p.sheet, self.xf(p.sheet)
        ref = _ref(sheet, w.id)
        op: dict = {"op": "wall", "key": f"{sheet}-{w.id}-{storey}", "level": level, "start": t(w.start), "end": t(w.end)}
        function = {"exterior": "exterior", "interior": "interior", "parapet": "exterior", "boundary": "exterior"}.get(w.kind)
        if function is None:
            answer = self.need(f"wall_function:{sheet}:{w.id}", "wall_function",
                               f"Is wall {w.id} ({w.thickness_mm:g} mm) on {sheet} an exterior or an interior wall?",
                               [ref], ["exterior", "interior"])
            if answer not in ("exterior", "interior"):
                return
            function = answer
        if w.kind == "parapet":
            height = w.height_mm or self.section_value("parapet_height_mm", "parapet_height", "the parapet height")
            if height is None:
                return
            op["height_mm"] = height
        elif w.kind == "boundary":
            height = w.height_mm or self.need_mm("boundary_wall_height", "boundary_wall_height",
                                                 "How high are the boundary walls (mm)? The drawings do not say.", [ref])
            if height is None:
                return
            op["height_mm"] = height
        elif top is None:
            if roof:
                self.need_mm("level:roof_structure", "level",
                             "Walls stand on the roof (a lift room or stair cover); how high is their top above "
                             "the roof (mm)? The sections do not settle it.", [ref])
            return
        else:
            op["top_level"] = top
        op["type"] = self.wall_type(w.thickness_mm, function)
        self.wall_keys[(sheet, w.id, storey)] = self.emit(op, Source(sheet=sheet, element=w.id, storey=storey))

    def column_op(self, sheet: str, c: Column, storey: str, level: str, top: str, t) -> None:
        if c.shape != "rectangular":
            self.skip("circular columns", "no circular concrete column type in the build plan yet", [_ref(sheet, c.id)])
            return
        b, h = sorted((c.width_mm, c.depth_mm))
        name, family = self.frame_type("column", b, h)
        rotation = (c.rotation_deg + (90 if c.width_mm > c.depth_mm else 0)) % 180
        op = {"op": "column", "key": f"{sheet}-{c.id}-{storey}", "level": level, "top_level": top,
              "point": t(c.center), "type": name, "family": family}
        if rotation:
            op["rotation_deg"] = rotation
        self.emit(op, Source(sheet=sheet, element=c.id, storey=storey))

    def storey(self, p: PlanRecognition, s: int) -> None:
        level, top = self.levels.by_storey.get(s), self.levels.above(s)
        if level is None:
            return
        sheet, t, tok = p.sheet, self.xf(p.sheet), str(s)
        lowest = s == min(p.storeys)
        if top is None:
            self.need_mm("level:roof", "level", f"Nothing above storey {s} is levelled: where does it end (roof level, mm)?",
                         [_ref(sheet, "sheet")])
        for w in p.walls:
            if w.kind == "boundary" and not lowest:
                continue  # drawn once, on the lowest storey of the plan
            self.wall_op(p, w, tok, level, top, roof=False)
        if top is not None:
            for c in p.columns:
                self.column_op(sheet, c, tok, level, top, t)
        walls = {w.id: w for w in p.walls}
        inside = self.inside_test(p)
        for o in p.openings:
            self.place_opening(p, o, tok, level, top, walls, inside)
        self.rooms(p, tok, "R" if tok == ROOF else tok, level)
        if top is not None:
            for st in p.stairs:
                self.stair(p, st, s, level, top)
        for pr in p.projections:
            self.projection(p, pr, tok, level, walls)

    def roof_plan(self, p: PlanRecognition) -> None:
        roof = self.levels.roof
        if roof is None:
            return
        sheet, t = p.sheet, self.xf(p.sheet)
        upper = self.levels.upper_roof
        for w in p.walls:
            self.wall_op(p, w, ROOF, roof, upper, roof=True)
        for c in p.columns:
            if upper is None:
                self.need_mm("level:roof_structure", "level",
                             "Columns stand on the roof (a lift room or stair cover); how high is their top above the "
                             "roof (mm)?", [_ref(sheet, c.id)])
                continue
            self.column_op(sheet, c, ROOF, roof, upper, t)
        walls = {w.id: w for w in p.walls}
        inside = self.inside_test(p)
        for o in p.openings:
            self.place_opening(p, o, ROOF, roof, upper, walls, inside)
        self.rooms(p, ROOF, "R", roof)
        for st in p.stairs:
            self.skip("stairs drawn on the roof plan", "the stair arriving at the roof is built with the storey below",
                      [_ref(sheet, st.id)])
        for pr in p.projections:
            self.projection(p, pr, ROOF, roof, walls)
        if p.falls:
            self.skip("roof falls (slope arrows)", "the roof slab is built flat; falls are not modelled",
                      [_ref(sheet, f.id) for f in p.falls])

    # ---------------------------------------------------------------------------------------- rooms

    def rooms(self, p: PlanRecognition, tok: str, label: str, level: str) -> None:
        sheet, t = p.sheet, self.xf(p.sheet)
        handles = {r.id: set(r.evidence.handles) for r in p.rooms}
        grouped: dict[str, list[Room]] = {}
        for f in p.findings:
            if f.topic == "rooms_share_space":
                fh = set(f.evidence.handles)
                members = [r for r in p.rooms if handles[r.id] & fh]
                if members:
                    grouped[f.id] = members
        in_group = {r.id for rs in grouped.values() for r in rs}
        placed: list[tuple[Room, str]] = []  # (room, name)
        for fid, members in grouped.items():
            names = " / ".join(dict.fromkeys(r.name for r in members))
            answer = self.need(f"rooms:{sheet}:{fid}", "rooms_share_space",
                               f"Rooms {names} on {sheet} share one space with no wall between them. One room, or "
                               "separate rooms divided by room separation lines (give the lines)?",
                               [_ref(sheet, r.id) for r in members],
                               [{"mode": "one_room", "name": names}, {"mode": "separate", "lines": []}, "skip"])
            mode = answer.get("mode") if isinstance(answer, dict) else answer
            if mode == "one_room":
                name = answer.get("name") if isinstance(answer, dict) and answer.get("name") else names
                placed.append((members[0], name))
            elif mode == "separate":
                lines = answer.get("lines") if isinstance(answer, dict) else None
                if not lines:
                    self.missing[f"rooms:{sheet}:{fid}"] = Missing(
                        id=f"rooms:{sheet}:{fid}", topic="rooms_share_space", elements=[_ref(sheet, r.id) for r in members],
                        text=f"Separate rooms {names} need their room separation lines ([[x, y], ...] in {sheet}'s "
                             "coordinates).")
                    continue
                for n, line in enumerate(lines, 1):
                    self.emit({"op": "room_separator", "key": f"{sheet}-{fid}.{n}-{tok}", "level": level,
                               "points": [t(tuple(q)) for q in line]},
                              Source(sheet=sheet, element=fid, storey=tok, note=f"separation line {n} (answer)"))
                placed += [(r, r.name) for r in members]
            elif mode == "skip":
                self.skip("rooms sharing one space", "the answer was to leave them out", [_ref(sheet, r.id) for r in members])
        for r in p.rooms:
            if r.id in in_group:
                continue
            if r.boundary is None:
                answer = self.need(f"room:{sheet}:{r.id}", "room_not_enclosed",
                                   f"\"{r.name}\" on {sheet} is not enclosed by walls, so Revit cannot place a room "
                                   "there. Skip it (a label only), or close it with room separation lines (give them)?",
                                   [_ref(sheet, r.id)], ["skip", {"mode": "separate", "lines": []}])
                mode = answer.get("mode") if isinstance(answer, dict) else answer
                if mode == "skip":
                    self.skip("room labels not enclosed by walls", "the answer was to leave them out", [_ref(sheet, r.id)])
                    continue
                if mode == "separate" and isinstance(answer, dict) and answer.get("lines"):
                    for n, line in enumerate(answer["lines"], 1):
                        self.emit({"op": "room_separator", "key": f"{sheet}-{r.id}.{n}-{tok}", "level": level,
                                   "points": [t(tuple(q)) for q in line]},
                                  Source(sheet=sheet, element=r.id, storey=tok, note=f"separation line {n} (answer)"))
                    placed.append((r, r.name))
                continue
            placed.append((r, r.name))
        counters: Counter[str | None] = Counter()
        for r, name in sorted(placed, key=lambda rn: (rn[0].unit or "", _id_order(rn[0].id))):
            counters[r.unit] += 1
            number = f"{label}-{r.unit}-{counters[r.unit]}" if r.unit else f"{label}-{counters[r.unit]}"
            op = {"op": "room", "key": f"{sheet}-{r.id}-{tok}", "level": level, "point": t(r.point), "name": name,
                  "number": number}
            if r.unit:
                op["flat"] = r.unit
            self.emit(op, Source(sheet=sheet, element=r.id, storey=tok))

    # ---------------------------------------------------------------------------------------- stairs

    def stair(self, p: PlanRecognition, st: Stair, s: int, level: str, top: str) -> None:
        sheet, t, tok = p.sheet, self.xf(p.sheet), str(s)
        ref = _ref(sheet, st.id)
        if st.kind not in ("dog_leg", "straight"):
            self.skip(f"{st.kind} stairs", "no layout rule for this stair kind yet", [ref])
            return
        width = st.width_mm or self.need_mm(f"stair_width:{sheet}:{st.id}", "stair_width",
                                            f"How wide are the flights of stair {st.id} on {sheet} (mm)?", [ref])
        tread = st.tread_mm or self.need_mm(f"stair_tread:{sheet}:{st.id}", "stair_tread",
                                            f"How deep is a tread of stair {st.id} on {sheet} (mm)?", [ref])
        height = self.levels.elevation[top] - self.levels.elevation[level]
        max_riser = next((r["max"] for r in rule_set(self.rules).get("rules", [])
                          if r.get("type") == "stair" and r.get("field") == "riser_mm"), None)
        drawn = sum(st.risers_per_flight)
        nbc = math.ceil(height / max_riser) if max_riser else None
        if drawn and (max_riser is None or height / drawn <= max_riser + 1e-6):
            risers = drawn
        else:
            risers = _num(self.need(f"stair_risers:{sheet}:{st.id}-{tok}", "stair_risers",
                                    f"Stair {st.id} on {sheet} climbs {height:g} mm; the plan draws {drawn or 'no'} "
                                    f"risers{f', more than {max_riser:g} mm each' if drawn else ''}. How many risers?",
                                    [ref], [n for n in (drawn, nbc) if n]))
            risers = int(risers) if risers else None
        outline = [t(q) for q in st.outline]
        if len(outline) != 4 or width is None or tread is None or risers is None:
            return
        xs, ys = [q[0] for q in outline], [q[1] for q in outline]
        box = (min(xs), min(ys), max(xs), max(ys))
        flights = len(st.risers_per_flight) or (2 if st.kind == "dog_leg" else 1)
        per = _split(risers, flights)
        goings = [(n - 1) * tread for n in per]
        span = (box[2] - box[0], box[3] - box[1])
        # The run axis: the side flights run along. For a dog-leg the other side holds both flights side by side.
        fits = [axis for axis in (0, 1) if span[axis] >= max(goings) + (width if st.kind == "dog_leg" else 0) - 1
                and span[1 - axis] >= (2 if st.kind == "dog_leg" else 1) * width - 1]
        if len(fits) != 1:
            answer = self.need(f"stair_axis:{sheet}:{st.id}", "stair_axis",
                               f"Stair {st.id} on {sheet}: do its flights run along x (east-west) or y (north-south)?",
                               [ref], ["x", "y"])
            if answer not in ("x", "y"):
                return
            axis = 0 if answer == "x" else 1
        else:
            axis = fits[0]
        ends = ("west", "east") if axis == 0 else ("south", "north")
        sides = ("south", "north") if axis == 0 else ("west", "east")
        lo, hi = box[axis], box[axis + 2]
        across_lo, across_hi = box[1 - axis], box[3 - axis]

        def pt(along: float, across: float) -> Point:
            return (round(along, 2), round(across, 2)) if axis == 0 else (round(across, 2), round(along, 2))

        tip = t(st.up_arrow) if st.up_arrow else None
        if st.kind == "straight":
            if tip is None:
                answer = self.need(f"stair_up:{sheet}:{st.id}", "stair_direction",
                                   f"Stair {st.id} on {sheet} has no up arrow: which end is its top?", [ref], list(ends))
                if answer not in ends:
                    return
                up_hi = answer == ends[1]
            else:
                up_hi = tip[axis] > (lo + hi) / 2
            mid = (across_lo + across_hi) / 2
            start = lo if up_hi else hi
            sign = 1 if up_hi else -1
            runs = [{"start": pt(start, mid), "end": pt(start + sign * goings[0], mid), "risers": per[0]}]
        else:
            landing = self.need(f"stair_landing:{sheet}:{st.id}", "stair_landing",
                                f"Stair {st.id} on {sheet} (dog-leg, flights along {'x' if axis == 0 else 'y'}): at "
                                f"which end is its mid landing, {ends[0]} or {ends[1]}?", [ref], list(ends))
            if landing not in ends:
                return
            landing_hi = landing == ends[1]
            if tip is None:
                first = self.need(f"stair_first_flight:{sheet}:{st.id}", "stair_first_flight",
                                  f"Stair {st.id} on {sheet} has no up arrow: on which side is its first flight, "
                                  f"{sides[0]} or {sides[1]}?", [ref], list(sides))
                if first not in sides:
                    return
                first_hi = first == sides[1]
            else:
                # The arrow head shows which way its flight climbs: towards the landing on the first flight, away
                # from it on the second.
                tip_side_hi = tip[1 - axis] > (across_lo + across_hi) / 2
                tip_near_landing = (tip[axis] > (lo + hi) / 2) == landing_hi
                first_hi = tip_side_hi if tip_near_landing else not tip_side_hi
            entry = lo if landing_hi else hi
            sign = 1 if landing_hi else -1
            c1 = across_hi - width / 2 if first_hi else across_lo + width / 2
            c2 = across_lo + width / 2 if first_hi else across_hi - width / 2
            runs = [{"start": pt(entry, c1), "end": pt(entry + sign * goings[0], c1), "risers": per[0]},
                    {"start": pt(entry + sign * goings[1], c2), "end": pt(entry, c2), "risers": per[1]}]
        key = self.emit({"op": "stair", "key": f"{sheet}-{st.id}-{tok}", "base_level": level, "top_level": top,
                         "width_mm": width, "tread_mm": tread, "runs": runs},
                        Source(sheet=sheet, element=st.id, storey=tok,
                               note=f"{risers} risers of {height / risers:.1f} mm"))
        self.emit({"op": "railing", "key": f"{sheet}-{st.id}.railing-{tok}", "stair_key": key},
                  Source(sheet=sheet, element=st.id, storey=tok, note="railing on the stair"))

    # ---------------------------------------------------------------------------------------- projections

    def projection(self, p: PlanRecognition, pr: Projection, tok: str, level: str, walls: dict[str, Wall]) -> None:
        sheet, t = p.sheet, self.xf(p.sheet)
        ref = _ref(sheet, pr.id)
        kind = pr.kind
        if kind == "unknown":
            kind = self.need(f"projection:{sheet}:{pr.id}", "projection_kind",
                             f"What is the projection {pr.id} on {sheet} (outside the walls, no door onto it)?", [ref],
                             ["balcony", "verandah", "chajja", "canopy", "ledge", "none"])
            if kind == "none":
                self.skip("projections answered as none", "the answer was that it is not built", [ref])
                return
            if kind not in ("balcony", "verandah", "chajja", "canopy", "ledge"):
                return
        points = [t(q) for q in pr.outline]
        if len(points) < 3:
            return
        key, source = f"{sheet}-{pr.id}-{tok}", Source(sheet=sheet, element=pr.id, storey=tok, note=kind)
        if kind in ("balcony", "verandah"):
            if self.slab_thickness is None:
                return
            self.emit({"op": "floor", "key": key, "level": level, "type": self.floor_type(self.slab_thickness, "slab"),
                       "points": points}, source)
            return
        thickness = self.need_mm(f"projection_thickness:{kind}", "projection_thickness",
                                 f"How thick is a {kind} slab (mm)? The drawings do not dimension it.", [ref])
        poly = Polygon(points)
        best = None
        for o in p.openings:
            wall = walls.get(o.wall_id) if o.wall_id else None
            if o.kind == "opening" or wall is None:
                continue
            d = poly.distance(SPoint(t(o.center)))
            if d <= wall.thickness_mm / 2 + SHADE_REACH and (best is None or d < best[0]):
                best = (d, o)
        if best is None:
            head = self.need_mm(f"projection_height:{sheet}:{pr.id}", "projection_height",
                                f"The {kind} {pr.id} on {sheet} shades no door or window: at what height is its top "
                                "above the floor (mm)?", [ref])
        else:
            head = self.head_of(sheet, best[1])
            if head is not None and thickness is not None:
                head += thickness
            source.note = f"{kind} over {best[1].id}"
        if head is None or thickness is None:
            return
        self.emit({"op": "floor", "key": key, "level": level, "type": self.floor_type(thickness, "projection"),
                   "points": points, "offset_mm": round(head, 1)}, source)

    # ---------------------------------------------------------------------------------------- slabs and shafts

    def ground_layers(self) -> tuple[list[TypeLayer] | None, list[str]]:
        layers = next((s.foundation_layers for s in self.b.sections if s.foundation_layers), [])
        if not layers:
            return None, []
        out = [TypeLayer(function="structure" if i == 0 else "substrate", thickness_mm=th) for i, (_, th) in enumerate(layers)]
        return out, [n for n, _ in layers]

    def slabs(self, storey_plan: dict[int, PlanRecognition], roof_plans: list[PlanRecognition]) -> None:
        if not storey_plan:
            return
        lowest = min(storey_plan)
        for s, p in sorted(storey_plan.items()):
            level = self.levels.by_storey.get(s)
            if level is None:
                continue
            outline, sheet, note = p.outline, p.sheet, "the plan's outline"
            if not outline:
                answer = self.need(f"slab_outline:storey-{s}", "slab_outline",
                                   f"Storey {s} ({p.sheet}) has no single building outline, so its floor slab has no "
                                   "edge. Use the outline of the storey above, or build no slab at this level?",
                                   [_ref(p.sheet, "outline")], ["storey_above", "none"])
                if answer == "none":
                    self.skip(f"floor slab at {level}", "the answer was no slab at this level", [_ref(p.sheet, "outline")])
                    continue
                above = storey_plan.get(s + 1)
                if answer != "storey_above" or above is None or not above.outline:
                    continue
                outline, sheet, note = above.outline, above.sheet, f"outline of storey {s + 1} (answer)"
            t = self.xf(sheet)
            if s == lowest:
                layers, names = self.ground_layers()
                if layers:
                    thickness = sum(layer.thickness_mm for layer in layers)
                    type_name = self.floor_type(thickness, "ground", layers, names)
                    self.skip("floor build-up materials", f"layers {', '.join(names)} keep the copied type's materials "
                              "(materials are not created)", [])
                else:
                    if self.slab_thickness is None:
                        continue
                    type_name = self.floor_type(self.slab_thickness, "slab")
            else:
                if self.slab_thickness is None:
                    continue
                type_name = self.floor_type(self.slab_thickness, "slab")
            self.slab_levels.add(level)
            self.emit({"op": "floor", "key": f"{p.sheet}-outline-{s}", "level": level, "type": type_name,
                       "points": [t(q) for q in outline]}, Source(sheet=sheet, element="outline", storey=str(s), note=note))
        roof = self.levels.roof
        if roof is None or self.slab_thickness is None:
            return
        source_plan = next((r for r in roof_plans if r.outline), None) or storey_plan[max(storey_plan)]
        if not source_plan.outline:
            self.need(f"slab_outline:roof", "slab_outline", "Neither the roof plan nor the top storey gives the roof "
                      "slab's outline. Where is its edge?", [_ref(source_plan.sheet, "outline")])
            return
        t = self.xf(source_plan.sheet)
        self.slab_levels.add(roof)
        self.emit({"op": "floor", "key": f"{source_plan.sheet}-outline-{ROOF}", "level": roof,
                   "type": self.floor_type(self.slab_thickness, "slab"), "points": [t(q) for q in source_plan.outline]},
                  Source(sheet=source_plan.sheet, element="outline", storey=ROOF, note="roof slab"))
        if self.levels.upper_roof:
            self.skip("slab over the roof structure (lift room, stair cover)", "no plan of it is drawn", [])

    def shaft_op(self, sheet: str, element: str, tok: str, level: str, points: list[Point], note: str) -> None:
        if level not in self.slab_levels or self.slab_thickness is None:
            return
        self.emit({"op": "shaft", "key": f"{sheet}-{element}-{tok}", "base_level": level, "top_level": level,
                   "base_offset_mm": -(self.slab_thickness + SHAFT_MARGIN), "top_offset_mm": SHAFT_MARGIN,
                   "points": points}, Source(sheet=sheet, element=element, storey=tok, note=note))

    def vertical_openings(self, storey_plan: dict[int, PlanRecognition]) -> None:
        """A shaft (lift, duct) drawn on a storey's plan cuts that storey's floor slab; a stair cuts the slab of the
        floor it climbs to."""
        for s, p in sorted(storey_plan.items()):
            t = self.xf(p.sheet)
            level = self.levels.by_storey.get(s)
            for sh in p.shafts:
                if level is not None and s != min(storey_plan):
                    self.shaft_op(p.sheet, sh.id, str(s), level, [t(q) for q in sh.outline], f"{sh.kind} shaft")
                elif level is not None:
                    self.skip(f"{sh.kind} shafts on the lowest floor", "the lowest floor's slab is not cut",
                              [_ref(p.sheet, sh.id)])
            above = self.levels.above(s)
            for st in p.stairs:
                if above is not None and st.kind in ("dog_leg", "straight"):
                    self.shaft_op(p.sheet, st.id + ".well", str(s), above, [t(q) for q in st.outline],
                                  f"stair opening in the slab at {above}")

    # ---------------------------------------------------------------------------------------- foundations, beams

    def foundations(self) -> None:
        for f in self.b.foundations:
            t = self.xf(f.sheet)
            level = self.levels.foundation
            for fo in f.footings:
                ref = _ref(f.sheet, fo.id)
                depth = fo.depth_mm or self.need_mm(f"footing_depth:{f.sheet}:{fo.id}", "footing_depth",
                                                    f"How deep is footing {fo.id} (mm)?", [ref])
                if level is None:
                    self.need_mm("level:foundation", "level", "At what elevation is the top of the footings (mm)?", [ref])
                    continue
                if depth is None:
                    continue
                hw, hl = fo.width_mm / 2, fo.length_mm / 2
                cx, cy = fo.center
                corners = [(cx - hw, cy - hl), (cx + hw, cy - hl), (cx + hw, cy + hl), (cx - hw, cy + hl)]
                self.emit({"op": "foundation_slab", "key": f"{f.sheet}-{fo.id}-{FOUNDATION}", "level": level,
                           "type": self.floor_type(depth, "foundation"), "points": [t(q) for q in corners]},
                          Source(sheet=f.sheet, element=fo.id, storey=FOUNDATION, note=fo.kind))
            lowest = min(self.levels.by_storey) if self.levels.by_storey else None
            if level is not None and lowest is not None:
                for c in f.plan_columns:
                    self.column_op(f.sheet, c, FOUNDATION, level, self.levels.by_storey[lowest], t)
            if f.rebar:
                self.skip("reinforcement", "rebar is listed in the standards, not built",
                          [_ref(f.sheet, r.member) for r in f.rebar][:1])
            self.notes += [f"rebar {r.member}: {r.text}" for r in f.rebar]

    def beams(self) -> None:
        with_layout = set()
        for bl in self.b.beam_layouts:
            t = self.xf(bl.sheet)
            if bl.storey is None:
                self.need(f"beam_layout_storey:{bl.sheet}", "beam_layout_storey",
                          f"Which floor's beams does {bl.sheet} show?", [_ref(bl.sheet, "sheet")])
                continue
            level = (self.levels.plinth if bl.storey == min(self.levels.by_storey or {0: 0}) and self.levels.plinth
                     else self.levels.by_storey.get(bl.storey))
            if level is None:
                continue
            with_layout.add(level)
            depths = {b.depth_mm for b in bl.beams if b.depth_mm}
            for bm in bl.beams:
                ref = _ref(bl.sheet, bm.id)
                width = bm.width_mm or self.need_mm(f"beam_width:{bl.sheet}", "beam_width",
                                                    f"How wide are the beams on {bl.sheet} (mm)?", [ref])
                depth = bm.depth_mm or self.need_mm(
                    f"beam_depth:{bl.sheet}", "beam_depth",
                    f"How deep are the beams on {bl.sheet} (mm)? The sheet gives no depth.", [ref],
                    sorted(depths | {s.beam_depth_mm for s in self.b.sections if s.beam_depth_mm}))
                if width is None or depth is None:
                    continue
                name, family = self.frame_type("beam", width, depth)
                self.emit({"op": "beam", "key": f"{bl.sheet}-{bm.id}-{bl.storey}", "level": level, "start": t(bm.start),
                           "end": t(bm.end), "type": name, "family": family},
                          Source(sheet=bl.sheet, element=bm.id, storey=str(bl.storey)))
        without = [n for n in list(self.levels.by_storey.values()) + [self.levels.roof]
                   if n and n not in with_layout and n != self.levels.by_storey.get(min(self.levels.by_storey or {0: 0}))]
        if without:
            self.skip(f"beams at {', '.join(without)}", "no beam layout is drawn for these floors (the sections give a "
                      "depth, not where the beams run)", [_ref(s.sheet, s.name) for s in self.b.sections])

    def report_unmodelled(self) -> None:
        for s in self.b.sections:
            if s.unexplained:
                self.skip("section dimensions not interpreted", "the section recogniser gave them no meaning",
                          [_ref(s.sheet, e.handles[0]) for e in s.unexplained if e.handles])
        for f in self.b.findings + [f for s in self.b.sections for f in s.findings]:
            if f.topic == "foundation_layer":
                self.skip("floor build-up layers without a thickness", f.text, [_ref(f.evidence.sheet, f.id)])


def _letters():
    letters = [c for c in "ABCDEFGHJKLMNPQRSTUVWXYZ"]  # I and O are left out, as on drawings
    yield from letters
    for a in letters:
        for b in letters:
            yield a + b


def _split(total: int, parts: int) -> list[int]:
    base, extra = divmod(total, parts)
    return [base + (1 if i < extra else 0) for i in range(parts)]


def _id_order(element_id: str) -> tuple:
    head, _, tail = element_id.rpartition("-")
    return (head, int(tail)) if tail.isdigit() else (element_id, 0)
