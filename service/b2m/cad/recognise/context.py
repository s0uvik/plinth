"""What every recogniser shares: the sheet's visible drawing entities as primitives (segments, arcs, texts, solid
fills, points, block references), the tolerances, the claims (which entity went into which element) and the
findings. Nothing here is specific to a drawing: tolerances come from the drawing's unit (everything is mm by now)
and from ``Params``, which holds building-practice bounds (e.g. how thick a wall can plausibly be), never values
read off a sample.
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass, field
from pathlib import Path

from b2m.cad.building import Evidence, Finding
from b2m.cad.reader import CadEntity
from b2m.cad.recognise.geometry import Point, Seg, frame

HIDDEN_RE = re.compile(r"HIDDEN|DASHED|DASH(?!DOT)|DOT(?!.*DASH)", re.I)
CENTER_RE = re.compile(r"CENT(?:ER|RE)|DASHDOT|PHANTOM|AXIS|DIVIDE", re.I)


@dataclass(frozen=True)
class Params:
    """Building-practice bounds used to tell elements apart. Every value is a general bound, not a sample value."""

    tol_mm: float = 1.0  # snapping / coincidence tolerance (the drawings are true size in mm)
    ang_tol_deg: float = 0.5  # parallel within this
    wall_min_mm: float = 40.0  # thinnest plausible wall (partition)
    wall_max_mm: float = 700.0  # thickest plausible wall
    class_min_share: float = 0.03  # a thickness class holds at least this share of paired length
    column_max_mm: float = 2000.0  # longest side of a plausible column section
    column_min_mm: float = 100.0  # shortest side of a plausible column section
    opening_max_mm: float = 4000.0  # widest plausible single opening (door/window/plain) in a wall
    text_width_factor: float = 0.75  # estimated character width / text height (box estimate for nearness)
    projection_max_share: float = 0.25  # a projection is smaller than this share of the building outline


@dataclass
class Text:
    handle: str
    text: str
    pos: Point  # insertion point
    height: float
    rotation: float
    center: Point  # estimated centre of the text box
    box: list[Point]  # estimated text box corners

    @property
    def width(self) -> float:
        return math.dist(self.box[0], self.box[1])

    @property
    def flat(self) -> str:
        return " ".join(self.text.replace("%%U", "").replace("%%u", "").split())


@dataclass
class Arc:
    handle: str
    top: str  # top-level handle (the insert it sits in, or itself)
    center: Point
    radius: float
    start: float  # degrees ccw
    end: float

    @property
    def sweep(self) -> float:
        return (self.end - self.start) % 360.0 or 360.0

    def point_at(self, angle_deg: float) -> Point:
        r = math.radians(angle_deg)
        return (self.center[0] + self.radius * math.cos(r), self.center[1] + self.radius * math.sin(r))

    @property
    def p_start(self) -> Point:
        return self.point_at(self.start)

    @property
    def p_end(self) -> Point:
        return self.point_at(self.end)


@dataclass
class Fill:
    handle: str
    top: str
    polygon: list[Point]


@dataclass
class Insert:
    entity: CadEntity
    name: str  # effective block name when dynamic, else the block name
    segs: list[Seg]
    arcs: list[Arc]
    circles: list[Arc]
    texts: list[Text]
    fills: list[Fill]


@dataclass
class Ctx:
    sheet: str
    params: Params
    entities: list[CadEntity]  # top level drawing entities
    layer_linetypes: dict[str, str]
    segs: list[Seg] = field(default_factory=list)  # loose linework (lines, polyline edges), visible
    arcs: list[Arc] = field(default_factory=list)  # loose arcs
    circles: list[Arc] = field(default_factory=list)
    texts: list[Text] = field(default_factory=list)  # all visible text, loose and in blocks (attributes)
    fills: list[Fill] = field(default_factory=list)  # loose solid fills (hatch boundaries, SOLID)
    points: list[tuple[str, Point]] = field(default_factory=list)
    inserts: list[Insert] = field(default_factory=list)
    others: list[CadEntity] = field(default_factory=list)  # dimensions and anything else
    claims: dict[str, str] = field(default_factory=dict)  # handle -> what claimed it
    findings: list[Finding] = field(default_factory=list)
    notes: dict[str, str] = field(default_factory=dict)  # handle -> classification for unclaimed evidence
    groups: list[tuple[list[str], str]] = field(default_factory=list)  # unclaimed items listed together, with a note
    _ids: dict[str, int] = field(default_factory=dict)

    def new_id(self, prefix: str) -> str:
        self._ids[prefix] = self._ids.get(prefix, 0) + 1
        return f"{prefix}-{self._ids[prefix]}"

    def ev(self, handles: list[str], note: str | None = None) -> Evidence:
        seen: list[str] = []
        for h in handles:
            if h not in seen:
                seen.append(h)
        return Evidence(sheet=self.sheet, handles=seen, note=note)

    def claim(self, handles: list[str], by: str) -> None:
        for h in handles:
            self.claims.setdefault(h, by)

    def claimed(self, handle: str) -> bool:
        return handle in self.claims

    def finding(self, topic: str, text: str, handles: list[str], severity: str = "check",
                options: list[str] | None = None, note: str | None = None) -> Finding:
        f = Finding(id=self.new_id(topic.replace("_", "-")), topic=topic, text=text, severity=severity,
                    evidence=self.ev(handles, note), options=options or [])
        self.findings.append(f)
        return f

    def linetype_class(self, e: CadEntity, parent: CadEntity | None = None) -> str:
        """continuous, hidden or center: the entity's effective linetype (BYLAYER and BYBLOCK resolved)."""
        lt = (e.linetype or "BYLAYER")
        if lt.upper() == "BYBLOCK" and parent is not None:
            lt = parent.linetype or "BYLAYER"
            if lt.upper() == "BYLAYER":
                lt = self.layer_linetypes.get(parent.layer, "Continuous")
        if lt.upper() == "BYLAYER":
            lt = self.layer_linetypes.get(e.layer, "Continuous")
        if CENTER_RE.search(lt):
            return "center"
        if HIDDEN_RE.search(lt):
            return "hidden"
        return "continuous"


def text_item(e: CadEntity, params: Params) -> Text | None:
    if not e.text or not e.text.strip() or not e.points:
        return None
    h = e.height or 0.0
    rot = e.rotation or 0.0
    flat = " ".join(e.text.split())
    w = max(len(flat), 1) * h * params.text_width_factor
    u, n = frame(rot)
    p = e.points[0]
    align = (e.alignment or "LEFT").upper()
    # where the insertion point sits on the box: fraction along the width and the height
    fx = 0.5 if ("CENTER" in align or "MIDDLE" == align or align in ("ALIGNED", "FIT", "MIDDLE")) else (
        1.0 if "RIGHT" in align else 0.0)
    fy = 1.0 if "TOP" in align else (0.5 if ("MIDDLE" in align) else 0.0)
    if e.dxftype == "MTEXT":
        fy = 1.0 if ("TOP" in align or align in ("LEFT", "CENTER", "RIGHT")) else fy
    origin = (p[0] - u[0] * w * fx - n[0] * h * fy, p[1] - u[1] * w * fx - n[1] * h * fy)
    box = [origin, (origin[0] + u[0] * w, origin[1] + u[1] * w),
           (origin[0] + u[0] * w + n[0] * h, origin[1] + u[1] * w + n[1] * h),
           (origin[0] + n[0] * h, origin[1] + n[1] * h)]
    center = (origin[0] + u[0] * w / 2 + n[0] * h / 2, origin[1] + u[1] * w / 2 + n[1] * h / 2)
    return Text(e.handle, flat, p, h, rot, center, box)


def _polyline_segs(e: CadEntity) -> list[tuple[Point, Point]]:
    pts = e.points
    out = []
    n = len(pts)
    bulges = e.bulges or [0.0] * n
    for i in range(n if e.closed else n - 1):
        a, b = pts[i], pts[(i + 1) % n]
        if a == b:
            continue
        if i < len(bulges) and bulges[i] and abs(bulges[i]) > 1e-9:
            continue  # curved edge: handled as an arc
        out.append((a, b))
    return out


def _polyline_arcs(e: CadEntity) -> list[tuple[Point, float, float, float]]:
    """Bulged polyline edges as arcs (centre, radius, start, end degrees ccw)."""
    out = []
    pts = e.points
    n = len(pts)
    bulges = e.bulges or []
    for i in range(n if e.closed else n - 1):
        if i >= len(bulges) or not bulges[i] or abs(bulges[i]) < 1e-9:
            continue
        a, b = pts[i], pts[(i + 1) % n]
        bulge = bulges[i]
        chord = math.dist(a, b)
        if chord == 0:
            continue
        theta = 4 * math.atan(bulge)
        r = chord / (2 * math.sin(abs(theta) / 2))
        mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
        d = math.sqrt(max(r * r - (chord / 2) ** 2, 0.0))
        ux, uy = (b[0] - a[0]) / chord, (b[1] - a[1]) / chord
        sign = 1 if (bulge > 0) == (abs(theta) < math.pi) else -1
        c = (mid[0] - uy * d * sign, mid[1] + ux * d * sign)
        sa = math.degrees(math.atan2(a[1] - c[1], a[0] - c[0])) % 360
        sb = math.degrees(math.atan2(b[1] - c[1], b[0] - c[0])) % 360
        out.append((c, r, sa, sb) if bulge > 0 else (c, r, sb, sa))
    return out


def build(sheet_name: str, entities: list[CadEntity], layer_linetypes: dict[str, str],
          params: Params | None = None) -> Ctx:
    params = params or Params()
    ctx = Ctx(sheet=sheet_name, params=params, entities=entities, layer_linetypes=layer_linetypes)
    for e in entities:
        if e.invisible:
            continue
        if e.kind == "insert":
            ctx.inserts.append(_insert(ctx, e))
            continue
        _loose(ctx, e)
    return ctx


def _loose(ctx: Ctx, e: CadEntity) -> None:
    lt = ctx.linetype_class(e)
    if e.kind == "line" and len(e.points) == 2 and e.points[0] != e.points[1]:
        ctx.segs.append(Seg(e.points[0], e.points[1], [e.handle], lt, e.layer))
    elif e.kind == "polyline":
        for a, b in _polyline_segs(e):
            ctx.segs.append(Seg(a, b, [e.handle], lt, e.layer))
        for c, r, s0, s1 in _polyline_arcs(e):
            ctx.arcs.append(Arc(e.handle, e.handle, c, r, s0, s1))
    elif e.kind == "arc" and e.radius:
        ctx.arcs.append(Arc(e.handle, e.handle, e.points[0], e.radius, e.start_angle or 0.0, e.end_angle or 360.0))
    elif e.kind == "circle" and e.radius:
        ctx.circles.append(Arc(e.handle, e.handle, e.points[0], e.radius, 0.0, 360.0))
    elif e.kind == "text":
        t = text_item(e, ctx.params)
        if t:
            ctx.texts.append(t)
    elif e.kind == "hatch":
        if e.solid and e.boundaries:
            for b in e.boundaries:
                if len(b) >= 3:
                    ctx.fills.append(Fill(e.handle, e.handle, list(b)))
        else:
            ctx.others.append(e)
    elif e.kind == "point" and e.points:
        ctx.points.append((e.handle, e.points[0]))
    else:
        ctx.others.append(e)


def _insert(ctx: Ctx, e: CadEntity) -> Insert:
    ins = Insert(e, e.effective_block or e.block or "", [], [], [], [], [])
    for c in e.walk():
        if c is e or c.invisible or c.kind == "insert":
            continue
        if c.kind == "line" and len(c.points) == 2 and c.points[0] != c.points[1]:
            ins.segs.append(Seg(c.points[0], c.points[1], [c.handle], ctx.linetype_class(c, e), c.layer))
        elif c.kind == "polyline":
            for a, b in _polyline_segs(c):
                ins.segs.append(Seg(a, b, [c.handle], ctx.linetype_class(c, e), c.layer))
            for cc, r, s0, s1 in _polyline_arcs(c):
                ins.arcs.append(Arc(c.handle, e.handle, cc, r, s0, s1))
        elif c.kind == "arc" and c.radius:
            ins.arcs.append(Arc(c.handle, e.handle, c.points[0], c.radius, c.start_angle or 0.0, c.end_angle or 360.0))
        elif c.kind == "circle" and c.radius:
            ins.circles.append(Arc(c.handle, e.handle, c.points[0], c.radius, 0.0, 360.0))
        elif c.kind == "text":
            t = text_item(c, ctx.params)
            if t:
                ins.texts.append(t)
                ctx.texts.append(t)
        elif c.kind == "hatch" and c.solid and c.boundaries:
            for b in c.boundaries:
                if len(b) >= 3:
                    ins.fills.append(Fill(c.handle, e.handle, list(b)))
    return ins


def sheet_name(source: str) -> str:
    return Path(source).name
