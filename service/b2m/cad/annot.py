"""Drawing primitives shared by section, foundation and beam-layout recognition and registration (PRD Step 9, T4).

Everything here works on the normalised entities of a sheet (``reader.CadEntity``) and knows no layer names, sizes
or counts of any particular drawing set. Only drafting conventions are used: a dimension measures between its two
extension-line origins; a note's leader starts at the note and ends on what it describes; a level mark ("GL", "PL")
sits on or beside its level line; an arrowhead is a small solid triangle; centre lines are drawn with a centre
linetype or on a centre/grid/axis layer.

``TOL`` is the drafting tolerance: two drawn points closer than this are the same point.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from typing import Iterable, Literal

from b2m.cad.reader import CadEntity

Point = tuple[float, float]
Rect = tuple[float, float, float, float]  # xmin, ymin, xmax, ymax
TOL = 1.0  # mm
CHAR_WIDTH = 0.8  # a character is about this many text heights wide (upper bound for the usual CAD fonts)
LINE_SPACING = 1.67  # MTEXT line pitch in text heights (AutoCAD's default)
LEADER_REACH = 2.5  # a leader starts within this many text heights of its note
LEADER_MAX_SEGMENTS = 12  # a leader (with a fan of branches) has a handful of segments; more is drawing geometry
CENTRE_WORDS = re.compile(r"CENT(?:ER|RE)|GRID|AXIS|DASHDOT|CHAIN", re.I)


# ---------------------------------------------------------------- entities and segments

def flatten(entities: Iterable[CadEntity]) -> list[CadEntity]:
    """Every entity, block children included, without the block references themselves."""
    return [e for top in entities for e in top.walk() if e.kind != "insert"]


def name_of(sheet) -> str:
    """A sheet's file name, the name findings and evidence use."""
    return sheet.cad.source.replace("\\", "/").rsplit("/", 1)[-1]


@dataclass(frozen=True)
class Seg:
    a: Point
    b: Point
    handle: str
    layer: str
    linetype: str | None
    color: int | None
    dxftype: str

    @property
    def length(self) -> float:
        return math.dist(self.a, self.b)

    def horizontal(self, tol: float = TOL) -> bool:
        return abs(self.a[1] - self.b[1]) <= tol and abs(self.a[0] - self.b[0]) > tol

    def vertical(self, tol: float = TOL) -> bool:
        return abs(self.a[0] - self.b[0]) <= tol and abs(self.a[1] - self.b[1]) > tol

    @property
    def x0(self) -> float:
        return min(self.a[0], self.b[0])

    @property
    def x1(self) -> float:
        return max(self.a[0], self.b[0])

    @property
    def y0(self) -> float:
        return min(self.a[1], self.b[1])

    @property
    def y1(self) -> float:
        return max(self.a[1], self.b[1])

    def style(self) -> tuple:
        return (self.layer, self.linetype, self.color)

    def centre_styled(self) -> bool:
        return bool(CENTRE_WORDS.search(self.layer or "") or CENTRE_WORDS.search(self.linetype or ""))


def segments(entities: Iterable[CadEntity]) -> list[Seg]:
    """Straight pieces of lines and polylines (bulged polyline edges are arcs and are left out)."""
    out: list[Seg] = []
    for e in entities:
        if e.kind == "line" and len(e.points) == 2:
            out.append(Seg(e.points[0], e.points[1], e.handle, e.layer, e.linetype, e.color, e.dxftype))
        elif e.kind == "polyline" and len(e.points) >= 2 and not e.approximated:
            pts = list(e.points) + ([e.points[0]] if e.closed else [])
            bulges = list(e.bulges or [])
            for i, (a, b) in enumerate(zip(pts, pts[1:])):
                if i < len(bulges) and bulges[i]:
                    continue
                if math.dist(a, b) > 1e-9:
                    out.append(Seg(a, b, e.handle, e.layer, e.linetype, e.color, e.dxftype))
    return out


def on_segment(p: Point, s: Seg, tol: float = TOL) -> bool:
    """p lies on segment s (ends included) within tol."""
    ax, ay = s.a
    dx, dy = s.b[0] - ax, s.b[1] - ay
    length2 = dx * dx + dy * dy
    if length2 == 0:
        return math.dist(p, s.a) <= tol
    t = ((p[0] - ax) * dx + (p[1] - ay) * dy) / length2
    t = max(0.0, min(1.0, t))
    return math.dist(p, (ax + t * dx, ay + t * dy)) <= tol


class HIndex:
    """Horizontal segments, for "which line is at this height, across this x" questions."""

    def __init__(self, segs: Iterable[Seg], tol: float = TOL):
        self.tol = tol
        self.segs = sorted((s for s in segs if s.horizontal(tol)), key=lambda s: s.a[1])

    def at(self, y: float, tol: float | None = None) -> list[Seg]:
        tol = self.tol if tol is None else tol
        return [s for s in self.segs if abs(s.a[1] - y) <= tol]

    def touching(self, p: Point, tol: float | None = None) -> list[Seg]:
        tol = self.tol if tol is None else tol
        return [s for s in self.at(p[1], tol) if s.x0 - tol <= p[0] <= s.x1 + tol]

    def below(self, x: float, y: float) -> float | None:
        """The nearest horizontal line under (x, y) that runs across x."""
        ys = [s.a[1] for s in self.segs if s.a[1] < y - self.tol and s.x0 - self.tol <= x <= s.x1 + self.tol]
        return max(ys) if ys else None

    def extent(self, y: float) -> tuple[float, float] | None:
        segs = self.at(y)
        return (min(s.x0 for s in segs), max(s.x1 for s in segs)) if segs else None

    def coverage(self, y: float) -> float:
        """Length covered by the union of the horizontal segments at y."""
        spans = sorted((s.x0, s.x1) for s in self.at(y))
        total, cur = 0.0, None
        for x0, x1 in spans:
            if cur is None or x0 > cur[1]:
                if cur:
                    total += cur[1] - cur[0]
                cur = [x0, x1]
            else:
                cur[1] = max(cur[1], x1)
        return total + (cur[1] - cur[0] if cur else 0.0)


# ---------------------------------------------------------------- dimensions

@dataclass
class Measure:
    """A linear dimension: what it measures (value, mm as written) and between which drawn points."""

    handle: str
    layer: str
    value: float
    text: str  # as displayed: the override with "<>" filled in, or the measured value
    axis: Literal["h", "v", "o"]  # measures along x, along y, or another direction
    p1: Point  # extension line origins
    p2: Point
    line: Point  # a point on the dimension line
    geom: float  # the distance the dimension spans in drawing units
    overridden: bool = False  # the displayed text replaces the measured value

    @property
    def scale(self) -> float | None:
        """Written value per drawing unit (1 for a true-size drawing; 5 for a 1:5 detail on a 1:1 sheet)."""
        return self.value / self.geom if self.geom > 1e-9 else None

    @property
    def lo(self) -> float:
        i = 1 if self.axis == "v" else 0
        return min(self.p1[i], self.p2[i])

    @property
    def hi(self) -> float:
        i = 1 if self.axis == "v" else 0
        return max(self.p1[i], self.p2[i])

    @property
    def lo_point(self) -> Point:
        i = 1 if self.axis == "v" else 0
        return self.p1 if self.p1[i] <= self.p2[i] else self.p2

    @property
    def hi_point(self) -> Point:
        i = 1 if self.axis == "v" else 0
        return self.p2 if self.p1[i] <= self.p2[i] else self.p1


def measures(entities: Iterable[CadEntity]) -> list[Measure]:
    """Linear and aligned dimensions. Dimensions on internal layers ("*...", e.g. dynamic-block constraints) are
    parameters, not annotation, and are left out."""
    out = []
    for e in entities:
        if e.kind != "dimension" or e.layer.startswith("*") or e.measurement is None:
            continue
        if e.dim_kind not in ("linear", "aligned") or len(e.points) < 3:
            continue
        line, p1, p2 = e.points[0], e.points[1], e.points[2]
        if e.dim_kind == "linear":
            r = math.radians(e.rotation or 0.0)
            d = (math.cos(r), math.sin(r))
        else:
            length = math.dist(p1, p2) or 1.0
            d = ((p2[0] - p1[0]) / length, (p2[1] - p1[1]) / length)
        geom = abs((p2[0] - p1[0]) * d[0] + (p2[1] - p1[1]) * d[1])
        axis = "h" if abs(d[1]) < 1e-6 else "v" if abs(d[0]) < 1e-6 else "o"
        text, overridden = display_text(e)
        out.append(Measure(e.handle, e.layer, float(e.measurement), text, axis, p1, p2, line, geom, overridden))
    return out


def display_text(e: CadEntity) -> tuple[str, bool]:
    value = f"{e.measurement:g}" if e.measurement is not None else ""
    raw = (e.raw_text or "").strip()
    if raw in ("", "<>"):
        return value, False
    return raw.replace("<>", value), True


# ---------------------------------------------------------------- texts and leaders

def flat(text: str | None) -> str:
    return " ".join((text or "").split())


def text_box(e: CadEntity) -> Rect:
    """Approximate extents of a text (the reader keeps only its alignment point)."""
    h = e.height or 0.0
    lines = (e.text or "").split("\n") or [""]
    w = max(len(line) for line in lines) * h * CHAR_WIDTH
    total = h + (len(lines) - 1) * h * LINE_SPACING
    name = (e.alignment or "LEFT").upper()
    if "RIGHT" in name:
        dx = -w
    elif "CENTER" in name or name == "MIDDLE":
        dx = -w / 2
    else:
        dx = 0.0
    if "TOP" in name:
        dy = -total
    elif "MIDDLE" in name:
        dy = -total / 2
    else:
        dy = 0.0
    x, y = e.points[0]
    corners = [(x + dx, y + dy), (x + dx + w, y + dy), (x + dx + w, y + dy + total), (x + dx, y + dy + total)]
    if e.rotation:
        c, s = math.cos(math.radians(e.rotation)), math.sin(math.radians(e.rotation))
        corners = [(x + (px - x) * c - (py - y) * s, y + (px - x) * s + (py - y) * c) for px, py in corners]
    xs, ys = [p[0] for p in corners], [p[1] for p in corners]
    return (min(xs), min(ys), max(xs), max(ys))


def rect_distance(p: Point, r: Rect) -> float:
    dx = max(r[0] - p[0], 0.0, p[0] - r[2])
    dy = max(r[1] - p[1], 0.0, p[1] - r[3])
    return math.hypot(dx, dy)


def texts(entities: Iterable[CadEntity]) -> list[CadEntity]:
    return [e for e in entities if e.kind == "text" and flat(e.text)]


@dataclass
class Leader:
    tips: list[Point]  # where the leader points (one per branch)
    handles: list[str]


def leader(note: CadEntity, segs: list[Seg], tol: float = TOL, others: Iterable[CadEntity] = (),
           arrows: Iterable["Arrow"] = ()) -> Leader | None:
    """The leader drawn from a note: segments on the note's layer (or LEADER entities) that start at the note,
    followed through their joints to their free ends; an arrowhead at a free end moves the tip to the arrow's point.
    A start nearer another note (``others``) is that note's."""
    box = text_box(note)
    reach = (note.height or 0.0) * LEADER_REACH + tol
    arrows = list(arrows)
    boxes = [text_box(o) for o in others if o.handle != note.handle]
    pool = [s for s in segs if s.layer == note.layer or s.dxftype == "LEADER"]

    def mine(seg: Seg) -> bool:
        d = min(rect_distance(seg.a, box), rect_distance(seg.b, box))
        if d > reach:
            return False
        return all(min(rect_distance(seg.a, b), rect_distance(seg.b, b)) >= d for b in boxes)

    starts = [s for s in pool if mine(s)]
    if not starts:
        return None
    seen: set[int] = set()
    stack = [pool.index(s) for s in starts]
    while stack:
        i = stack.pop()
        if i in seen:
            continue
        seen.add(i)
        if len(seen) > LEADER_MAX_SEGMENTS:
            return None  # joined to drawing geometry: not a leader
        for j, other in enumerate(pool):
            if j not in seen and any(math.dist(p, q) <= tol for p in (pool[i].a, pool[i].b) for q in (other.a, other.b)):
                stack.append(j)
    chain = [pool[i] for i in seen]
    ends = [p for s in chain for p in (s.a, s.b)]
    open_ends = [p for p in ends if sum(math.dist(p, q) <= tol for q in ends) == 1]
    handles = {s.handle for s in chain}
    tips: list[Point] = []
    for p in open_ends:
        arrow = next((a for a in arrows if math.dist(a.base, p) <= max(tol, a.size * 0.1)), None)
        if arrow:
            tips.append(arrow.tip)
            handles.add(arrow.handle)
        elif rect_distance(p, box) > reach:
            tips.append(p)
    if not tips:
        far = max(ends, key=lambda p: rect_distance(p, box))
        if rect_distance(far, box) <= reach:
            return None
        tips = [far]
    unique: list[Point] = []
    for p in tips:
        if all(math.dist(p, q) > tol for q in unique):
            unique.append(p)
    return Leader(unique, sorted(handles))


def leaders(notes: list[CadEntity], segs: list[Seg], tol: float = TOL, arrows: Iterable["Arrow"] = ()
            ) -> dict[str, Leader]:
    """Leaders of several notes at once, each start given to its nearest note. On the layer that carries most of
    the drawing's geometry a leader cannot be told from the drawing, so there only LEADER entities count."""
    out = {}
    arrows = list(arrows)
    counts: dict[str, int] = {}
    for seg in segs:
        counts[seg.layer] = counts.get(seg.layer, 0) + 1
    main = max(counts, key=counts.get) if counts else None
    for note in notes:
        pool = [s for s in segs if s.layer != main or s.dxftype == "LEADER"]
        ld = leader(note, pool, tol, notes, arrows)
        if ld:
            out[note.handle] = ld
    return out


# ---------------------------------------------------------------- level marks

LEVEL_LABELS: list[tuple[str, re.Pattern]] = [
    ("ground", re.compile(r"^(?:N\.?|E\.?|F\.?)?G\.?\s*L\.?$|^GROUND\s+LEVEL$", re.I)),
    ("plinth", re.compile(r"^P\.?\s*L\.?$|^PLINTH(?:\s+LEVEL)?$", re.I)),
    ("floor", re.compile(r"^F\.?\s*F\.?\s*L\.?$", re.I)),
    ("roof", re.compile(r"^(?:ROOF|TERRACE)\s+LEVEL$|^T\.?O\.?S\.?$", re.I)),
    ("lintel", re.compile(r"^LINTEL(?:\s+LEVEL)?$", re.I)),
    ("sill", re.compile(r"^SILL(?:\s+LEVEL)?$", re.I)),
]


def level_kind(text: str) -> str | None:
    t = flat(text)
    for kind, pattern in LEVEL_LABELS:
        if pattern.match(t):
            return kind
    return None


@dataclass
class LevelMark:
    kind: str
    text: str
    y: float  # the level line's height
    handles: list[str]  # the text and the line
    candidates: int = 1  # lines that could be this mark's line (more than one with similar fit: ambiguous)


def level_marks(entities: list[CadEntity], hidx: HIndex) -> list[LevelMark]:
    """Level labels and the horizontal line each one marks: a line through the text's height band or just under its
    baseline, starting beside the text (or running past it)."""
    out = []
    for t in texts(entities):
        kind = level_kind(t.text)
        if not kind:
            continue
        h = t.height or 0.0
        box = text_box(t)
        scored = []
        for s in hidx.segs:
            y = s.a[1]
            if not (box[1] - 0.5 * h <= y <= box[3]):
                continue
            gap = max(s.x0 - box[2], box[0] - s.x1, 0.0)
            if gap > 2 * h:
                continue
            scored.append((gap + abs(y - (box[1] + box[3]) / 2), y, s.handle))
        if not scored:
            continue
        scored.sort()
        best = scored[0]
        close = {round(y, 3) for score, y, _ in scored if score <= best[0] * 1.1 + TOL}
        out.append(LevelMark(kind, flat(t.text), best[1], [t.handle, best[2]], candidates=len(close)))
    return out


# ---------------------------------------------------------------- arrowheads and rectangles

@dataclass
class Arrow:
    tip: Point
    base: Point  # middle of the base
    handle: str

    @property
    def direction(self) -> Point:
        d = math.dist(self.tip, self.base) or 1.0
        return ((self.tip[0] - self.base[0]) / d, (self.tip[1] - self.base[1]) / d)

    @property
    def size(self) -> float:
        return math.dist(self.tip, self.base)


def arrowheads(entities: Iterable[CadEntity]) -> list[Arrow]:
    """Solid triangles: the tip is the corner opposite the shortest side."""
    out = []
    for e in entities:
        if e.kind != "hatch" or not e.solid:
            continue
        for boundary in e.boundaries or []:
            pts = simplify(boundary)
            if len(pts) != 3:
                continue
            sides = [(math.dist(pts[(i + 1) % 3], pts[(i + 2) % 3]), i) for i in range(3)]
            shortest, i = min(sides)
            if shortest <= 0 or sorted(sides)[1][0] < shortest * 1.5:
                continue  # not a narrow triangle
            a, b = pts[(i + 1) % 3], pts[(i + 2) % 3]
            out.append(Arrow(pts[i], ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2), e.handle))
    return out


def simplify(points: list[Point], tol: float = 1e-6) -> list[Point]:
    """Drop repeated and collinear vertices of a closed polygon."""
    pts = [p for i, p in enumerate(points) if i == 0 or math.dist(p, points[i - 1]) > tol]
    if len(pts) > 1 and math.dist(pts[0], pts[-1]) <= tol:
        pts.pop()
    changed = True
    while changed and len(pts) > 3:
        changed = False
        for i in range(len(pts)):
            a, b, c = pts[i - 1], pts[i], pts[(i + 1) % len(pts)]
            cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])
            if abs(cross) <= tol * max(math.dist(a, b), math.dist(b, c), 1.0):
                pts.pop(i)
                changed = True
                break
    return pts


def as_rect(points: list[Point], tol: float = TOL) -> Rect | None:
    pts = simplify(points)
    if len(pts) != 4:
        return None
    for a, b in zip(pts, pts[1:] + pts[:1]):
        if abs(a[0] - b[0]) > tol and abs(a[1] - b[1]) > tol:
            return None
    xs, ys = [p[0] for p in pts], [p[1] for p in pts]
    return (min(xs), min(ys), max(xs), max(ys))


@dataclass
class Box:
    rect: Rect
    handles: list[str] = field(default_factory=list)

    @property
    def center(self) -> Point:
        return ((self.rect[0] + self.rect[2]) / 2, (self.rect[1] + self.rect[3]) / 2)

    @property
    def width(self) -> float:
        return self.rect[2] - self.rect[0]

    @property
    def height(self) -> float:
        return self.rect[3] - self.rect[1]


def solid_boxes(entities: Iterable[CadEntity], tol: float = TOL) -> list[Box]:
    """Axis-aligned rectangles filled solid (hatch loops), touching ones merged: how columns are usually drawn."""
    boxes = []
    for e in entities:
        if e.kind == "hatch" and e.solid:
            for boundary in e.boundaries or []:
                rect = as_rect(boundary)
                if rect:
                    boxes.append(Box(rect, [e.handle]))
    return merge_touching(boxes, tol)


def merge_touching(boxes: list[Box], tol: float = TOL) -> list[Box]:
    parent = list(range(len(boxes)))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    order = sorted(range(len(boxes)), key=lambda i: boxes[i].rect[0])
    for n, i in enumerate(order):
        a = boxes[i].rect
        for j in order[n + 1:]:
            b = boxes[j].rect
            if b[0] > a[2] + tol:
                break
            if b[1] <= a[3] + tol and a[1] <= b[3] + tol:
                parent[find(j)] = find(i)
    groups: dict[int, Box] = {}
    for i, box in enumerate(boxes):
        g = groups.get(find(i))
        if g is None:
            groups[find(i)] = Box(box.rect, list(box.handles))
        else:
            r = g.rect
            g.rect = (min(r[0], box.rect[0]), min(r[1], box.rect[1]), max(r[2], box.rect[2]), max(r[3], box.rect[3]))
            g.handles += [h for h in box.handles if h not in g.handles]
    return list(groups.values())


def outline_boxes(entities: Iterable[CadEntity], tol: float = TOL) -> list[Box]:
    """Axis-aligned rectangles drawn as one closed polyline (collinear vertices allowed)."""
    out = []
    for e in entities:
        if e.kind == "polyline" and not any(e.bulges or []) and (e.closed or (
                len(e.points) > 4 and math.dist(e.points[0], e.points[-1]) <= tol)):
            rect = as_rect(e.points, tol)
            if rect:
                out.append(Box(rect, [e.handle]))
    return out


def line_boxes(segs: list[Seg], tol: float = TOL) -> list[Box]:
    """Axis-aligned rectangles whose four sides are separate segments meeting at the corners."""
    hs = [s for s in segs if s.horizontal(tol)]
    vs = [s for s in segs if s.vertical(tol)]
    by_x: dict[int, list[Seg]] = {}
    for v in vs:
        by_x.setdefault(round(v.a[0]), []).append(v)

    def side(x: float, y0: float, y1: float) -> Seg | None:
        for k in (round(x) - 1, round(x), round(x) + 1):
            for v in by_x.get(k, []):
                if abs(v.a[0] - x) <= tol and abs(v.y0 - y0) <= tol and abs(v.y1 - y1) <= tol:
                    return v
        return None

    out = []
    hs.sort(key=lambda s: (round(s.x0), round(s.x1), s.a[1]))
    for i, a in enumerate(hs):
        for b in hs[i + 1:]:
            if abs(b.x0 - a.x0) > tol or abs(b.x1 - a.x1) > tol:
                if b.x0 > a.x0 + tol:
                    break
                continue
            if b.a[1] - a.a[1] <= tol:
                continue
            left, right = side(a.x0, a.a[1], b.a[1]), side(a.x1, a.a[1], b.a[1])
            if left and right:
                out.append(Box((a.x0, a.a[1], a.x1, b.a[1]), [a.handle, b.handle, left.handle, right.handle]))
    return out


def inside(p: Point, r: Rect, tol: float = 0.0) -> bool:
    return r[0] - tol <= p[0] <= r[2] + tol and r[1] - tol <= p[1] <= r[3] + tol


def median(values: list[float]) -> float | None:
    if not values:
        return None
    v = sorted(values)
    n = len(v)
    return v[n // 2] if n % 2 else (v[n // 2 - 1] + v[n // 2]) / 2
