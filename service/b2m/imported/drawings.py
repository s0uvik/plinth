"""Drawing set for imported projects (PRD Step 9, stage 9 "Drawings", T10): the SOURCE sheet set reproduced in Revit.

    from b2m.imported.drawings import plan_sheet_set, make_import_drawing_set
    plan = plan_sheet_set(project, building, result, sheets)          # pure: what to make, what to ask, what cannot be
    out = make_import_drawing_set(revit, project, building, result, created, out_dir, record=...)

One Revit sheet per source sheet, in source page order, numbered by the source page label and named by its title, the
view at the source scale and placed where the source drawing sits on its page, then one PDF of the whole set and a
comparison with the source sheets (``drawings-comparison.json`` / ``.html``).

Everything comes from the import: the sheet records (role, storeys, title, scale, page), the framed source sheets
(border, drawing entities, DIMENSION entities), the recognised building (registrations, section cut paths and view
directions, level marks, rooms, openings) and the build plan with its op ``sources`` (which op is which recognised
element); the model element ids come from the run's ``created`` map (op key -> element id). Nothing is guessed: what
cannot be derived is a question (nothing is created until it is answered), and what the add-in cannot draw is listed
in ``not_reproduced`` with the reason, never faked.

Per source role:
- floor plan: a floor plan at the first storey the sheet covers (typical plan 1st-4th -> Floor 1), named like the
  source; roof plan at the level the roof plan's elements were built on.
- section: on the source cut path (from the plan section marks, T4), looking the way the marks' arrows point. A
  jogged path gives one section per cut segment (segments along the view direction are the jogs), placed side by side
  on the sheet, because the add-in cannot make a segmented section.
- elevation: an elevation looking at the face the title names (NORTH ELEVATION shows the north face, so it looks
  south); "FRONT" and other names do not say which face, so the user is asked.
- foundation / beam layout: a structural plan at the level the foundation / beams were built on, its view range
  cutting the columns and looking down past the deepest footing / beam bottom, showing only the model categories the
  source sheet holds (footings, columns, beams, grids).
- site plan: the source DWG imported on a drafting view at the source scale (not modelled, so not compared value by
  value).

Annotation from what the source sheet shows:
- plan dimensions: each source DIMENSION is classified: both ends on grid lines -> the same dimension between those
  grids (create_dimensions, grid references, at the source's dimension line); both ends on wall centrelines -> the
  same between the walls; ends on the outermost wall faces outside the building -> auto_dimension_plan "overall" on
  that side; ends on exterior wall faces / opening edges outside -> "exterior_openings" on that side; otherwise each
  end is matched to a model reference on the measured line (wall face, column / slab / footing / beam face, door or
  window Left/Right reference; structural sheets only structural ones) and drawn with create_dimensions; drawn at
  another scale -> a detail (not modelled); anything else -> not reproduced (listed with its value).
- room labels: rooms the source labels with a size get the tag plus the size note as written ("3500 X 4000") by
  label_rooms; the other rooms get a room tag.
- door/window marks: a tag per door / window when the source plan writes marks on that kind.
- sections: auto_dimension_section with the chains the source dimensions need ("levels", "slabs", "openings" for
  sills and heads, "beams" for modelled beam faces, "foundation" below ground level), on the side the source puts
  them, through the levels the source dimensions stop at; spot_level for every source level mark (PL, GL) on the
  Revit level at its height. Horizontal section dimensions are not drawn.

The title block: the paper is the smallest ISO A sheet that holds the source border (a 277 x 190 mm border is A4
landscape, 10 mm margins). A title block reported with a size (list_titleblocks sheet_width_mm/sheet_height_mm) must
match the paper or the border, and is used without a trial sheet; when no size is known the candidates are the title
blocks named with the paper ("A4"): exactly one is used and checked on the first sheet made (its frame must match),
several or none are a question listing them. The user's answer wins.
An existing title block is never edited.

Every view, sheet, viewport, dimension, tag, note and spot created is recorded (``record(key, id)``, keys DWG-...),
so removing the run removes them.
"""

from __future__ import annotations

import html
import json
import math
import re
from collections import Counter
from pathlib import Path
from typing import Any, Callable, Iterable, Literal

from pydantic import BaseModel, ConfigDict

from b2m.cad import annot, reader
from b2m.cad import sheet as cad_sheet
from b2m.cad.building import (BeamLayoutRecognition, FoundationRecognition, PlanRecognition, RecognisedBuilding,
                              Registration, SectionRecognition)
from b2m.cad.project import ImportProject, SheetRecord
from b2m.drawings import ELEVATION_DISTANCE
from b2m.imported.to_plan import ImportPlan
from b2m.plan import ColumnOp, FloorOp, FoundationSlabOp, GridOp, LevelOp, RoomOp, WallOp
from b2m.revit.client import RevitError

Point = tuple[float, float]
Rect = tuple[float, float, float, float]

# ISO 216 A series, portrait (width, height) in mm.
ISO_A = {"A0": (841.0, 1189.0), "A1": (594.0, 841.0), "A2": (420.0, 594.0), "A3": (297.0, 420.0), "A4": (210.0, 297.0),
         "A5": (148.0, 210.0)}
PAPER_TOL_MM = 2.0  # a title block frame matches a paper/border size within this (paper mm)
REF_TOL_MM = 2.0  # a dimension end lies on a grid / wall line within this (model mm), plus the registration residual
SECTION_BEYOND_MM = 1000.0  # far clip of sections / depth of elevations a little past the building
SCALE_TOL = 0.01  # a dimension whose written value / drawn length differs from 1 by more is drawn at a detail scale
VALUE_STEP = 0.5  # dimension values are compared rounded to this (mm); sources write 2687.5
DRAFTING_VIEWS = True  # create_view makes drafting views: the site plan is the source DWG on one
STRUCTURAL_SKIP = {1, 2, 4}  # wall centre lines, wall faces, door/window references (priorities in _Model3D.faces)
REACH_MM = 1500.0  # a dimension end may lie this far beyond the element whose face it measures to (planes extend)
NOT_DIMENSIONABLE = ("no model element has a face, centre line or reference there (create_dimensions measures to "
                     "grids, walls, wall faces, levels, door/window references and slab, column, footing or beam faces)")

# Model categories B2M builds; views of structural sheets hide those the source sheet does not show.
MODEL_CATEGORIES = ["OST_Walls", "OST_Doors", "OST_Windows", "OST_Rooms", "OST_Floors", "OST_Stairs", "OST_StairsRailing",
                    "OST_StructuralColumns", "OST_StructuralFraming", "OST_StructuralFoundation", "OST_ShaftOpening"]
COMPASS_FACE = {"NORTH": "south", "SOUTH": "north", "EAST": "west", "WEST": "east"}  # face shown -> view looks
DIRECTIONS = ["north", "south", "east", "west"]
LOOK = {"north": (0.0, 1.0), "south": (0.0, -1.0), "east": (1.0, 0.0), "west": (-1.0, 0.0)}
SIZE_TEXT = re.compile(r"^\s*\d+(?:\.\d+)?\s*[xX×]\s*\d+(?:\.\d+)?\s*$")

# Answer keys (project.inputs.values or the answers argument).
ANSWER_TITLE_BLOCK = "drawing:title_block"  # "Family: Type" (one for every paper) or {"A4": "Family: Type"}


def answer_elevation(file: str) -> str:
    return f"drawing:elevation:{file}"  # north | south | east | west: the way the elevation looks


def answer_sheet_number(file: str) -> str:
    return f"drawing:sheet_number:{file}"


def answer_scale(file: str) -> str:
    return f"drawing:scale:{file}"  # N of 1:N


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class DrawingQuestion(_Model):
    id: str  # the answer key
    topic: str
    text: str
    sheet: str | None = None
    options: list[str] = []


class NotReproduced(_Model):
    sheet: str
    what: str
    reason: str
    values: list[str] = []  # e.g. dimension values that will be missing


class Step(_Model):
    """One add-in call after the view exists. "$view" in params is the view id; element keys ("element_key",
    "room_key", "element_keys") are op keys, turned into element ids from the run's created map."""

    command: str
    params: dict[str, Any]
    purpose: str


class SourceDimension(_Model):
    handle: str
    value: float | None  # None when the text is not a number
    text: str
    axis: str
    kind: Literal["grids", "walls", "references", "overall", "exterior_openings", "levels", "slabs", "openings", "beams",
                  "foundation", "detail", "text", "local"]
    side: str | None = None
    note: str | None = None


class PlannedView(_Model):
    key: str  # record key suffix
    kind: Literal["floor_plan", "structural_plan", "section", "elevation", "drafting"]
    name: str
    create: dict[str, Any]  # create_view (or drafting) params
    after_create: list[Step] = []  # crop, visibility, CAD import
    steps: list[Step] = []  # annotation
    paper_center: Point | None = None  # viewport centre on the SOURCE paper (mm from its lower left corner)


class PlannedSheet(_Model):
    file: str
    role: str
    title: str | None
    page: str | None
    page_number: int | None
    number: str | None  # Revit sheet number (from the page label)
    scale: float | None
    paper: str | None  # "A4"
    paper_mm: tuple[float, float] | None  # as laid out (landscape when the border is)
    border_mm: tuple[float, float] | None  # the source border on paper
    margins_mm: tuple[float, float] | None
    views: list[PlannedView] = []
    reproduced: bool = True  # False: no Revit sheet is made (see not_reproduced)
    source_dimensions: list[SourceDimension] = []
    source_rooms: list[dict[str, Any]] = []  # {"name", "size"} as the source labels them
    source_marks: dict[str, dict[str, int]] = {}  # {"door": {"D1": 20}, "window": {...}}
    source_level_marks: list[str] = []


class SheetPlan(_Model):
    sheets: list[PlannedSheet] = []
    questions: list[DrawingQuestion] = []
    not_reproduced: list[NotReproduced] = []

    def blocking(self) -> bool:
        return bool(self.questions)


# ------------------------------------------------------------------------------------------------ small helpers

def _xf(reg: Registration | None, offset: Point = (0.0, 0.0)) -> Callable[[Point], Point]:
    """Sheet drawing coordinates -> model (building frame + model offset)."""
    r = math.radians(reg.rotation_deg if reg else 0.0)
    c, s = math.cos(r), math.sin(r)
    tx, ty = reg.translation if reg else (0.0, 0.0)
    return lambda p: (c * p[0] - s * p[1] + tx + offset[0], s * p[0] + c * p[1] + ty + offset[1])


def _bbox(points: Iterable[Point]) -> Rect | None:
    pts = list(points)
    if not pts:
        return None
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def _r(v: float, step: float = 0.1) -> float:
    return round(round(v / step) * step, 3)


def _value_key(v: float) -> float:
    return _r(v, VALUE_STEP)


def paper_for(border: tuple[float, float]) -> tuple[str, tuple[float, float]] | None:
    """The smallest ISO A sheet holding the border, laid out the border's way (landscape when wider)."""
    w, h = border
    for name in sorted(ISO_A, key=lambda n: ISO_A[n][0] * ISO_A[n][1]):
        a, b = ISO_A[name]
        size = (max(a, b), min(a, b)) if w >= h else (min(a, b), max(a, b))
        if size[0] + 1e-6 >= w and size[1] + 1e-6 >= h:
            return name, size
    return None


def _drawing_bbox(sheet: cad_sheet.Sheet) -> Rect | None:
    """The box around what is drawn on the sheet, within its inner frame (what shows on the page)."""
    pts = [p for e in annot.flatten(sheet.drawing_entities) for p in (e.points or [])]
    for e in annot.flatten(sheet.drawing_entities):
        if e.kind == "hatch" and e.boundaries:
            pts += [p for b in e.boundaries for p in b]
    box = _bbox(pts)
    if box is None or sheet.inner is None:
        return box
    i = sheet.inner
    box = (max(box[0], i[0]), max(box[1], i[1]), min(box[2], i[2]), min(box[3], i[3]))
    return box if box[0] < box[2] and box[1] < box[3] else None


def _texts_of(sheet: cad_sheet.Sheet | None, handles: list[str]) -> list[str]:
    if sheet is None:
        return []
    by = {}
    for e in sheet.cad.walk():
        by[e.handle] = e
    out = []
    for h in handles:
        e = by.get(h)
        if e is None:
            continue
        out += [annot.flat(x.text) for x in e.walk() if x.kind == "text" and x.text]
    return out


# ------------------------------------------------------------------------------------------------ the model side

class _Model3D:
    """What the sheet plan needs from the build plan: levels, grids, walls, extents, op keys by source element."""

    def __init__(self, result: ImportPlan, building: RecognisedBuilding, offset: Point):
        plan = result.plan
        if plan is None:
            raise ValueError("The import has no build plan yet (missing inputs); build it before its drawing set.")
        self.offset = offset
        self.ops = plan.ops
        self.sources = result.sources
        self.levels = {op.name: op.elevation_mm for op in plan.ops if isinstance(op, LevelOp)}
        self.grids = [op for op in plan.ops if isinstance(op, GridOp)]
        self.regs = {r.sheet: r for r in building.registrations}
        self.by_element: dict[tuple[str, str, str], str] = {}  # (sheet, element, storey) -> op key
        for key, src in result.sources.items():
            if src.sheet and src.element:
                self.by_element.setdefault((src.sheet, src.element, src.storey or ""), key)
        boundary = {(p.sheet, w.id) for p in building.plans for w in p.walls if w.kind == "boundary"}
        pts: list[Point] = []
        for op in plan.ops:
            src = result.sources.get(op.key)
            if isinstance(op, WallOp):
                if src and (src.sheet, src.element) in boundary:
                    continue
                pts += [op.start, op.end]
            elif isinstance(op, (FloorOp, FoundationSlabOp)):
                pts += list(op.points)
            elif isinstance(op, ColumnOp):
                pts.append(op.point)
        box = _bbox(pts) or (0.0, 0.0, 0.0, 0.0)
        self.extent = (box[0] + offset[0], box[1] + offset[1], box[2] + offset[0], box[3] + offset[1])
        self.by_key = plan.by_key()
        self.wall_thickness = {op.name: op.thickness_mm for op in plan.ops if op.op == "wall_type"}
        self.floor_thickness = {op.name: op.thickness_mm for op in plan.ops if op.op == "floor_type"}
        self.opening_types = {(op.category, op.name): op for op in plan.ops if op.op == "opening_type"}
        self.frames = {f.name: (f.b_mm, f.h_mm) for f in result.standards.columns + result.standards.beams}
        self._faces: dict[str, list[tuple[str, float, tuple[float, float], int, dict[str, Any]]]] = {}

    def _p(self, p: Point) -> Point:
        return p[0] + self.offset[0], p[1] + self.offset[1]

    def faces(self, level: str) -> list[tuple[str, float, tuple[float, float], int, dict[str, Any]]]:
        """Every planar reference a plan dimension on ``level`` can measure to: (axis, coordinate, extent along the
        other axis, priority, create_dimensions reference). Axis "h" is a plane of constant x (a horizontal
        dimension ends on it). Lower priority wins between references at the same place."""
        if level in self._faces:
            return self._faces[level]
        out: list[tuple[str, float, tuple[float, float], int, dict[str, Any]]] = []

        def add(axis, coord, lo, hi, prio, ref):
            out.append((axis, coord, (min(lo, hi), max(lo, hi)), prio, ref))

        def box_faces(key, xs, ys, prio, extra=None):
            for axis, values, other in (("h", xs, ys), ("v", ys, xs)):
                d = "x" if axis == "h" else "y"
                add(axis, min(values), min(other), max(other), prio,
                    {"mode": "element_face", "element_key": key, "direction": d, "end": "negative", **(extra or {})})
                add(axis, max(values), min(other), max(other), prio,
                    {"mode": "element_face", "element_key": key, "direction": d, "end": "positive", **(extra or {})})

        for g in self.grids:
            (ax, ay), (bx, by) = self._p(g.start), self._p(g.end)
            if abs(ax - bx) < 1e-3:
                add("h", ax, ay, by, 0, {"mode": "grid_curve", "element_key": g.key})
            elif abs(ay - by) < 1e-3:
                add("v", ay, ax, bx, 0, {"mode": "grid_curve", "element_key": g.key})
        for op in self.ops:
            lv = getattr(op, "level", None)
            if op.op == "wall" and lv == level:
                (ax, ay), (bx, by) = self._p(op.start), self._p(op.end)
                t = self.wall_thickness.get(op.type, 0.0) / 2
                ux, uy = _unit((ax, ay), (bx, by))
                left = (-uy, ux)  # left of start -> end
                if abs(ax - bx) < 1e-3:
                    axis, c, lo, hi, n = "h", ax, ay, by, (1.0, 0.0)
                elif abs(ay - by) < 1e-3:
                    axis, c, lo, hi, n = "v", ay, ax, bx, (0.0, 1.0)
                else:
                    continue
                add(axis, c, lo, hi, 1, {"mode": "wall_centerline", "element_key": op.key})
                if t:
                    plus = "left" if _dot(left, n) > 0 else "right"
                    minus = "right" if plus == "left" else "left"
                    add(axis, c + t, lo, hi, 2, {"mode": "wall_face", "element_key": op.key, "face": plus})
                    add(axis, c - t, lo, hi, 2, {"mode": "wall_face", "element_key": op.key, "face": minus})
            elif op.op == "column" and lv == level and op.type in self.frames:
                b, h = self.frames[op.type]
                w, d = (b, h) if round(op.rotation_deg) % 180 == 0 else (h, b)
                x, y = self._p(op.point)
                box_faces(op.key, [x - w / 2, x + w / 2], [y - d / 2, y + d / 2], 3)
            elif op.op in ("floor", "foundation_slab") and lv == level:
                pts = [self._p(p) for p in op.points]
                box_faces(op.key, [p[0] for p in pts], [p[1] for p in pts], 5)
            elif op.op == "beam" and lv == level and op.type in self.frames:
                (ax, ay), (bx, by) = self._p(op.start), self._p(op.end)
                half = self.frames[op.type][0] / 2
                if abs(ax - bx) < 1e-3:
                    box_faces(op.key, [ax - half, ax + half], [ay, by], 6)
                elif abs(ay - by) < 1e-3:
                    box_faces(op.key, [ax, bx], [ay - half, ay + half], 6)
            elif op.op in ("door", "window"):
                host = self.by_key.get(op.host_key)
                t = self.opening_types.get((op.op, op.type))
                if host is None or t is None or getattr(host, "level", None) != level:
                    continue
                a, b = self._p(host.start), self._p(host.end)
                u = _unit(a, b)
                c = (a[0] + u[0] * op.offset_mm, a[1] + u[1] * op.offset_mm)
                # The family's Right reference lies on its hand side: along the host wall, reversed when the
                # instance is hand-flipped (to confirm against Revit's HandOrientation on the live run).
                hand = (-u[0], -u[1]) if op.flip_hand else u
                for sign, name in ((1, "Right"), (-1, "Left")):
                    e = (c[0] + sign * hand[0] * t.width_mm / 2, c[1] + sign * hand[1] * t.width_mm / 2)
                    ref = {"mode": "family_instance", "element_key": op.key, "reference": name}
                    if abs(u[1]) < 1e-6:
                        add("h", e[0], c[1] - 300, c[1] + 300, 4, ref)
                    elif abs(u[0]) < 1e-6:
                        add("v", e[1], c[0] - 300, c[0] + 300, 4, ref)
        self._faces[level] = out
        return out

    def reference_at(self, p: Point, axis: str, level: str, tol: float,
                     structural: bool = False) -> dict[str, Any] | None:
        """The model reference a dimension end at ``p`` measures to (see ``faces``): on the measured line within
        ``tol``, preferring references whose element reaches past ``p``, then by priority. A reference plane
        extends past its element, so an element up to ``REACH_MM`` away still gives the right value.
        ``structural``: only grids, columns, slabs, footings and beams (what a structural plan shows)."""
        i, j = (0, 1) if axis == "h" else (1, 0)
        best = None
        for ax, coord, (lo, hi), prio, ref in self.faces(level):
            if ax != axis or abs(coord - p[i]) > tol or (structural and prio in STRUCTURAL_SKIP):
                continue
            outside = max(lo - p[j], p[j] - hi, 0.0)
            if outside > REACH_MM:
                continue
            rank = (outside > tol, prio, outside, abs(coord - p[i]))
            if best is None or rank < best[0]:
                best = (rank, ref)
        return dict(best[1]) if best else None

    def level_of(self, sheet: str, storey: str) -> str | None:
        """The level the sheet's elements of that storey were built on (most of them)."""
        found: Counter[str] = Counter()
        for op in self.ops:
            src = self.sources.get(op.key)
            if src is None or src.sheet != sheet or (src.storey or "") != storey:
                continue
            level = getattr(op, "level", None) or getattr(op, "base_level", None)
            if level:
                found[level] += 1
        return found.most_common(1)[0][0] if found else None

    def level_at(self, elevation: float, tol: float = 1.0) -> str | None:
        hits = [n for n, e in self.levels.items() if abs(e - elevation) <= tol]
        return hits[0] if len(hits) == 1 else None

    def grid_at(self, p: Point, along: str, tol: float) -> str | None:
        """A grid line through p perpendicular to the measured axis (along "h": a grid of constant x)."""
        best = None
        for g in self.grids:
            (ax, ay), (bx, by) = g.start, g.end
            ax, ay, bx, by = ax + self.offset[0], ay + self.offset[1], bx + self.offset[0], by + self.offset[1]
            if along == "h" and abs(ax - bx) < 1e-3:
                d, lo, hi, t = abs(ax - p[0]), min(ay, by), max(ay, by), p[1]
            elif along == "v" and abs(ay - by) < 1e-3:
                d, lo, hi, t = abs(ay - p[1]), min(ax, bx), max(ax, bx), p[0]
            else:
                continue
            if d > tol:
                continue
            outside = max(lo - t, t - hi, 0.0)
            if best is None or (outside, d) < best[0]:
                best = ((outside, d), g.key)
        return best[1] if best else None

    def wall_at(self, p: Point, along: str, level: str, tol: float) -> str | None:
        for op in self.ops:
            if not isinstance(op, WallOp) or op.level != level:
                continue
            (ax, ay), (bx, by) = op.start, op.end
            ax, ay, bx, by = ax + self.offset[0], ay + self.offset[1], bx + self.offset[0], by + self.offset[1]
            if along == "h" and abs(ax - bx) < 1e-3 and abs(ax - p[0]) <= tol and min(ay, by) - tol <= p[1] <= max(ay, by) + tol:
                return op.key
            if along == "v" and abs(ay - by) < 1e-3 and abs(ay - p[1]) <= tol and min(ax, bx) - tol <= p[0] <= max(ax, bx) + tol:
                return op.key
        return None

    def room_keys(self, sheet: str, storey: str) -> list[tuple[str, str]]:
        """(op key, recognised room id) of the sheet's rooms at that storey."""
        out = []
        for op in self.ops:
            if isinstance(op, RoomOp):
                src = self.sources.get(op.key)
                if src and src.sheet == sheet and (src.storey or "") == storey and src.element:
                    out.append((op.key, src.element))
        return out


# ------------------------------------------------------------------------------------------------ the sheet plan

def _ordered(project: ImportProject) -> list[SheetRecord]:
    return sorted(project.sheets, key=lambda s: (s.page_number is None, s.page_number or 0, s.file))


def load_sheets(project: ImportProject) -> dict[str, cad_sheet.Sheet]:
    """The framed source sheets, read again from the DXFs the import used."""
    by_source = {Path(src.path).name: src.dxf for src in project.sources}
    out = {}
    for rec in project.sheets:
        dxf = by_source.get(rec.file) or rec.source
        out[rec.file] = cad_sheet.detect(reader.read_dxf(Path(dxf)))
    return out


def plan_sheet_set(project: ImportProject, building: RecognisedBuilding, result: ImportPlan,
                   sheets: dict[str, cad_sheet.Sheet], answers: dict[str, Any] | None = None, prefix: str = "",
                   model_offset: Point = (0.0, 0.0)) -> SheetPlan:
    """What the drawing set will be: one planned sheet per source sheet in page order, its views and annotation steps,
    and the questions that must be answered first. Pure: no Revit."""
    answers = dict(project.inputs.values) | dict(answers or {})
    model = _Model3D(result, building, model_offset)
    out = SheetPlan()
    regs = {r.sheet: r for r in building.registrations}
    plans = {p.sheet: p for p in building.plans}
    sections = {s.sheet: s for s in building.sections}
    foundations = {f.sheet: f for f in building.foundations}
    layouts = {b.sheet: b for b in building.beam_layouts}
    numbers: Counter[str] = Counter()

    for rec in _ordered(project):
        sheet = sheets.get(rec.file)
        planned = _sheet_frame(rec, sheet, answers, out, prefix)
        numbers[planned.number or ""] += 1
        out.sheets.append(planned)
        if sheet is None:
            _skip(out, planned, "the sheet", "the source sheet could not be read")
            continue
        if planned.scale is None:
            continue  # asked above; its views are planned once the scale is known
        planned.source_level_marks = [m.text for m in annot.level_marks(
            annot.flatten(sheet.drawing_entities), annot.HIndex(annot.segments(annot.flatten(sheet.drawing_entities))))]
        if rec.role in ("floor_plan", "roof_plan"):
            _plan_view(planned, rec, sheet, plans.get(rec.file), regs.get(rec.file), model, out, prefix)
        elif rec.role == "section":
            sec = sections.get(rec.file)
            _section_views(planned, rec, sheet, sec, regs.get(rec.file), model, out, prefix)
        elif rec.role == "elevation":
            _elevation_view(planned, rec, sheet, building, model, answers, out, prefix)
        elif rec.role in ("foundation", "beam_layout"):
            rec_struct = foundations.get(rec.file) if rec.role == "foundation" else layouts.get(rec.file)
            _structural_view(planned, rec, sheet, rec_struct, regs.get(rec.file), model, out, prefix)
        elif rec.role == "site_plan":
            _site_view(planned, rec, sheet, out, prefix)
        else:
            _skip(out, planned, "the sheet", f"its role is not known ({rec.role}); answer the sheet-role question first")
    for number, n in numbers.items():
        if number and n > 1:
            out.questions.append(DrawingQuestion(
                id=f"drawing:sheet_numbers:{number}", topic="sheet_number", options=[],
                text=f"{n} source sheets carry page {number}; give each its own sheet number."))
    return out


def _skip(out: SheetPlan, planned: PlannedSheet, what: str, reason: str, values: list[str] | None = None) -> None:
    if what == "the sheet":
        planned.reproduced = False
        planned.views = []
    out.not_reproduced.append(NotReproduced(sheet=planned.file, what=what, reason=reason, values=values or []))


def _sheet_frame(rec: SheetRecord, sheet: cad_sheet.Sheet | None, answers: dict[str, Any], out: SheetPlan,
                 prefix: str) -> PlannedSheet:
    scale = rec.scale
    if scale is None and answers.get(answer_scale(rec.file)):
        scale = float(answers[answer_scale(rec.file)])
    number = str(rec.page_number) if rec.page_number is not None else answers.get(answer_sheet_number(rec.file))
    planned = PlannedSheet(file=rec.file, role=rec.role, title=rec.title, page=rec.page, page_number=rec.page_number,
                           number=f"{prefix}{number}" if number else None, scale=scale, paper=None, paper_mm=None,
                           border_mm=None, margins_mm=None)
    if number is None:
        out.questions.append(DrawingQuestion(
            id=answer_sheet_number(rec.file), topic="sheet_number", sheet=rec.file,
            text=f"{rec.file} has no page label; which sheet number should it get?"))
    if scale is None:
        out.questions.append(DrawingQuestion(
            id=answer_scale(rec.file), topic="scale", sheet=rec.file,
            text=f"{rec.file} gives no scale (\"SCALE 1:N\"); at which scale should its view be drawn (N of 1:N)?"))
    if sheet is not None and sheet.outer is not None and scale:
        x0, y0, x1, y1 = sheet.outer
        border = (_r((x1 - x0) / scale), _r((y1 - y0) / scale))
        planned.border_mm = border
        found = paper_for(border)
        named = str(answers.get(f"drawing:paper:{rec.file}") or "").upper()
        if named in ISO_A:
            a, b = ISO_A[named]
            found = (named, (max(a, b), min(a, b)) if border[0] >= border[1] else (min(a, b), max(a, b)))
        if found:
            planned.paper, planned.paper_mm = found
            planned.margins_mm = (_r((found[1][0] - border[0]) / 2), _r((found[1][1] - border[1]) / 2))
        else:
            out.questions.append(DrawingQuestion(
                id=f"drawing:paper:{rec.file}", topic="paper", sheet=rec.file,
                text=f"{rec.file}'s border ({border[0]:g} x {border[1]:g} mm) fits no ISO A sheet; which paper is it?"))
    elif sheet is not None and sheet.outer is None:
        out.questions.append(DrawingQuestion(
            id=f"drawing:paper:{rec.file}", topic="paper", sheet=rec.file,
            text=f"{rec.file} has no page border, so its paper size is not known; which paper is it?"))
    return planned


def _paper_center(planned: PlannedSheet, sheet: cad_sheet.Sheet, at: Point) -> Point | None:
    """A point in the sheet's drawing coordinates -> mm on the source paper from its lower left corner."""
    if sheet.outer is None or not planned.scale or planned.margins_mm is None:
        return None
    return (_r((at[0] - sheet.outer[0]) / planned.scale + planned.margins_mm[0]),
            _r((at[1] - sheet.outer[1]) / planned.scale + planned.margins_mm[1]))


def _view_name(prefix: str, rec: SheetRecord, suffix: str = "") -> str:
    return f"{prefix}{rec.title or Path(rec.file).stem}{suffix}"


# ---- plans

def _plan_view(planned: PlannedSheet, rec: SheetRecord, sheet: cad_sheet.Sheet, plan: PlanRecognition | None,
               reg: Registration | None, model: _Model3D, out: SheetPlan, prefix: str) -> None:
    storey = (str(min(rec.storeys)) if rec.storeys else "") if rec.role == "floor_plan" else "roof"
    level = model.level_of(rec.file, storey)
    if level is None:
        _skip(out, planned, "the sheet", f"no model element was built from this sheet at storey {storey or '-'}, so "
                                         "there is no level to draw its plan on")
        return
    if reg is None:
        _skip(out, planned, "the sheet", "the sheet is not registered to the building frame, so its view cannot be "
                                         "cropped and placed like the source")
        return
    view = _plan_view_for(planned, rec, sheet, reg, model, level, prefix)
    xf = _xf(reg, model.offset)
    dims = _plan_dimensions(planned, rec, sheet, plan, reg, model, level, xf, out)
    view.steps += dims
    view.steps += _room_steps(planned, rec, sheet, plan, model, storey, out)
    view.steps += _mark_steps(planned, plan)
    planned.views.append(view)


def _plan_view_for(planned, rec, sheet, reg, model, level, prefix, key="V1") -> PlannedView:
    box = _drawing_bbox(sheet)
    xf = _xf(reg, model.offset)
    corners = [xf((box[0], box[1])), xf((box[2], box[1])), xf((box[2], box[3])), xf((box[0], box[3]))]
    crop = _bbox(corners)
    params = {"viewType": "floor_plan", "level": level, "name": _view_name(prefix, rec), "scale": int(planned.scale),
              "cropMinX": _r(crop[0]), "cropMinY": _r(crop[1]), "cropMaxX": _r(crop[2]), "cropMaxY": _r(crop[3])}
    centre = ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
    return PlannedView(key=key, kind="floor_plan", name=params["name"], create=params,
                       paper_center=_paper_center(planned, sheet, centre))


def _outline_box(plan: PlanRecognition | None) -> Rect | None:
    if plan is None:
        return None
    faces = []
    for w in plan.walls:
        if w.kind == "boundary":
            continue
        t = w.thickness_mm / 2
        for (x, y) in (w.start, w.end):
            faces += [(x - t, y - t), (x + t, y + t)]
    return _bbox(faces)


def _measures(sheet: cad_sheet.Sheet) -> list[annot.Measure]:
    return annot.measures(annot.flatten(sheet.drawing_entities))


def _number(text: str) -> float | None:
    try:
        return float(text.replace(",", ""))
    except ValueError:
        return None


def _source_dim(m: annot.Measure, kind: str, side: str | None = None, note: str | None = None) -> SourceDimension:
    value = _number(m.text) if m.overridden else m.value
    return SourceDimension(handle=m.handle, value=value, text=m.text, axis=m.axis, kind=kind, side=side, note=note)


def _plan_dimensions(planned, rec, sheet, plan, reg, model, level, xf, out) -> list[Step]:
    tol = REF_TOL_MM + (reg.residual_mm if reg else 0.0)
    box = _outline_box(plan)
    faces_x, faces_y, edges_x, edges_y = _faces_and_edges(plan)
    auto: dict[str, dict[str, Any]] = {}  # side -> {"modes": set, "offset": float}
    steps: list[Step] = []
    missing: list[str] = []
    elevation = model.levels.get(level, 0.0)
    for m in _measures(sheet):
        if m.overridden and _number(m.text) is None:
            planned.source_dimensions.append(_source_dim(m, "text", note="text in place of the value"))
            missing.append(m.text)
            continue
        if m.scale is None or abs(m.scale - 1) > SCALE_TOL or m.axis == "o":
            planned.source_dimensions.append(_source_dim(m, "detail", note=(
                "drawn at a detail scale" if m.axis != "o" else "not along x or y")))
            missing.append(m.text)
            continue
        a, b, line = xf(m.p1), xf(m.p2), xf(m.line)
        refs = None
        g1, g2 = model.grid_at(a, m.axis, tol), model.grid_at(b, m.axis, tol)
        if g1 and g2 and g1 != g2:
            refs, kind = [("grid_curve", g1), ("grid_curve", g2)], "grids"
        else:
            w1, w2 = model.wall_at(a, m.axis, level, tol), model.wall_at(b, m.axis, level, tol)
            if w1 and w2 and w1 != w2:
                refs, kind = [("wall_centerline", w1), ("wall_centerline", w2)], "walls"
        if refs:
            if m.axis == "h":
                start, end = (a[0], line[1]), (b[0], line[1])
            else:
                start, end = (line[0], a[1]), (line[0], b[1])
            steps.append(Step(command="create_dimensions", purpose=f"source dimension {m.text} ({kind})", params={
                "view_id": "$view", "references": [{"mode": mode, "element_key": k} for mode, k in refs],
                "line": {"start": {"x": _r(start[0]), "y": _r(start[1]), "z": elevation},
                         "end": {"x": _r(end[0]), "y": _r(end[1]), "z": elevation}}}))
            planned.source_dimensions.append(_source_dim(m, kind))
            continue
        side, dist = _side(m, box)
        kind = "local"
        if side is not None:
            i = 0 if m.axis == "h" else 1
            lo, hi = sorted((m.p1[i], m.p2[i]))
            if box and abs(lo - box[i]) <= tol and abs(hi - box[i + 2]) <= tol:
                kind = "overall"
            else:
                stops = (faces_x | edges_x) if m.axis == "h" else (faces_y | edges_y)
                if _near(lo, stops, tol) and _near(hi, stops, tol):
                    kind = "exterior_openings"
        if kind == "local":
            r1 = model.reference_at(a, m.axis, level, tol, structural=plan is None)
            r2 = model.reference_at(b, m.axis, level, tol, structural=plan is None)
            if r1 and r2 and r1 != r2:
                if m.axis == "h":
                    start, end = (a[0], line[1]), (b[0], line[1])
                else:
                    start, end = (line[0], a[1]), (line[0], b[1])
                steps.append(Step(command="create_dimensions", purpose=f"source dimension {m.text} (references)",
                                  params={"view_id": "$view", "references": [r1, r2],
                                          "line": {"start": {"x": _r(start[0]), "y": _r(start[1]), "z": elevation},
                                                   "end": {"x": _r(end[0]), "y": _r(end[1]), "z": elevation}}}))
                planned.source_dimensions.append(_source_dim(m, "references", side))
                continue
            planned.source_dimensions.append(_source_dim(m, "local", side, NOT_DIMENSIONABLE))
            missing.append(m.text)
            continue
        entry = auto.setdefault(side, {"modes": set(), "offset": dist})
        entry["modes"].add(kind)
        entry["offset"] = min(entry["offset"], dist)
        planned.source_dimensions.append(_source_dim(m, kind, side))
    for side in sorted(auto):
        entry = auto[side]
        steps.append(Step(command="auto_dimension_plan", purpose=f"{', '.join(sorted(entry['modes']))} chains, {side}",
                          params={"view_id": "$view", "modes": sorted(entry["modes"]), "sides": [side],
                                  "offset_mm": _r(entry["offset"])}))
    if missing:
        _skip(out, planned, "dimensions", "these source dimensions are details drawn at another scale, text in "
                                          f"place of a value, or end where {NOT_DIMENSIONABLE}", missing)
    return steps


def _faces_and_edges(plan: PlanRecognition | None):
    fx, fy, ex, ey = set(), set(), set(), set()
    if plan is None:
        return fx, fy, ex, ey
    walls = {w.id: w for w in plan.walls}
    for w in plan.walls:
        if w.kind not in ("exterior", "unknown"):
            continue
        t = w.thickness_mm / 2
        if abs(w.start[0] - w.end[0]) < 1e-3:
            fx |= {w.start[0] - t, w.start[0] + t}
        elif abs(w.start[1] - w.end[1]) < 1e-3:
            fy |= {w.start[1] - t, w.start[1] + t}
    for o in plan.openings:
        w = walls.get(o.wall_id or "")
        if w is None or w.kind not in ("exterior", "unknown"):
            continue
        half = o.width_mm / 2
        if abs(w.start[1] - w.end[1]) < 1e-3:
            ex |= {o.center[0] - half, o.center[0] + half}
        elif abs(w.start[0] - w.end[0]) < 1e-3:
            ey |= {o.center[1] - half, o.center[1] + half}
    return fx, fy, ex, ey


def _near(v: float, values: set[float], tol: float) -> bool:
    return any(abs(v - x) <= tol for x in values)


def _side(m: annot.Measure, box: Rect | None) -> tuple[str | None, float]:
    """Which side of the building the dimension line is on, and how far out (model mm), or (None, 0) inside."""
    if box is None:
        return None, 0.0
    x, y = m.line
    if m.axis == "h":
        if y > box[3]:
            return "top", y - box[3]
        if y < box[1]:
            return "bottom", box[1] - y
    elif m.axis == "v":
        if x > box[2]:
            return "right", x - box[2]
        if x < box[0]:
            return "left", box[0] - x
    return None, 0.0


def _room_steps(planned, rec, sheet, plan, model, storey, out) -> list[Step]:
    if plan is None:
        return []
    rooms = {r.id: r for r in plan.rooms}
    sized, tagged = [], []
    for key, element in model.room_keys(rec.file, storey):
        room = rooms.get(element)
        if room is None:
            continue
        size = None
        if room.labelled_size_mm:
            written = [t for t in _texts_of(sheet, room.evidence.handles) if SIZE_TEXT.match(t)]
            size = written[0] if written else f"{room.labelled_size_mm[0]:g} X {room.labelled_size_mm[1]:g}"
            sized.append({"room_key": key, "text": size})
        else:
            tagged.append(key)
    for r in plan.rooms:
        written = [t for t in _texts_of(sheet, r.evidence.handles) if SIZE_TEXT.match(t)]
        size = (written[0] if written else f"{r.labelled_size_mm[0]:g} X {r.labelled_size_mm[1]:g}") \
            if r.labelled_size_mm else None
        planned.source_rooms.append({"name": r.name, "size": size})
    steps = []
    if sized:
        steps.append(Step(command="label_rooms", purpose="room labels NAME / W X L as the source writes them",
                          params={"view_id": "$view", "rooms": sized, "text_mode": "given"}))
    if tagged:
        steps.append(Step(command="tag_elements", purpose="room name tags", params={"view_id": "$view",
                                                                                 "element_keys": tagged}))
    labelled = len(model.room_keys(rec.file, storey))
    if labelled < len(plan.rooms):
        _skip(out, planned, "room labels", f"{len(plan.rooms)} room labels on the source, {labelled} rooms built from "
                                           "them (labels sharing one room, or rooms not built, carry one tag)")
    return steps


def _mark_steps(planned: PlannedSheet, plan: PlanRecognition | None) -> list[Step]:
    if plan is None:
        return []
    steps = []
    for kind, category in (("door", "OST_Doors"), ("window", "OST_Windows")):
        marks = Counter(o.mark_normalised or o.mark for o in plan.openings if o.kind == kind and (o.mark_normalised or o.mark))
        if marks:
            planned.source_marks[kind] = dict(sorted(marks.items()))
            steps.append(Step(command="tag_all_by_category", purpose=f"{kind} marks",
                              params={"view_id": "$view", "category": category, "limit": 500}))
    return steps


# ---- sections

def _section_views(planned, rec, sheet, sec: SectionRecognition | None, vreg: Registration | None, model: _Model3D,
                   out: SheetPlan, prefix: str) -> None:
    if sec is None:
        _skip(out, planned, "the sheet", "the section was not recognised")
        return
    path = sec.cut_path or (list(sec.cut_line) if sec.cut_line else None)
    if not path or sec.view_direction is None:
        _skip(out, planned, "the sheet", "the section's cut line or its view direction was not found on the plans "
                                         "(section marks)")
        return
    cut_reg = model.regs.get(sec.cut_sheet or "")
    if cut_reg is None or cut_reg.frame != "plan":
        _skip(out, planned, "the sheet", f"the cut line is on {sec.cut_sheet or 'no sheet'}, which is not registered to "
                                         "the building frame")
        return
    vd = sec.view_direction
    xf = _xf(cut_reg, model.offset)
    pts = [xf(p) for p in path]
    segments = [(a, b) for a, b in zip(pts, pts[1:]) if math.dist(a, b) > 1e-6]
    cuts = [(a, b) for a, b in segments if abs(_dot(_unit(a, b), vd)) < 1e-3]
    jogs = [(a, b) for a, b in segments if abs(abs(_dot(_unit(a, b), vd)) - 1) < 1e-3]
    if len(cuts) + len(jogs) != len(segments) or not cuts:
        _skip(out, planned, "the sheet", "the cut path has segments neither across nor along the view direction")
        return
    box = _drawing_bbox(sheet)
    bottom = top = None
    if vreg is not None and vreg.frame == "vertical" and box:
        bottom, top = box[1] + vreg.translation[1], box[3] + vreg.translation[1]
    else:
        levels = list(model.levels.values())
        bottom, top = min(levels) - SECTION_BEYOND_MM, max(levels) + SECTION_BEYOND_MM
        out.not_reproduced.append(NotReproduced(sheet=rec.file, what="section height", reason=(
            "the section sheet is not registered vertically; its height range is the levels plus a margin")))
    corners = [(model.extent[0], model.extent[1]), (model.extent[2], model.extent[3]),
               (model.extent[0], model.extent[3]), (model.extent[2], model.extent[1])]
    far = max(_dot(c, vd) for c in corners) + SECTION_BEYOND_MM
    right = (vd[1], -vd[0])  # the view's right on the sheet
    us = [(min(_dot(a, right), _dot(b, right)), max(_dot(a, right), _dot(b, right))) for a, b in cuts]
    total = (min(u[0] for u in us), max(u[1] for u in us))
    centre_src = ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2) if box else None
    if centre_src and bottom is not None and vreg is not None and vreg.frame == "vertical":
        centre_src = (centre_src[0], (bottom + top) / 2 - vreg.translation[1])
    centre_paper = _paper_center(planned, sheet, centre_src) if centre_src else None
    order = sorted(range(len(cuts)), key=lambda i: us[i][0])
    for n, i in enumerate(order, 1):
        a, b = cuts[i]
        normal = (-(b[1] - a[1]), b[0] - a[0])
        side = "left" if _dot(normal, vd) > 0 else "right"
        depth = far - _dot(a, vd)
        suffix = "" if len(cuts) == 1 else f" ({n} of {len(cuts)})"
        name = _view_name(prefix, rec, suffix)
        params = {"viewType": "section", "name": name, "scale": int(planned.scale), "startX": _r(a[0]),
                  "startY": _r(a[1]), "endX": _r(b[0]), "endY": _r(b[1]), "side": side, "depth": _r(depth),
                  "bottom": _r(bottom), "top": _r(top)}
        paper = None
        if centre_paper:
            mid = (us[i][0] + us[i][1]) / 2 - (total[0] + total[1]) / 2
            paper = (_r(centre_paper[0] + mid / planned.scale), centre_paper[1])
        planned.views.append(PlannedView(key=f"V{n}", kind="section", name=name, create=params, paper_center=paper))
    if len(cuts) > 1:
        out.not_reproduced.append(NotReproduced(sheet=rec.file, what="segmented section", reason=(
            f"the cut path is jogged ({len(cuts)} cut segments, {len(jogs)} jogs); the add-in cannot split a section "
            "into segments, so each cut segment is its own section, placed side by side (no jog lines)")))
    _section_annotation(planned, rec, sheet, sec, vreg, model, out)


def _dot(a: Point, b: Point) -> float:
    return a[0] * b[0] + a[1] * b[1]


def _unit(a: Point, b: Point) -> Point:
    d = (b[0] - a[0], b[1] - a[1])
    n = math.hypot(*d) or 1.0
    return d[0] / n, d[1] / n


def _section_annotation(planned, rec, sheet, sec, vreg, model: _Model3D, out) -> None:
    ms = _measures(sheet)
    box = _drawing_bbox(sheet)
    include, names, sides, missing = set(), set(), Counter(), []
    ground_mark = next((lv for lv in sec.levels if annot.flat(lv.name) == "GL"), None)
    ground = model.level_at(ground_mark.elevation_mm) if ground_mark else None
    stops = _section_stops(model, sec, model.levels.get(ground, 0.0) if ground else 0.0)
    for m in ms:
        if m.axis != "v" or m.scale is None or abs(m.scale - 1) > SCALE_TOL or vreg is None or vreg.frame != "vertical":
            planned.source_dimensions.append(_source_dim(m, "detail" if m.axis == "v" else "local", note=(
                "not a vertical dimension (no command dimensions horizontally in a section)" if m.axis != "v"
                else "drawn at a detail scale or not registered")))
            missing.append(m.text)
            continue
        lo, hi = m.lo + vreg.translation[1], m.hi + vreg.translation[1]
        l1, l2 = model.level_at(lo, REF_TOL_MM), model.level_at(hi, REF_TOL_MM)
        side = "left" if box and m.line[0] < (box[0] + box[2]) / 2 else "right"
        kind = next((k for k, at in stops.items() if _near(lo, at, REF_TOL_MM) and _near(hi, at, REF_TOL_MM)), None)
        if kind is None:
            planned.source_dimensions.append(_source_dim(m, "local", side, (
                "does not run between levels, slab faces, opening sills/heads, modelled beam faces or foundation "
                "faces")))
            missing.append(m.text)
            continue
        names |= {n for n in (l1, l2) if n}
        include.add(kind)
        sides[side] += 1
        planned.source_dimensions.append(_source_dim(m, kind, side))
    target = planned.views[0] if not sides or sides.most_common(1)[0][0] == "left" else planned.views[-1]
    if include:
        side = sides.most_common(1)[0][0]
        ordered = sorted(names, key=lambda n: model.levels[n])
        params = {"view_id": "$view", "include": sorted(include), "side": side, "level_names": ordered}
        if "foundation" in include and ground:
            params["ground_level"] = ground
        target.steps.append(Step(command="auto_dimension_section", purpose=f"{', '.join(sorted(include))} chain, {side}",
                                 params=params))
    marks = []
    for lv in sec.levels:
        if annot.level_kind(lv.name):
            revit = model.level_at(lv.elevation_mm)
            if revit:
                marks.append({"level_name": revit, "label": lv.name})
            else:
                out.not_reproduced.append(NotReproduced(sheet=rec.file, what=f"level mark {lv.name}", reason=(
                    f"no Revit level at {lv.elevation_mm:g} mm"), values=[lv.name]))
    if marks:
        target.steps.append(Step(command="spot_level", purpose="level marks as the source writes them",
                                 params={"view_id": "$view", "targets": marks, "side": sides.most_common(1)[0][0]
                                         if sides else "left"}))
    if missing:
        _skip(out, planned, "dimensions", "these source dimensions run horizontally, or between points the model has "
                                          "no level, slab, opening, beam or foundation face at (members the plan does "
                                          "not model, such as upper-floor beams and lintels), which "
                                          "auto_dimension_section cannot draw", missing)


def _section_stops(model: _Model3D, sec: SectionRecognition, ground: float) -> dict[str, set[float]]:
    """The heights each auto_dimension_section chain stops at, from the model (chains in the order they are tried)."""
    levels = set(model.levels.values())
    slab = sec.slab_thickness_mm
    out: dict[str, set[float]] = {"levels": levels}
    out["slabs"] = levels | ({e - slab for e in levels} if slab else set())
    openings: set[float] = set()
    beams: set[float] = set()
    foundation: set[float] = set()
    for op in model.ops:
        if op.op in ("door", "window"):
            host = model.by_key.get(op.host_key)
            t = model.opening_types.get((op.op, op.type))
            if host is None or t is None or host.level not in model.levels:
                continue
            sill = model.levels[host.level] + (getattr(op, "sill_mm", None) or 0.0)
            openings |= {sill, sill + t.height_mm} if op.op == "window" else {sill + t.height_mm}
        elif op.op == "beam" and op.level in model.levels and op.type in model.frames:
            e = model.levels[op.level]
            beams |= {e, e - model.frames[op.type][1]}
        elif op.op in ("foundation_slab", "floor") and op.level in model.levels:
            top = model.levels[op.level] + op.offset_mm
            if top <= ground + REF_TOL_MM:
                foundation |= {top, top - model.floor_thickness.get(op.type, 0.0)}
    out["openings"] = levels | openings
    out["beams"] = levels | beams
    below = {e for e in levels if e <= ground + REF_TOL_MM}
    out["foundation"] = below | foundation if foundation else set()
    return out


# ---- elevations

def _elevation_view(planned, rec, sheet, building: RecognisedBuilding, model: _Model3D, answers, out, prefix) -> None:
    name = (rec.name or "").upper()
    looks = next((COMPASS_FACE[w] for w in name.split() if w in COMPASS_FACE), None)
    answered = answers.get(answer_elevation(rec.file))
    if answered:
        looks = str(answered).lower()
    if looks not in LOOK:
        out.questions.append(DrawingQuestion(
            id=answer_elevation(rec.file), topic="elevation_direction", sheet=rec.file, options=DIRECTIONS,
            text=(f"\"{rec.title}\" does not say which face of the building it shows (the elevation drawing is not "
                  "recognised, and the site plan's road is not tied to the building). Which way does the elevation "
                  "look? north = it shows the south face, south = the north face, east = the west face, "
                  "west = the east face.")))
        return
    ground = min((n for n, e in model.levels.items() if e >= 0), key=lambda n: model.levels[n], default=None)
    x0, y0, x1, y1 = model.extent
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    marker = {"north": (cx, y0 - ELEVATION_DISTANCE), "south": (cx, y1 + ELEVATION_DISTANCE),
              "east": (x0 - ELEVATION_DISTANCE, cy), "west": (x1 + ELEVATION_DISTANCE, cy)}[looks]
    params = {"viewType": "elevation", "name": _view_name(prefix, rec), "scale": int(planned.scale),
              "level": ground, "x": _r(marker[0]), "y": _r(marker[1]), "direction": looks}
    box = _drawing_bbox(sheet)
    shift = _elevation_shift(sheet, building)
    across = (x1 - x0) if looks in ("north", "south") else (y1 - y0)
    width = max(across, (box[2] - box[0]) if box else across)
    if shift is not None and box:
        zmin, zmax = box[1] + shift, box[3] + shift
    else:
        zmin, zmax = min(model.levels.values()) - SECTION_BEYOND_MM, max(model.levels.values()) + SECTION_BEYOND_MM
    if looks in ("north", "south"):
        bounds = {"min_x_mm": _r(cx - width / 2), "max_x_mm": _r(cx + width / 2),
                  "min_y_mm": _r(y0 - SECTION_BEYOND_MM), "max_y_mm": _r(y1 + SECTION_BEYOND_MM)}
    else:
        bounds = {"min_x_mm": _r(x0 - SECTION_BEYOND_MM), "max_x_mm": _r(x1 + SECTION_BEYOND_MM),
                  "min_y_mm": _r(cy - width / 2), "max_y_mm": _r(cy + width / 2)}
    bounds |= {"min_z_mm": _r(zmin), "max_z_mm": _r(zmax)}
    view = PlannedView(key="V1", kind="elevation", name=params["name"], create=params,
                       after_create=[Step(command="set_view_crop", purpose="crop like the source drawing",
                                          params={"view_id": "$view", "enabled": True, "bounds": bounds})])
    if box:
        centre = ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
        view.paper_center = _paper_center(planned, sheet, centre)
    out.not_reproduced.append(NotReproduced(sheet=rec.file, what="elevation crop", reason=(
        "the elevation drawing is not registered horizontally: its view is centred on the building, as wide as the "
        "source drawing")))
    if shift is None:
        out.not_reproduced.append(NotReproduced(sheet=rec.file, what="elevation height", reason=(
            "no level mark on the elevation matches a section level, so its height range is the levels plus a margin")))
    marks = []
    for label in planned.source_level_marks:
        elevation = _section_level(building, label)
        revit = model.level_at(elevation) if elevation is not None else None
        if revit:
            marks.append({"level_name": revit, "label": label})
        else:
            out.not_reproduced.append(NotReproduced(sheet=rec.file, what=f"level mark {label}", reason=(
                "no section names this level, so its height is not known"), values=[label]))
    if marks:
        view.steps.append(Step(command="spot_level", purpose="level marks as the source writes them",
                               params={"view_id": "$view", "targets": marks}))
    for m in _measures(sheet):
        planned.source_dimensions.append(_source_dim(m, "local", note="elevation dimensions are not drawn"))
    if planned.source_dimensions:
        _skip(out, planned, "dimensions", "dimensions on elevations are not drawn by any add-in command",
              [d.text for d in planned.source_dimensions])
    planned.views.append(view)


def _section_level(building: RecognisedBuilding, label: str) -> float | None:
    found = {lv.elevation_mm for s in building.sections for lv in s.levels if annot.flat(lv.name) == annot.flat(label)}
    return found.pop() if len(found) == 1 else None


def _elevation_shift(sheet: cad_sheet.Sheet, building: RecognisedBuilding) -> float | None:
    """Drawing y -> elevation on an elevation sheet, from its level marks named like section levels."""
    ents = annot.flatten(sheet.drawing_entities)
    shifts = []
    for mark in annot.level_marks(ents, annot.HIndex(annot.segments(ents))):
        elevation = _section_level(building, mark.text)
        if elevation is not None and mark.candidates == 1:
            shifts.append(elevation - mark.y)
    if not shifts or max(shifts) - min(shifts) > REF_TOL_MM:
        return None
    return sum(shifts) / len(shifts)


# ---- foundation and beam layout

def _structural_view(planned, rec, sheet, rec_struct: FoundationRecognition | BeamLayoutRecognition | None,
                     reg: Registration | None, model: _Model3D, out, prefix) -> None:
    storey = "foundation" if rec.role == "foundation" else (str(rec.storeys[0]) if rec.storeys else "")
    level = model.level_of(rec.file, storey)
    if level is None:
        _skip(out, planned, "the sheet", "no model element was built from this sheet, so there is no level to draw it on")
        return
    if reg is None or rec_struct is None:
        _skip(out, planned, "the sheet", "the sheet is not recognised or not registered to the building frame")
        return
    view = _plan_view_for(planned, rec, sheet, reg, model, level, prefix)
    view.kind = "structural_plan"
    view.create["viewType"] = "structural_plan"
    view.create["viewRange"] = _structural_range(model, rec.file, level)
    shown = {"OST_StructuralColumns"}
    if isinstance(rec_struct, FoundationRecognition) and rec_struct.footings:
        shown.add("OST_StructuralFoundation")
    if isinstance(rec_struct, BeamLayoutRecognition) and rec_struct.beams:
        shown.add("OST_StructuralFraming")
    hidden = [c for c in MODEL_CATEGORIES if c not in shown]
    view.after_create.append(Step(command="set_category_visibility", purpose="show what the source sheet shows",
                                  params={"view_id": "$view", "categories": hidden, "hidden": True}))
    xf = _xf(reg, model.offset)
    view.steps += _plan_dimensions(planned, rec, sheet, None, reg, model, level, xf, out)
    planned.views.append(view)
    if isinstance(rec_struct, FoundationRecognition) and (rec_struct.column_sections or rec_struct.rebar):
        out.not_reproduced.append(NotReproduced(sheet=rec.file, what="details and notes", reason=(
            f"{len(rec_struct.column_sections)} column section details and {len(rec_struct.rebar)} reinforcement notes "
            "are drafting, not model; they are not drawn")))


def _structural_range(model: _Model3D, sheet: str, level: str) -> dict[str, float]:
    """A view range that cuts the columns above the level and looks down past the deepest member built from the
    sheet (footing or beam bottom), so the plan shows what the source sheet draws."""
    e = model.levels[level]
    deepest = 0.0
    for op in model.ops:
        src = model.sources.get(op.key)
        if src is None or src.sheet != sheet or getattr(op, "level", None) != level:
            continue
        if op.op in ("foundation_slab", "floor"):
            deepest = max(deepest, model.floor_thickness.get(op.type, 0.0) - op.offset_mm)
        elif op.op == "beam" and op.type in model.frames:
            deepest = max(deepest, model.frames[op.type][1])
    above = sorted(v for v in model.levels.values() if v > e + 1.0)
    cut = min(1200.0, (above[0] - e) / 2) if above else 1200.0
    below = -_r(deepest + 100.0)
    return {"top_offset_mm": _r(2 * cut), "cut_offset_mm": _r(cut), "bottom_offset_mm": below,
            "depth_offset_mm": below}


# ---- site plan

def _site_view(planned, rec, sheet, out, prefix) -> None:
    if not DRAFTING_VIEWS:
        _skip(out, planned, "the sheet", "the site plan is not modelled; it would be the source DWG on a drafting "
                                         "view, but create_view cannot make drafting views yet")
        return
    out.not_reproduced.append(NotReproduced(sheet=rec.file, what="site plan content", reason=(
        "the site plan is not modelled: its view is the source DWG imported on a drafting view, so its dimensions "
        "and texts are the source's own (not compared value by value)")))
    view = PlannedView(key="V1", kind="drafting", name=_view_name(prefix, rec),
                       create={"viewType": "drafting", "name": _view_name(prefix, rec), "scale": int(planned.scale)})
    view.after_create.append(Step(command="import_cad_to_view", purpose="the source site plan drawing",
                                  params={"view_id": "$view", "path": rec.source, "placement": "origin",
                                          "unit": "millimeter"}))
    box = _drawing_bbox(sheet)
    if box:
        view.paper_center = _paper_center(planned, sheet, ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2))
    planned.views.append(view)


# ------------------------------------------------------------------------------------------------ title blocks

def choose_title_blocks(rows: list[dict[str, Any]], papers: dict[str, tuple[float, float]], borders: dict[str, Any],
                        answers: dict[str, Any]) -> tuple[dict[str, str], list[DrawingQuestion]]:
    """Title block per paper: the user's answer, else one whose reported size matches the paper or border, else the
    single one named with the paper. Several or none -> a question."""
    chosen, questions = {}, []
    answer = answers.get(ANSWER_TITLE_BLOCK)
    names = [r.get("display_name") for r in rows if r.get("display_name")]
    for paper, size in papers.items():
        picked = answer.get(paper) if isinstance(answer, dict) else answer
        if picked:
            chosen[paper] = picked
            continue
        sized = [r for r in rows if r.get("sheet_width_mm") and r.get("sheet_height_mm")]
        fits = [r["display_name"] for r in sized if _fits((r["sheet_width_mm"], r["sheet_height_mm"]),
                                                           [size, *borders.get(paper, [])])]
        if not fits and not sized:
            token = re.compile(rf"(?<![A-Z0-9]){re.escape(paper)}(?![0-9])", re.I)
            fits = [n for n in names if token.search(n)]
        if len(fits) == 1:
            chosen[paper] = fits[0]
            continue
        what = "several title blocks could be" if fits else "no loaded title block is"
        questions.append(DrawingQuestion(
            id=ANSWER_TITLE_BLOCK, topic="title_block", options=fits or names,
            text=(f"The source sheets are {paper} ({size[0]:g} x {size[1]:g} mm) and {what} {paper}. Which title "
                  "block should the sheets use? (B2M does not edit title blocks; load a matching one, or pick one.)")))
    return chosen, questions


def _fits(size: tuple[float, float], targets: list[tuple[float, float]]) -> bool:
    w, h = sorted(size)
    return any(abs(w - min(t)) <= PAPER_TOL_MM and abs(h - max(t)) <= PAPER_TOL_MM for t in targets)


# ------------------------------------------------------------------------------------------------ making it in Revit

class _Exec:
    def __init__(self, revit, created: dict[str, int], record: Callable[[str, int], None]):
        self.revit = revit
        self.created = created
        self.record = record
        self.log: list[dict[str, Any]] = []
        self.failed: list[dict[str, Any]] = []

    def call(self, command: str, params: dict[str, Any], sheet: str | None = None, purpose: str = "",
             timeout: int = 300) -> dict[str, Any] | None:
        try:
            data = self.revit.call(command, params, timeout_seconds=timeout)
        except RevitError as e:
            self.log.append({"sheet": sheet, "command": command, "purpose": purpose, "ok": False, "error": e.error})
            self.failed.append({"sheet": sheet, "command": command, "purpose": purpose, "error": e.error})
            return None
        self.log.append({"sheet": sheet, "command": command, "purpose": purpose, "ok": True})
        return data or {}

    def keep(self, key: str, element_id: Any) -> None:
        if element_id is not None:
            self.record(key, int(element_id))

    def resolve(self, params: dict[str, Any], view_id: int) -> tuple[dict[str, Any], list[str]]:
        """$view and op keys -> ids; returns the params and the keys not in the model."""
        missing: list[str] = []

        def conv(v: Any) -> Any:
            if v == "$view":
                return view_id
            if isinstance(v, list):
                return [conv(x) for x in v]
            if isinstance(v, dict):
                out = {}
                for k, x in v.items():
                    if k in ("element_key", "room_key"):
                        if x in self.created:
                            out["element_id" if k == "element_key" else "room_id"] = self.created[x]
                        else:
                            missing.append(x)
                    else:
                        out[k] = conv(x)
                return out
            return v

        resolved = conv(params)
        if "element_keys" in resolved:
            keys = resolved.pop("element_keys")
            missing += [k for k in keys if k not in self.created]
            resolved["element_ids"] = [self.created[k] for k in keys if k in self.created]
        if "rooms" in resolved:
            resolved["rooms"] = [r for r in resolved["rooms"] if "room_id" in r]
        return resolved, missing


def make_import_drawing_set(revit, project: ImportProject, building: RecognisedBuilding, result: ImportPlan,
                            created: dict[str, int], out_dir: Path,
                            record: Callable[[str, int], None] = lambda key, element_id: None,
                            sheets: dict[str, cad_sheet.Sheet] | None = None, answers: dict[str, Any] | None = None,
                            prefix: str = "", model_offset: Point = (0.0, 0.0)) -> dict[str, Any]:
    """Reproduce the source sheet set in Revit after an imported project was built. ``created`` is the run's created
    map (op key -> element id); ``record`` is told every element id made here. Returns {"status": "questions"} with
    nothing created while anything must be asked, else the sheets, the PDF and the comparison with the source."""
    answers = dict(project.inputs.values) | dict(answers or {})
    sheets = sheets if sheets is not None else load_sheets(project)
    plan = plan_sheet_set(project, building, result, sheets, answers, prefix, model_offset)
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir / "drawings-sheet-plan.json").write_text(plan.model_dump_json(indent=1), encoding="utf-8")
    if plan.blocking():
        return {"status": "questions", "questions": [q.model_dump() for q in plan.questions],
                "not_reproduced": [n.model_dump() for n in plan.not_reproduced], "sheets": []}

    ex = _Exec(revit, created, record)
    todo = [s for s in plan.sheets if s.reproduced and s.views]
    papers = {s.paper: s.paper_mm for s in todo if s.paper}
    borders: dict[str, list] = {}
    for s in todo:
        if s.paper and s.border_mm:
            borders.setdefault(s.paper, []).append(s.border_mm)
    rows = (ex.call("list_titleblocks", {}, purpose="find title blocks") or {}).get("titleblocks") or []
    chosen, questions = choose_title_blocks(rows, papers, borders, answers)
    if questions:
        return {"status": "questions", "questions": [q.model_dump() for q in questions],
                "not_reproduced": [n.model_dump() for n in plan.not_reproduced], "sheets": []}

    # The first sheet of each paper checks its title block's size before anything else is made.
    made: dict[str, dict[str, Any]] = {}
    fit_mode: dict[str, str] = {}
    sizes = {r.get("display_name"): (r.get("sheet_width_mm"), r.get("sheet_height_mm")) for r in rows}
    for paper in papers:
        first = next(s for s in todo if s.paper == paper)
        w, h = sizes.get(chosen[paper]) or (None, None)
        if w and h:  # the title block reports its size: no trial sheet needed
            mode = _frame_mode({"width": w, "height": h}, first)
            if mode is None and not _user_chose(answers, paper):
                return {"status": "questions", "sheets": [],
                        "not_reproduced": [n.model_dump() for n in plan.not_reproduced],
                        "questions": [_size_question(chosen[paper], f"{w:g} x {h:g} mm", paper, first, rows)]}
            fit_mode[paper] = mode or "fit"  # the user chose a title block of another size: fit the views to it
            continue
        data = ex.call("create_sheet", {"sheet_number": first.number, "sheet_name": first.title or first.file,
                                        "title_block_name": chosen[paper]}, first.file, "sheet")
        if data is None:
            return _stopped(plan, ex, f"the {paper} sheet could not be made with title block {chosen[paper]}")
        frame = data.get("frame_sheet_mm") or {}
        mode = _frame_mode(frame, first)
        if mode is None and not _user_chose(answers, paper):
            ex.call("delete_element", {"elementIds": [data["sheet_id"]]}, first.file, "remove the trial sheet")
            size = f"{frame.get('width', 0):g} x {frame.get('height', 0):g} mm" if frame else "unknown"
            return {"status": "questions", "sheets": [], "not_reproduced": [n.model_dump() for n in plan.not_reproduced],
                    "questions": [_size_question(chosen[paper], size, paper, first, rows)]}
        fit_mode[paper] = mode or "fit"
        ex.keep(f"DWG-SHEET-{first.number}", data["sheet_id"])
        made[first.file] = data

    results = []
    for s in todo:
        data = made.get(s.file)
        if data is None:
            data = ex.call("create_sheet", {"sheet_number": s.number, "sheet_name": s.title or s.file,
                                            "title_block_name": chosen[s.paper]}, s.file, "sheet")
            if data is None:
                results.append({"file": s.file, "sheet_id": None, "views": []})
                continue
            ex.keep(f"DWG-SHEET-{s.number}", data["sheet_id"])
        results.append(_make_sheet(ex, s, data, fit_mode.get(s.paper, "paper")))

    sheet_ids = [r["sheet_id"] for r in results if r.get("sheet_id")]
    pdf = None
    if sheet_ids:
        exported = ex.call("export_pdf", {"output_folder": str(out_dir), "file_name": "drawings.pdf",
                                          "view_ids": sheet_ids, "overwrite": True}, purpose="export", timeout=600)
        pdf = exported.get("path") if exported else None
    comparison = compare(plan, results)
    write_comparison(comparison, out_dir)
    return {"status": "done" if not ex.failed else "done_with_issues", "sheets": [r.get("number") for r in results
                                                                                 if r.get("sheet_id")],
            "pdf": pdf, "comparison": comparison, "failed": ex.failed,
            "not_reproduced": [n.model_dump() for n in plan.not_reproduced],
            "comparison_json": str(out_dir / "drawings-comparison.json"),
            "comparison_html": str(out_dir / "drawings-comparison.html"), "log": ex.log}


def _size_question(name: str, size: str, paper: str, first: PlannedSheet, rows: list[dict[str, Any]]) -> dict[str, Any]:
    return DrawingQuestion(
        id=ANSWER_TITLE_BLOCK, topic="title_block", options=[r.get("display_name") for r in rows],
        text=(f"Title block {name} is {size}, not {paper} ({first.paper_mm[0]:g} x {first.paper_mm[1]:g} mm) or the "
              f"source border ({first.border_mm[0]:g} x {first.border_mm[1]:g} mm). Which title block should the "
              "sheets use?")).model_dump()


def _user_chose(answers: dict[str, Any], paper: str) -> bool:
    a = answers.get(ANSWER_TITLE_BLOCK)
    return bool(a.get(paper) if isinstance(a, dict) else a)


def _frame_mode(frame: dict[str, Any], s: PlannedSheet) -> str | None:
    if not frame:
        return None
    size = (frame.get("width", 0), frame.get("height", 0))
    if s.paper_mm and _fits(size, [s.paper_mm]):
        return "paper"
    if s.border_mm and _fits(size, [s.border_mm]):
        return "border"
    return None


def _stopped(plan: SheetPlan, ex: _Exec, why: str) -> dict[str, Any]:
    return {"status": "failed", "error": why, "failed": ex.failed, "sheets": [],
            "not_reproduced": [n.model_dump() for n in plan.not_reproduced], "log": ex.log}


def _sheet_point(frame: dict[str, Any], s: PlannedSheet, paper: Point | None, mode: str) -> tuple[float, float] | None:
    if paper is None or not frame:
        return None
    x, y = paper
    if mode == "border" and s.margins_mm:
        x, y = x - s.margins_mm[0], y - s.margins_mm[1]
    if (frame.get("width") or 0) < (frame.get("height") or 0) and s.paper_mm and s.paper_mm[0] > s.paper_mm[1]:
        return None  # the title block is portrait, the source landscape: let the add-in centre it
    return _r(frame["min_x"] + x), _r(frame["min_y"] + y)


# When the title block is not the source paper (the user chose another, e.g. A1 for an A4 set), the source page
# position and scale mean nothing on it: each view is fitted instead. The scale is the largest standard scale at
# which the view (plus room for its dimension chains and tags) fits its share of the drawing area, set before any
# annotation so tags and notes are placed at the final scale; the view is centred in its share. Title blocks keep
# their information strip on the right (landscape) or at the bottom (portrait); its size is not exposed by Revit's
# API, so the drawing area is the frame less TITLE_STRIP of that side.
STANDARD_SCALES = (1, 2, 5, 10, 20, 25, 50, 75, 100, 125, 150, 200, 250, 300, 400, 500, 750, 1000, 1250, 1500,
                   2000, 2500, 3000, 5000, 10000)
TITLE_STRIP = 0.18  # share of the frame's long side taken by the title block's information strip
FIT_FILL = 0.78  # share of the drawing area the view itself may use; the rest is for dimensions, tags and titles


def _extent_mm(v: PlannedView) -> tuple[float, float] | None:
    """The view's model extent (width, height) in mm, from what it was planned with; None when unknown."""
    c = v.create
    if v.kind in ("floor_plan", "structural_plan") and {"cropMinX", "cropMaxX", "cropMinY", "cropMaxY"} <= c.keys():
        return c["cropMaxX"] - c["cropMinX"], c["cropMaxY"] - c["cropMinY"]
    if v.kind == "section" and {"startX", "startY", "endX", "endY", "bottom", "top"} <= c.keys():
        return math.hypot(c["endX"] - c["startX"], c["endY"] - c["startY"]), c["top"] - c["bottom"]
    if v.kind == "elevation":
        for step in v.after_create:
            b = step.params.get("bounds") if step.command == "set_view_crop" else None
            if b:
                across = (b["max_x_mm"] - b["min_x_mm"]) if c.get("direction") in ("north", "south")                     else (b["max_y_mm"] - b["min_y_mm"])
                return across, b["max_z_mm"] - b["min_z_mm"]
    return None


def _drawing_area(frame: dict[str, Any]) -> tuple[float, float, float, float] | None:
    """(min_x, min_y, width, height) of the frame's drawing area, in sheet mm."""
    if not frame or not frame.get("width") or not frame.get("height"):
        return None
    x, y, w, h = frame["min_x"], frame["min_y"], frame["width"], frame["height"]
    if w >= h:
        return x, y, w * (1 - TITLE_STRIP), h
    return x, y + h * TITLE_STRIP, w, h * (1 - TITLE_STRIP)


def fit_scale(extent: tuple[float, float], slot: tuple[float, float]) -> int:
    """The largest standard scale (smallest N of 1:N) at which extent (model mm) fits FIT_FILL of slot (paper mm)."""
    need = max(extent[0] / (slot[0] * FIT_FILL), extent[1] / (slot[1] * FIT_FILL))
    return next((n for n in STANDARD_SCALES if n >= need), STANDARD_SCALES[-1])


def _fit_layout(frame: dict[str, Any], s: PlannedSheet) -> list[tuple[tuple[float, float], tuple[float, float]]]:
    """Per view: (slot centre, slot size) in sheet mm, the drawing area split side by side."""
    area = _drawing_area(frame)
    if area is None or not s.views:
        return []
    x, y, w, h = area
    n = len(s.views)
    return [((x + w * (i + 0.5) / n, y + h / 2), (w / n, h)) for i in range(n)]


def _make_sheet(ex: _Exec, s: PlannedSheet, sheet_data: dict[str, Any], mode: str) -> dict[str, Any]:
    out: dict[str, Any] = {"file": s.file, "number": sheet_data.get("sheet_number", s.number),
                           "name": sheet_data.get("sheet_name", s.title), "sheet_id": sheet_data["sheet_id"],
                           "views": []}
    frame = sheet_data.get("frame_sheet_mm") or {}
    slots = _fit_layout(frame, s) if mode == "fit" else []
    for i, v in enumerate(s.views):
        if slots and v.kind != "drafting":
            extent = _extent_mm(v)
            if extent:  # fitted views get their scale before anything is drawn in them
                v.create = {**v.create, "scale": fit_scale(extent, slots[i][1])}
        view = ex.call("create_view", v.create, s.file, f"view {v.name}")
        entry: dict[str, Any] = {"name": v.name, "kind": v.kind, "view_id": None, "scale": None, "dimensions": [],
                                 "labels": [], "tags": {}, "spots": [], "skipped": []}
        out["views"].append(entry)
        if view is None:
            continue
        vid = view["viewId"]
        entry["view_id"], entry["scale"] = vid, view.get("scale", v.create.get("scale"))
        ex.keep(f"DWG-VIEW-{s.number}-{v.key}", vid)
        ex.keep(f"DWG-VIEW-{s.number}-{v.key}-MARKER", view.get("elevationMarkerId"))
        for step in v.after_create + v.steps:
            params, missing = ex.resolve(step.params, vid)
            if missing:
                entry["skipped"].append({"step": step.purpose, "reason": "not in the model: " + ", ".join(missing)})
                if step.command == "create_dimensions" or not (params.get("element_ids") or params.get("rooms")):
                    continue
            data = ex.call(step.command, params, s.file, step.purpose)
            if data is None:
                continue
            _keep_step(ex, s, step, data, entry)
        at = slots[i][0] if slots else _sheet_point(frame, s, v.paper_center, mode)
        place = {"view_id": vid, "sheet_id": sheet_data["sheet_id"]}
        if at:
            place |= {"x_mm": at[0], "y_mm": at[1]}
        vp = ex.call("place_view_on_sheet", place, s.file, "viewport")
        if vp:
            ex.keep(f"DWG-VP-{vp['viewport_id']}", vp["viewport_id"])
    return out


def _keep_step(ex: _Exec, s: PlannedSheet, step: Step, data: dict[str, Any], entry: dict[str, Any]) -> None:
    c = step.command
    if c == "create_dimensions":
        ex.keep(f"DWG-DIM-{data.get('dimension_id')}", data.get("dimension_id"))
        if data.get("value_mm") is not None:
            entry["dimensions"].append(data["value_mm"])
    elif c in ("auto_dimension_plan", "auto_dimension_section"):
        for d in data.get("dimensions") or []:
            ex.keep(f"DWG-DIM-{d.get('dimension_id')}", d.get("dimension_id"))
            entry["dimensions"] += list(d.get("values_mm") or [])
        entry["skipped"] += [{"step": step.purpose, "reason": x.get("reason")} for x in data.get("skipped") or []]
    elif c == "label_rooms":
        for r in data.get("rooms") or []:
            if not r.get("tag_reused"):
                ex.keep(f"DWG-TAG-{r.get('tag_id')}", r.get("tag_id"))
            ex.keep(f"DWG-NOTE-{r.get('text_note_id')}", r.get("text_note_id"))
            entry["labels"].append({"name": r.get("room_name"), "size": r.get("text")})
    elif c in ("tag_elements", "tag_all_by_category"):
        tags = data.get("tags") or []
        for t in tags:
            ex.keep(f"DWG-TAG-{t.get('tag_id')}", t.get("tag_id"))
        what = step.params.get("category", "rooms") if c == "tag_all_by_category" else "rooms"
        entry["tags"][what] = entry["tags"].get(what, 0) + len(tags)
    elif c == "spot_level":
        for p in data.get("placed") or []:
            ex.keep(f"DWG-SPOT-{p.get('element_id')}", p.get("element_id"))
            entry["spots"].append(p.get("label") or p.get("text"))
        if not data.get("placed"):
            for i in data.get("element_ids") or []:
                ex.keep(f"DWG-SPOT-{i}", i)
    elif c == "import_cad_to_view":
        ex.keep(f"DWG-CAD-{data.get('instance_id')}", data.get("instance_id"))


# ------------------------------------------------------------------------------------------------ comparison

def _multiset_diff(source: list[float], made: list[float]) -> tuple[list[float], list[float]]:
    a, b = Counter(_value_key(v) for v in source), Counter(_value_key(v) for v in made)
    missing = sorted((a - b).elements())
    extra = sorted((b - a).elements())
    return missing, extra


def compare(plan: SheetPlan, results: list[dict[str, Any]]) -> dict[str, Any]:
    """The reproduced set against the source: sheet list (order, page, title, scale) and, per sheet, dimension values
    (as multisets), room labels and door/window mark counts."""
    made = {r["file"]: r for r in results}
    sheets, problems = [], 0
    for s in plan.sheets:
        r = made.get(s.file)
        views = (r or {}).get("views") or []
        scales = sorted({v["scale"] for v in views if v.get("scale")})
        source_values = [d.value for d in s.source_dimensions if d.value is not None]
        source_texts = [d.text for d in s.source_dimensions if d.value is None]
        made_values = [float(x) for v in views for x in v.get("dimensions") or []]
        missing, extra = _multiset_diff(source_values, made_values)
        rooms_src = Counter((x["name"], x["size"]) for x in s.source_rooms if x["size"])
        rooms_made = Counter((x["name"], x["size"]) for v in views for x in v.get("labels") or [])
        tags = Counter()
        for v in views:
            tags.update(v.get("tags") or {})
        marks = {kind: {"source": sum(counts.values()), "tagged": tags.get(cat, 0)}
                 for kind, counts, cat in ((k, c, "OST_Doors" if k == "door" else "OST_Windows")
                                           for k, c in s.source_marks.items())}
        spots = [x for v in views for x in v.get("spots") or []]
        entry = {
            "file": s.file, "page": s.page, "page_number": s.page_number,
            "source": {"number": s.number, "title": s.title, "scale": s.scale, "paper": s.paper},
            "made": None if r is None or not r.get("sheet_id") else {
                "number": r.get("number"), "title": r.get("name"), "scales": scales},
            "dimensions": {"source": sorted(_value_key(v) for v in source_values), "source_text": source_texts,
                           "made": sorted(_value_key(v) for v in made_values), "missing": missing, "extra": extra},
            "room_labels": {"source": sum(rooms_src.values()), "made": sum(rooms_made.values()),
                            "missing": sorted(f"{n} / {z}" for n, z in (rooms_src - rooms_made).elements()),
                            "extra": sorted(f"{n} / {z}" for n, z in (rooms_made - rooms_src).elements()),
                            "name_tags": tags.get("rooms", 0)},
            "marks": marks,
            "level_marks": {"source": s.source_level_marks, "made": spots},
            "skipped": [x for v in views for x in v.get("skipped") or []],
        }
        diffs = []
        if entry["made"] is None:
            diffs.append("sheet not reproduced")
        else:
            if entry["made"]["number"] != s.number or entry["made"]["title"] != s.title:
                diffs.append("number or title differs")
            if s.scale and scales != [int(s.scale)]:
                diffs.append("scale differs")
        if missing or extra or source_texts:
            diffs.append("dimension values differ")
        if entry["room_labels"]["missing"] or entry["room_labels"]["extra"]:
            diffs.append("room labels differ")
        if any(m["source"] != m["tagged"] for m in marks.values()):
            diffs.append("mark counts differ")
        if sorted(s.source_level_marks) != sorted(spots):
            diffs.append("level marks differ")
        entry["differences"] = diffs
        problems += bool(diffs)
        sheets.append(entry)
    order_source = [s.number for s in plan.sheets]
    order_made = [made[s.file].get("number") for s in plan.sheets if s.file in made and made[s.file].get("sheet_id")]
    return {"sheets": sheets, "order": {"source": order_source, "made": order_made,
                                        "same": [n for n in order_source if n in order_made] == order_made},
            "sheets_with_differences": problems,
            "not_reproduced": [n.model_dump() for n in plan.not_reproduced]}


def write_comparison(comparison: dict[str, Any], out_dir: Path) -> None:
    (out_dir / "drawings-comparison.json").write_text(json.dumps(comparison, indent=1), encoding="utf-8")
    (out_dir / "drawings-comparison.html").write_text(comparison_html(comparison), encoding="utf-8")


def comparison_html(c: dict[str, Any]) -> str:
    e = html.escape
    rows = []
    for s in c["sheets"]:
        made = s["made"] or {}
        d = s["dimensions"]
        rows.append(
            "<tr class='{}'><td>{}</td><td>{}</td><td>{}</td><td>1:{}</td><td>{}</td><td>{}</td><td>{}</td>"
            "<td>{}</td><td>{}</td><td>{}</td><td>{}</td></tr>".format(
                "diff" if s["differences"] else "ok", e(str(s["page"] or "")), e(str(s["source"]["number"] or "")),
                e(s["source"]["title"] or s["file"]), e(f"{s['source']['scale'] or '?':g}" if s["source"]["scale"] else "?"),
                e(str(made.get("number") or "not made")), e(", ".join(f"1:{x}" for x in made.get("scales") or [])),
                e(_vals(d["missing"]) + (" " + ", ".join(d["source_text"]) if d["source_text"] else "")),
                e(_vals(d["extra"])),
                e(f"{s['room_labels']['made']}/{s['room_labels']['source']}"),
                e("; ".join(f"{k} {m['tagged']}/{m['source']}" for k, m in s["marks"].items())),
                e("; ".join(s["differences"]) or "same")))
    gaps = "".join(f"<li><b>{e(n['sheet'])}</b> {e(n['what'])}: {e(n['reason'])}"
                   + (f" <i>({e(', '.join(n['values']))})</i>" if n["values"] else "") + "</li>"
                   for n in c["not_reproduced"])
    return f"""<!doctype html><html><head><meta charset="utf-8"><title>Drawing set comparison</title>
<style>body{{font:14px system-ui,sans-serif;margin:24px;color:#222}}table{{border-collapse:collapse;width:100%}}
td,th{{border:1px solid #ccc;padding:4px 6px;text-align:left;vertical-align:top}}th{{background:#f3f3f3}}
tr.diff td:last-child{{color:#a40}}tr.ok td:last-child{{color:#070}}</style></head><body>
<h1>Drawing set compared with the source</h1>
<p>Sheet order: source {e(', '.join(str(x) for x in c['order']['source']))}; made {e(', '.join(str(x) for x in c['order']['made']))}.
Sheets with differences: {c['sheets_with_differences']} of {len(c['sheets'])}.</p>
<table><tr><th>Page</th><th>Number</th><th>Title</th><th>Scale</th><th>Made</th><th>Viewport scale</th>
<th>Dimensions missing</th><th>Dimensions extra</th><th>Size labels made/source</th><th>Marks tagged/source</th>
<th>Differences</th></tr>{''.join(rows)}</table>
<h2>Not reproduced</h2><ul>{gaps}</ul></body></html>"""


def _vals(values: list[float]) -> str:
    return ", ".join(f"{v:g}" for v in values)
