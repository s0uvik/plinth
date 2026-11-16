"""Page border and title block of a sheet drawn in model space (PRD Step 9, stage 1).

The border is the largest axis-aligned rectangle (a closed polyline or four lines, also inside a title-block
insert) that holds nearly everything and covers most of the drawing's extent; the inner frame is the largest rectangle inside it with narrow margins.
The title is the underlined text nearest the "SCALE- 1:N" text; failing that the tallest underlined text, failing
that the tallest text. The page label is a text like "29 I Page" or "Page 29", anywhere on the sheet.
drawing_entities is everything whose centre lies inside the inner frame, less the border and those three texts;
the rest is listed in ``excluded`` with the reason. Geometry is never rotated: a plan drawn turned on the page
stays as drawn, and only the title's rotation is reported.
"""

from __future__ import annotations

import math
import re
from typing import Literal

from pydantic import BaseModel, ConfigDict

from b2m.cad.reader import CadEntity, CadSheet, Issue

Rect = tuple[float, float, float, float]  # xmin, ymin, xmax, ymax
BORDER_MIN_SHARE = 0.9  # the border holds at least this share of the top-level entities ...
BORDER_MIN_EXTENT = 0.5  # ... and covers at least this share of the box around everything drawn
INNER_MIN_AREA = 0.6  # the inner frame covers at least this share of the outer one ...
INNER_MAX_MARGIN = 0.1  # ... and no margin is wider than this share of the outer frame's shorter side
SCALE_RE = re.compile(r"\bSCALE\b\s*[-:=]?\s*(?:1\s*[:/]\s*([\d,]+(?:\.\d+)?)|(N\.?\s*T\.?\s*S\.?))", re.I)
PAGE_RE = re.compile(r"^\s*(?:(\d+)\s*[|Il]?\s*PAGE|PAGE\s*(?:NO\.?)?\s*[:\-]?\s*(\d+)(?:\s*OF\s*\d+)?)\s*$", re.I)

Reason = Literal["border", "title", "scale", "page", "margin", "outside_border"]


class Excluded(BaseModel):
    model_config = ConfigDict(extra="forbid")
    handle: str
    reason: Reason


class Sheet(BaseModel):
    model_config = ConfigDict(extra="forbid")
    cad: CadSheet
    outer: Rect | None = None
    inner: Rect | None = None
    title: str | None = None
    title_handle: str | None = None
    title_rotation: float | None = None
    title_basis: Literal["underlined_near_scale", "underlined_tallest", "tallest_text"] | None = None
    scale: float | None = None  # N of "1:N"
    scale_text: str | None = None
    page: str | None = None  # the label as printed, e.g. "29 I Page"
    page_number: int | None = None
    drawing_entities: list[CadEntity] = []
    excluded: list[Excluded] = []
    issues: list[Issue] = []


def detect(cad: CadSheet) -> Sheet:
    name = cad.source.replace("\\", "/").rsplit("/", 1)[-1]
    outer, inner, border_tops = _border(cad.entities)
    sheet = Sheet(cad=cad, outer=outer, inner=inner)
    issues = sheet.issues
    if outer is None:
        issues.append(Issue(topic="sheet", text=f"{name}: no page border found; every entity is treated as drawing."))

    texts = [e for e in cad.walk() if e.kind == "text" and e.text and e.text.strip()]
    scales = [(e, m) for e in texts if (m := SCALE_RE.search(_flat(e.text)))]
    pages = [(e, m) for e in texts if (m := PAGE_RE.match(_flat(e.text)))]
    special = {e.handle for e, _ in scales} | {e.handle for e, _ in pages}
    title, basis, scale_hit = _title([e for e in texts if e.handle not in special], scales, issues, name)

    marked: dict[str, Reason] = {h: "border" for h in border_tops}
    if title:
        sheet.title, sheet.title_handle, sheet.title_basis = _flat(title.text), title.handle, basis
        sheet.title_rotation = title.rotation
        marked.setdefault(_top(title.handle), "title")
    else:
        issues.append(Issue(topic="sheet", text=f"{name}: no title found on the sheet."))
    if scale_hit:
        entity, match = scale_hit
        sheet.scale_text = _flat(entity.text)
        sheet.scale = float(match.group(1).replace(",", "")) if match.group(1) else None
        if sheet.scale is None:
            issues.append(Issue(topic="scale", text=f"{name}: the sheet says it is not to scale "
                                                    f"({sheet.scale_text}).", handles=[entity.handle]))
        marked.setdefault(_top(entity.handle), "scale")
    else:
        issues.append(Issue(topic="scale", text=f"{name}: no scale text (\"SCALE 1:N\") found."))
    if pages:
        entity, match = pages[0]
        sheet.page, sheet.page_number = _flat(entity.text), int(match.group(1) or match.group(2))
        marked.setdefault(_top(entity.handle), "page")
        if len({int(m.group(1) or m.group(2)) for _, m in pages}) > 1:
            issues.append(Issue(topic="sheet", text=f"{name}: several page labels "
                                                    f"({', '.join(_flat(e.text) for e, _ in pages)}); took the first.",
                                handles=[e.handle for e, _ in pages]))

    for entity in cad.entities:
        reason = marked.get(entity.handle) or _place(entity, outer, inner)
        if reason:
            sheet.excluded.append(Excluded(handle=entity.handle, reason=reason))
        else:
            sheet.drawing_entities.append(entity)
    return sheet


def _flat(text: str) -> str:
    return " ".join(text.split())


def _top(handle: str) -> str:
    """The top-level entity a (possibly exploded) handle belongs to."""
    return handle.split(":", 1)[0]


def _title(candidates: list[CadEntity], scales: list, issues: list[Issue], name: str):
    underlined = [e for e in candidates if e.underlined]
    if underlined and scales:
        pairs = [(math.dist(u.points[0], s.points[0]), u, (s, m)) for u in underlined for s, m in scales]
        _, title, scale = min(pairs, key=lambda pair: pair[0])
        return title, "underlined_near_scale", scale
    scale = scales[0] if scales else None
    for pool, basis in ((underlined, "underlined_tallest"), (candidates, "tallest_text")):
        if not pool:
            continue
        tallest = max(e.height or 0 for e in pool)
        top = [e for e in pool if (e.height or 0) == tallest]
        if len({_flat(e.text) for e in top}) == 1:
            return top[0], basis, scale
        issues.append(Issue(topic="sheet", text=f"{name}: several texts could be the title: "
                                                + "; ".join(_flat(e.text) for e in top),
                            handles=[e.handle for e in top]))
        return None, None, scale
    return None, None, scale


def _place(entity: CadEntity, outer: Rect | None, inner: Rect | None) -> Reason | None:
    box = entity.bounds()
    if outer is None or box is None:
        return None
    centre = ((box[0] + box[2]) / 2, (box[1] + box[3]) / 2)
    if not _inside(centre, outer):
        return "outside_border"
    if not _inside(centre, inner or outer):
        return "margin"
    return None


def _inside(point: tuple[float, float], rect: Rect) -> bool:
    return rect[0] <= point[0] <= rect[2] and rect[1] <= point[1] <= rect[3]


def _border(entities: list[CadEntity]) -> tuple[Rect | None, Rect | None, set[str]]:
    """Outer and inner frame, and the handles of the top-level entities that draw them."""
    rects = _rectangles(entities)
    if not rects:
        return None, None, set()
    boxes = [b for e in entities if (b := e.bounds())]
    centres = [((b[0] + b[2]) / 2, (b[1] + b[3]) / 2) for b in boxes]
    extents = (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))
    outer = None
    for rect, handles in sorted(rects, key=lambda r: -_area(r[0])):
        if _area(rect) < BORDER_MIN_EXTENT * _area(extents):
            break
        if sum(_inside(c, _grow(rect)) for c in centres) >= BORDER_MIN_SHARE * len(centres):
            outer = (rect, handles)
            break
    if outer is None:
        return None, None, set()
    (x0, y0, x1, y1), tops = outer[0], {_top(h) for h in outer[1]}
    inner = None
    for rect, handles in sorted(rects, key=lambda r: -_area(r[0])):
        margins = (rect[0] - x0, rect[1] - y0, x1 - rect[2], y1 - rect[3])
        if (min(margins) > 0 and _area(rect) >= INNER_MIN_AREA * _area(outer[0])
                and max(margins) <= INNER_MAX_MARGIN * min(x1 - x0, y1 - y0)):
            inner = rect
            tops |= {_top(h) for h in handles}
            break
    return outer[0], inner, tops


def _area(rect: Rect) -> float:
    return (rect[2] - rect[0]) * (rect[3] - rect[1])


def _grow(rect: Rect, by: float = 1.0) -> Rect:
    return (rect[0] - by, rect[1] - by, rect[2] + by, rect[3] + by)


def _rectangles(entities: list[CadEntity]) -> list[tuple[Rect, list[str]]]:
    """Axis-aligned rectangles drawn as one closed polyline or as four lines (inserts' children included)."""
    found: list[tuple[Rect, list[str]]] = []
    lines: list[CadEntity] = []
    for entity in (e for top in entities for e in top.walk()):
        if entity.kind == "polyline" and not any(entity.bulges or []):
            rect = _as_rect(_unclosed(entity.points))
            if rect and (entity.closed or len(entity.points) == 5):
                found.append((rect, [entity.handle]))
        elif entity.kind == "line":
            lines.append(entity)
    found.extend(_line_rectangles(lines))
    return found


def _unclosed(points: list) -> list:
    return points[:-1] if len(points) == 5 and math.dist(points[0], points[-1]) < 1e-6 else points


def _as_rect(points: list) -> Rect | None:
    if len(points) != 4:
        return None
    xs, ys = sorted({round(p[0], 3) for p in points}), sorted({round(p[1], 3) for p in points})
    if len(xs) != 2 or len(ys) != 2:
        return None
    # Consecutive corners must share an x or a y (rules out a bow-tie through the same four corners).
    for a, b in zip(points, points[1:] + points[:1]):
        if round(a[0], 3) != round(b[0], 3) and round(a[1], 3) != round(b[1], 3):
            return None
    return (xs[0], ys[0], xs[1], ys[1])


def _line_rectangles(lines: list[CadEntity]) -> list[tuple[Rect, list[str]]]:
    """Rectangles whose four sides are separate lines; only the long lines are tried, as a border is large."""
    if not lines:
        return []
    xs = [p[0] for e in lines for p in e.points]
    ys = [p[1] for e in lines for p in e.points]
    span = min(max(xs) - min(xs), max(ys) - min(ys))
    horizontal, vertical = {}, {}
    for e in lines:
        (ax, ay), (bx, by) = e.points
        if abs(ay - by) < 1e-3 and abs(ax - bx) >= 0.5 * span:
            horizontal[(round(min(ax, bx), 3), round(max(ax, bx), 3), round(ay, 3))] = e.handle
        elif abs(ax - bx) < 1e-3 and abs(ay - by) >= 0.5 * span:
            vertical[(round(ax, 3), round(min(ay, by), 3), round(max(ay, by), 3))] = e.handle
    found = []
    for (x0, x1, ya), bottom in horizontal.items():
        for (hx0, hx1, yb), top in horizontal.items():
            if (hx0, hx1) != (x0, x1) or yb <= ya:
                continue
            left, right = vertical.get((x0, ya, yb)), vertical.get((x1, ya, yb))
            if left and right:
                found.append(((x0, ya, x1, yb), [bottom, top, left, right]))
    return found
