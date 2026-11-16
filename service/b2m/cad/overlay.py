"""SVG of a sheet's drawing entities with what plan recognition understood drawn over them (PRD Step 9, stage 6).

    svg = overlay.render(sheet.drawing_entities, recognition)

Source geometry is drawn thin and grey (inserts exploded, invisible entities skipped); recognised elements are drawn
in colour on top: walls as filled bodies by kind with their centrelines, columns, openings (door, window, plain)
with marks, rooms with names and boundaries, stairs, shafts, projections, the building outline, findings as
numbered rings at their evidence and unclaimed geometry in orange. Drawing coordinates are millimetres, y up; the
SVG flips y so the plan reads as printed. Every recognised shape carries data-id and data-handles attributes so a
review page can link a shape to its element and evidence. Styling is inline presentation attributes (plus a class
per shape), so any SVG renderer draws it the same.
"""

from __future__ import annotations

import html
import math
from typing import Iterable

from b2m.cad.building import PlanRecognition
from b2m.cad.reader import CadEntity

Rect = tuple[float, float, float, float]

# Per class; "sw" is a stroke width in output pixels, scaled to drawing millimetres at render time.
STYLES: dict[str, dict[str, str]] = {
    "src": {"fill": "none", "stroke": "#8c8c8c", "sw": "0.7"},
    "srcfill": {"fill": "#bdbdbd", "stroke": "none"},
    "srctext": {"fill": "#7a7a7a", "font-family": "sans-serif"},
    "wall exterior": {"fill": "#1f5fbf", "fill-opacity": "0.45"},
    "wall interior": {"fill": "#2aa198", "fill-opacity": "0.45"},
    "wall parapet": {"fill": "#8e44ad", "fill-opacity": "0.45"},
    "wall boundary": {"fill": "#7f6000", "fill-opacity": "0.45"},
    "wall unknown": {"fill": "#e67e22", "fill-opacity": "0.55"},
    "axis": {"fill": "none", "stroke": "#0b2a5b", "sw": "1"},
    "column": {"fill": "#c0392b", "fill-opacity": "0.7"},
    "opening door": {"fill": "none", "stroke": "#27ae60", "sw": "3"},
    "opening window": {"fill": "none", "stroke": "#2980b9", "sw": "3"},
    "opening opening": {"fill": "none", "stroke": "#f39c12", "sw": "3"},
    "opening ventilator": {"fill": "none", "stroke": "#16a085", "sw": "3"},
    "hinge": {"fill": "#27ae60"},
    "mark door": {"fill": "#1e8449", "font-family": "sans-serif", "font-weight": "bold"},
    "mark window": {"fill": "#1f618d", "font-family": "sans-serif", "font-weight": "bold"},
    "mark opening": {"fill": "#b9770e", "font-family": "sans-serif", "font-weight": "bold"},
    "mark ventilator": {"fill": "#117a65", "font-family": "sans-serif", "font-weight": "bold"},
    "room": {"fill": "#f1c40f", "fill-opacity": "0.18", "stroke": "#b7950b", "sw": "1"},
    "roomname": {"fill": "#7d6608", "font-family": "sans-serif"},
    "stair": {"fill": "#9b59b6", "fill-opacity": "0.2", "stroke": "#6c3483", "sw": "1"},
    "shaft": {"fill": "#34495e", "fill-opacity": "0.2", "stroke": "#212f3d", "sw": "1"},
    "projection": {"fill": "#1abc9c", "fill-opacity": "0.2", "stroke": "#117a65", "sw": "1"},
    "outline": {"fill": "none", "stroke": "#e74c3c", "sw": "1.5"},
    "grid": {"fill": "none", "stroke": "#e84393", "sw": "0.8"},
    "unclaimed": {"fill": "none", "stroke": "#ff7f00", "sw": "1.8"},
    "unclaimedfill": {"fill": "#ff7f00", "stroke": "none"},
    "finding": {"fill": "none", "stroke": "#d6007e", "sw": "1.5"},
    "findingno": {"fill": "#d6007e", "font-family": "sans-serif", "font-weight": "bold"},
}


class _Svg:
    def __init__(self, mm_per_px: float):
        self.px = mm_per_px

    def a(self, cls: str) -> str:
        """class plus inline presentation attributes."""
        attrs = [f'class="{cls}"']
        for k, v in STYLES.get(cls, {}).items():
            attrs.append(f'stroke-width="{float(v) * self.px:.2f}"' if k == "sw" else f'{k}="{v}"')
        return " ".join(attrs)


def render(entities: list[CadEntity], recognition: PlanRecognition | None = None, *, bounds: Rect | None = None,
           width_px: int = 2400, source: bool = True) -> str:
    """The SVG text. bounds (drawing mm) defaults to everything drawn plus a margin."""
    flat = [e for top in entities for e in top.walk() if not e.invisible and e.kind != "insert"]
    by_handle = {e.handle: e for top in entities for e in top.walk()}
    if bounds is None:
        bounds = _bounds(flat) or (0.0, 0.0, 1000.0, 1000.0)
        pad = 0.03 * max(bounds[2] - bounds[0], bounds[3] - bounds[1])
        bounds = (bounds[0] - pad, bounds[1] - pad, bounds[2] + pad, bounds[3] + pad)
    x0, y0, x1, y1 = bounds
    w, h = max(x1 - x0, 1.0), max(y1 - y0, 1.0)
    svg = _Svg(w / width_px)
    unit = max(w, h) / 400.0  # a nominal label size in mm for what the overlay writes
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="{_f(x0)} {_f(-y1)} {_f(w)} {_f(h)}" '
           f'width="{width_px}" height="{int(width_px * h / w)}">',
           f'<rect x="{_f(x0)}" y="{_f(-y1)}" width="{_f(w)}" height="{_f(h)}" fill="#ffffff"/>']
    if source:
        out.append('<g class="source">')
        out.extend(_shape(svg, e, "src", "srcfill", "srctext") for e in flat)
        out.append("</g>")
    if recognition is not None:
        out.extend(_recognised(svg, recognition, by_handle, unit))
    out.append("</svg>")
    return "\n".join(s for s in out if s)


def to_png(svg: str, path: str, dpi: int = 96) -> bool:
    """Write a PNG of the SVG when PyMuPDF is importable (a debugging aid); False when it is not."""
    try:
        import pymupdf  # type: ignore
    except ImportError:
        return False
    doc = pymupdf.open(stream=svg.encode("utf-8"), filetype="svg")
    doc[0].get_pixmap(dpi=dpi).save(path)
    return True


def _f(v: float) -> str:
    return f"{v:.1f}"


def _pts(points: Iterable[tuple[float, float]]) -> str:
    return " ".join(f"{_f(x)},{_f(-y)}" for x, y in points)


def _bounds(entities: list[CadEntity]) -> Rect | None:
    xs: list[float] = []
    ys: list[float] = []
    for e in entities:
        b = e.bounds()
        if b:
            xs += [b[0], b[2]]
            ys += [b[1], b[3]]
    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


def _arc(svg: _Svg, cls: str, c: tuple[float, float], r: float, a0: float, a1: float) -> str:
    sweep = (a1 - a0) % 360 or 360
    if sweep >= 359.999:
        return f'<circle {svg.a(cls)} cx="{_f(c[0])}" cy="{_f(-c[1])}" r="{_f(r)}"/>'
    p0 = (c[0] + r * math.cos(math.radians(a0)), c[1] + r * math.sin(math.radians(a0)))
    p1 = (c[0] + r * math.cos(math.radians(a0 + sweep)), c[1] + r * math.sin(math.radians(a0 + sweep)))
    large = 1 if sweep > 180 else 0
    # counter-clockwise in drawing space is clockwise once y is flipped: sweep-flag 0
    return (f'<path {svg.a(cls)} d="M{_f(p0[0])},{_f(-p0[1])} A{_f(r)},{_f(r)} 0 {large} 0 '
            f'{_f(p1[0])},{_f(-p1[1])}"/>')


def _shape(svg: _Svg, e: CadEntity, cls: str, fill_cls: str, text_cls: str) -> str:
    if e.kind == "line" and len(e.points) == 2:
        (ax, ay), (bx, by) = e.points
        return f'<line {svg.a(cls)} x1="{_f(ax)}" y1="{_f(-ay)}" x2="{_f(bx)}" y2="{_f(-by)}"/>'
    if e.kind == "polyline" and len(e.points) >= 2:
        tag = "polygon" if e.closed else "polyline"
        return f'<{tag} {svg.a(cls)} points="{_pts(e.points)}"/>'
    if e.kind == "arc" and e.radius:
        return _arc(svg, cls, e.points[0], e.radius, e.start_angle or 0.0, e.end_angle or 360.0)
    if e.kind == "circle" and e.radius:
        return f'<circle {svg.a(cls)} cx="{_f(e.points[0][0])}" cy="{_f(-e.points[0][1])}" r="{_f(e.radius)}"/>'
    if e.kind == "hatch" and e.boundaries:
        c = fill_cls if e.solid else cls
        return "".join(f'<polygon {svg.a(c)} points="{_pts(b)}"/>' for b in e.boundaries if len(b) >= 3)
    if e.kind == "text" and e.text and e.points:
        x, y = e.points[0]
        size = e.height or 100.0
        rot = -(e.rotation or 0.0)
        return (f'<text {svg.a(text_cls)} x="{_f(x)}" y="{_f(-y)}" font-size="{_f(size)}" '
                f'transform="rotate({_f(rot)} {_f(x)} {_f(-y)})">{html.escape(e.text)}</text>')
    if e.kind == "point" and e.points:
        x, y = e.points[0]
        return f'<circle {svg.a(cls)} cx="{_f(x)}" cy="{_f(-y)}" r="{_f(3 * svg.px)}"/>'
    return ""


def _label(svg: _Svg, x: float, y: float, text: str, cls: str, size: float) -> str:
    return (f'<text {svg.a(cls)} x="{_f(x)}" y="{_f(-y)}" font-size="{_f(size)}" text-anchor="middle" '
            f'dominant-baseline="middle">{html.escape(text)}</text>')


def _data(element_id: str, handles: list[str]) -> str:
    return f' data-id="{html.escape(element_id)}" data-handles="{html.escape(" ".join(handles))}"'


def _recognised(svg: _Svg, r: PlanRecognition, by_handle: dict[str, CadEntity], unit: float) -> list[str]:
    a = svg.a
    out = ['<g class="recognised">']
    for room in r.rooms:
        if room.boundary:
            out.append(f'<polygon {a("room")} points="{_pts(room.boundary)}"{_data(room.id, room.evidence.handles)}/>')
    for p in r.projections:
        out.append(f'<polygon {a("projection")} points="{_pts(p.outline)}"{_data(p.id, p.evidence.handles)}/>')
    for s in r.stairs:
        out.append(f'<polygon {a("stair")} points="{_pts(s.outline)}"{_data(s.id, s.evidence.handles)}/>')
    for s in r.shafts:
        out.append(f'<polygon {a("shaft")} points="{_pts(s.outline)}"{_data(s.id, s.evidence.handles)}/>')
    for g in r.grids:
        out.append(f'<line {a("grid")} x1="{_f(g.start[0])}" y1="{_f(-g.start[1])}" x2="{_f(g.end[0])}" '
                   f'y2="{_f(-g.end[1])}"{_data(g.id, g.evidence.handles)}/>')
    for wall in r.walls:
        (ax, ay), (bx, by) = wall.start, wall.end
        length = math.hypot(bx - ax, by - ay) or 1.0
        nx, ny = -(by - ay) / length * wall.thickness_mm / 2, (bx - ax) / length * wall.thickness_mm / 2
        body = [(ax + nx, ay + ny), (bx + nx, by + ny), (bx - nx, by - ny), (ax - nx, ay - ny)]
        out.append(f'<polygon {a("wall " + wall.kind)} points="{_pts(body)}"{_data(wall.id, wall.evidence.handles)}/>')
        out.append(f'<line {a("axis")} x1="{_f(ax)}" y1="{_f(-ay)}" x2="{_f(bx)}" y2="{_f(-by)}"/>')
    for c in r.columns:
        t = math.radians(c.rotation_deg)
        ux, uy, vx, vy = math.cos(t), math.sin(t), -math.sin(t), math.cos(t)
        hw, hd = c.width_mm / 2, c.depth_mm / 2
        corners = [(c.center[0] + sx * hw * ux + sy * hd * vx, c.center[1] + sx * hw * uy + sy * hd * vy)
                   for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        out.append(f'<polygon {a("column")} points="{_pts(corners)}"{_data(c.id, c.evidence.handles)}/>')
    walls = {w.id: w for w in r.walls}
    for o in r.openings:
        wall = walls.get(o.wall_id or "")
        dx, dy = 1.0, 0.0
        if wall:
            dx, dy = wall.end[0] - wall.start[0], wall.end[1] - wall.start[1]
            length = math.hypot(dx, dy) or 1.0
            dx, dy = dx / length, dy / length
        half = o.width_mm / 2
        p, q = (o.center[0] - dx * half, o.center[1] - dy * half), (o.center[0] + dx * half, o.center[1] + dy * half)
        out.append(f'<line {a("opening " + o.kind)} x1="{_f(p[0])}" y1="{_f(-p[1])}" x2="{_f(q[0])}" '
                   f'y2="{_f(-q[1])}"{_data(o.id, o.evidence.handles)}/>')
        if o.hinge:
            out.append(f'<circle {a("hinge")} cx="{_f(o.hinge[0])}" cy="{_f(-o.hinge[1])}" r="{_f(unit * 0.6)}"/>')
        label = o.mark_normalised or ("?" if o.kind != "opening" else "")
        if label:
            out.append(_label(svg, o.center[0] - dy * unit * 3, o.center[1] + dx * unit * 3, label,
                              "mark " + o.kind, unit * 3))
    for room in r.rooms:
        size = f" {room.labelled_size_mm[0]:g}x{room.labelled_size_mm[1]:g}" if room.labelled_size_mm else ""
        area = f" {room.area_m2:.2f}m2" if room.area_m2 else ""
        unit_id = f" [{room.unit}]" if room.unit else ""
        out.append(_label(svg, room.point[0], room.point[1] - unit * 4, room.name + size + area + unit_id,
                          "roomname", unit * 2.2))
    if r.outline:
        out.append(f'<polygon {a("outline")} points="{_pts(r.outline)}"/>')
    for ev in r.unclaimed:
        for h in ev.handles:
            e = by_handle.get(h)
            if e is not None:
                for part in e.walk():
                    if not part.invisible and part.kind != "insert":
                        out.append(_shape(svg, part, "unclaimed", "unclaimedfill", "unclaimedfill"))
    for i, f in enumerate(r.findings, 1):
        # one ring per piece of evidence (an entity's box, at most a few labels high), numbered as listed
        for k, h in enumerate(f.evidence.handles):
            b = by_handle[h].bounds() if h in by_handle else None
            if not b:
                continue
            cx, cy = (b[0] + b[2]) / 2, (b[1] + b[3]) / 2
            rad = min(max(math.hypot(b[2] - b[0], b[3] - b[1]) / 2, unit * 2), unit * 12)
            out.append(f'<circle {a("finding")} cx="{_f(cx)}" cy="{_f(-cy)}" r="{_f(rad)}" '
                       f'data-id="{html.escape(f.id)}"/>')
            if k == 0:
                out.append(_label(svg, cx + rad * 0.75, cy + rad * 0.75, str(i), "findingno", unit * 3))
    out.append("</g>")
    return out
