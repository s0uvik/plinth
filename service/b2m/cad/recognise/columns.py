"""Columns: solid-filled rectangles or circles of column size (hatches, SOLID entities).

Each boundary of a solid fill is checked on its own (one hatch can fill several columns). Touching fills that
together make one rectangle (a column filled in two halves) are merged. A filled rectangle outside the column size
bounds is not a column; triangles are arrowheads and are left for the arrow and section recognisers.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from shapely.geometry import LineString, Polygon
from shapely.ops import unary_union

from b2m.cad.building import Column
from b2m.cad.recognise.context import Ctx, Fill
from b2m.cad.recognise.geometry import Point, is_rectangle


@dataclass
class ColumnShape:
    column: Column
    polygon: Polygon


def find_columns(ctx: Ctx) -> list[ColumnShape]:
    p = ctx.params
    tol = p.tol_mm
    candidates: list[tuple[Polygon, list[str]]] = []
    for f in ctx.fills:
        if ctx.claimed(f.handle):
            continue
        poly = _polygon(f)
        if poly is None:
            continue
        candidates.append((poly, [f.handle]))
    # merge touching fills
    merged: list[tuple[Polygon, list[str]]] = []
    for poly, handles in candidates:
        for i, (mp, mh) in enumerate(merged):
            if mp.buffer(tol).intersects(poly) and mp.buffer(tol).intersection(poly).area > 0:
                union = unary_union([mp.buffer(tol / 2), poly.buffer(tol / 2)]).buffer(-tol / 2)
                if union.geom_type == "Polygon" and _rect_of(union, tol):
                    merged[i] = (union, mh + handles)
                    break
        else:
            merged.append((poly, handles))
    out: list[ColumnShape] = []
    for poly, handles in merged:
        rect = _rect_of(poly, tol)
        circle = _circle_of(poly, tol) if rect is None else None
        if rect is not None:
            centre, w, d, rot = rect
            if not (p.column_min_mm - tol <= min(w, d) and max(w, d) <= p.column_max_mm + tol):
                continue
            col = Column(id=ctx.new_id("column"), center=_r(centre), width_mm=round(w, 1), depth_mm=round(d, 1),
                         rotation_deg=round(rot, 3), shape="rectangular", evidence=ctx.ev(handles))
        elif circle is not None:
            centre, dia = circle
            if not (p.column_min_mm - tol <= dia <= p.column_max_mm + tol):
                continue
            col = Column(id=ctx.new_id("column"), center=_r(centre), width_mm=round(dia, 1),
                         depth_mm=round(dia, 1), shape="circular", evidence=ctx.ev(handles))
        else:
            continue
        ctx.claim(handles, col.id)
        out.append(ColumnShape(col, poly))
    return out


def claim_outlines(ctx: Ctx, shapes: list[ColumnShape]) -> None:
    """Lines drawn along a column's edge and no further (the column outlined as well as filled) are the column's,
    so they cannot pair up into a short wall."""
    if not shapes:
        return
    tol = ctx.params.tol_mm
    edges = [(s, s.polygon.exterior.buffer(tol)) for s in shapes]
    for seg in ctx.segs:
        if ctx.claimed(seg.handles[0]):
            continue
        ls = LineString([seg.a, seg.b])
        for shape, ring in edges:
            if ring.contains(ls):
                ctx.claim(seg.handles, shape.column.id)
                shape.column.evidence.handles.extend(h for h in seg.handles if h not in shape.column.evidence.handles)
                break


def _polygon(f: Fill) -> Polygon | None:
    pts = list(f.polygon)
    if len(pts) >= 2 and math.dist(pts[0], pts[-1]) < 1e-6:
        pts = pts[:-1]
    if len(pts) < 4:
        return None
    poly = Polygon(pts)
    if not poly.is_valid:
        poly = poly.buffer(0)
    return poly if poly.geom_type == "Polygon" and poly.area > 0 else None


def _rect_of(poly: Polygon, tol: float) -> tuple[Point, float, float, float] | None:
    simple = poly.simplify(tol)
    coords = list(simple.exterior.coords)[:-1]
    return is_rectangle(coords, 2 * tol)


def _circle_of(poly: Polygon, tol: float) -> tuple[Point, float] | None:
    c = poly.centroid
    coords = list(poly.exterior.coords)[:-1]
    if len(coords) < 8:
        return None
    radii = [math.dist((c.x, c.y), q) for q in coords]
    r = sum(radii) / len(radii)
    if max(radii) - min(radii) > max(2 * tol, 0.02 * r):
        return None
    return (c.x, c.y), 2 * r


def _r(p: Point) -> Point:
    return (round(p[0], 2), round(p[1], 2))
