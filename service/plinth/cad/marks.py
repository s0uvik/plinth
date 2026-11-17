"""Section marks on plan sheets (PRD Step 9, T4): where each building section cuts and which way it looks.

A section mark is a labelled arrowhead ("A") at each end of a cut line. The line runs from one arrow's base, through
any jogs, to the other arrow's base; where the mark is drawn with tails (short legs from the cut out to the arrows),
the end legs run along the arrows' direction and are not part of the cut. The arrows point the way the section looks.
Arrows with no label beside them (stair arrows, leaders) are not section marks.

This is the one section-mark finder: plan recognition (T3) stores its marks in ``PlanRecognition.section_marks``
(``to_model``), and registration and cut lines (T4) read them back from there (``from_model``).
"""

from __future__ import annotations

import math
import re
from dataclasses import dataclass

from plinth.cad.annot import TOL, Arrow, Seg, arrowheads, flat, flatten, rect_distance, segments, text_box, texts
from plinth.cad import building
from plinth.cad.building import Evidence, Finding

LABEL_RE = re.compile(r"^[A-Z0-9]{1,3}$", re.I)
LABEL_REACH = 3.0  # a label lies within this many label heights (or arrow lengths) of its arrow's tip


@dataclass
class SectionMark:
    label: str
    path: list[tuple[float, float]]  # from one arrow base to the other, every corner
    cut: list[tuple[float, float]]  # the path without the tails along the arrows
    view: tuple[float, float]  # unit vector the section looks along
    handles: list[str]

    @property
    def straight(self) -> bool:
        return len(self.cut) == 2


def to_model(mark: SectionMark, sheet_name: str) -> building.SectionMark:
    """The contract's form (PlanRecognition.section_marks)."""
    r = lambda p: (round(p[0], 2), round(p[1], 2))  # noqa: E731
    return building.SectionMark(name=mark.label, points=[r(p) for p in mark.path], cut=[r(p) for p in mark.cut],
                                view=(round(mark.view[0], 6) + 0.0, round(mark.view[1], 6) + 0.0),
                                evidence=Evidence(sheet=sheet_name, handles=list(dict.fromkeys(mark.handles))))


def from_model(mark: building.SectionMark) -> SectionMark:
    """Back from the contract's form (a mark recorded without its cut or view keeps its drawn line and no view)."""
    path = [tuple(p) for p in mark.points]
    cut = [tuple(p) for p in mark.cut] if mark.cut else list(path)
    view = tuple(mark.view) if mark.view else (0.0, 0.0)
    return SectionMark(mark.name, path, cut, view, list(mark.evidence.handles))


def mark_label(section_name: str) -> str:
    """The letter on the plan for a section name: "AA" -> "A", "1-1" -> "1", "X" -> "X"."""
    name = re.sub(r"[\s\-]", "", section_name.upper())
    half = len(name) // 2
    if len(name) % 2 == 0 and name[:half] == name[half:]:
        return name[:half]
    return name


def find_marks(sheet_name: str, entities) -> tuple[list[SectionMark], list[Finding]]:
    ents = flatten(entities)
    segs = segments(ents)
    labels = [t for t in texts(ents) if LABEL_RE.match(flat(t.text))]
    findings: list[Finding] = []
    labelled: list[tuple[Arrow, str, str]] = []
    for arrow in arrowheads(ents):
        best = None
        for t in labels:
            reach = LABEL_REACH * max(t.height or 0.0, arrow.size)
            d = rect_distance(arrow.tip, text_box(t))
            if d <= reach and (best is None or d < best[0]):
                best = (d, t)
        if best:
            labelled.append((arrow, flat(best[1].text).upper(), best[1].handle))
    marks: list[SectionMark] = []
    used: set[str] = set()
    for arrow, label, text_handle in labelled:
        if arrow.handle in used:
            continue
        path, path_handles = _trace(arrow, segs)
        if len(path) < 2:
            continue
        end = [(a, lab, th) for a, lab, th in labelled
               if a.handle != arrow.handle and math.dist(a.base, path[-1]) <= max(TOL, a.size * 0.05)]
        if not end:
            findings.append(Finding(
                id=f"section-mark-open-{label}-{arrow.handle}", topic="section_mark",
                text=f"The section mark \"{label}\" at {arrow.handle} has a cut line that does not reach a second "
                     "arrow; its cut is taken as drawn up to its last point.",
                evidence=Evidence(sheet=sheet_name, handles=[arrow.handle, text_handle, *path_handles])))
            other = None
        else:
            other = end[0]
            used.add(other[0].handle)
            if other[1] != label:
                findings.append(Finding(
                    id=f"section-mark-labels-{arrow.handle}", topic="section_mark", severity="check",
                    text=f"A cut line joins arrows labelled \"{label}\" and \"{other[1]}\".",
                    evidence=Evidence(sheet=sheet_name, handles=[arrow.handle, other[0].handle])))
        used.add(arrow.handle)
        view = arrow.direction
        if other and abs(view[0] * other[0].direction[0] + view[1] * other[0].direction[1] - 1) > 1e-3:
            findings.append(Finding(
                id=f"section-mark-arrows-{label}", topic="section_mark", severity="check",
                text=f"The two arrows of section mark \"{label}\" point different ways; the first one's "
                     "direction is used.",
                evidence=Evidence(sheet=sheet_name, handles=[arrow.handle, other[0].handle])))
        cut = list(path)
        if len(cut) > 2 and _along(cut[0], cut[1], view):
            cut = cut[1:]
        if len(cut) > 2 and _along(cut[-2], cut[-1], view):
            cut = cut[:-1]
        handles = [arrow.handle, text_handle, *path_handles] + ([other[0].handle, other[2]] if other else [])
        marks.append(SectionMark(label, path, cut, view, handles))
    by_label: dict[str, list[SectionMark]] = {}
    for m in marks:
        by_label.setdefault(m.label, []).append(m)
    for label, group in by_label.items():
        if len(group) > 1:
            findings.append(Finding(
                id=f"section-mark-repeated-{label}", topic="section_mark", severity="check",
                text=f"{len(group)} separate cut lines are labelled \"{label}\" on this sheet.",
                evidence=Evidence(sheet=sheet_name, handles=[h for m in group for h in m.handles[:1]])))
    return marks, findings


def _along(a, b, direction) -> bool:
    length = math.dist(a, b)
    if length == 0:
        return False
    cross = ((b[0] - a[0]) * direction[1] - (b[1] - a[1]) * direction[0]) / length
    return abs(cross) < 1e-3


def _trace(arrow: Arrow, segs: list[Seg]) -> tuple[list[tuple[float, float]], list[str]]:
    """Follow the line that starts at the arrow's base, joint by joint, keeping the first segment's style."""
    tol = max(TOL, arrow.size * 0.05)
    start = [s for s in segs if min(math.dist(s.a, arrow.base), math.dist(s.b, arrow.base)) <= tol]
    if not start:
        return [], []
    first = min(start, key=lambda s: -s.length)
    style = first.style()
    pool = [s for s in segs if s.style() == style]
    path = [arrow.base]
    handles: list[str] = []
    current, used = first, set()
    point = arrow.base
    while current is not None:
        used.add(id(current))
        far = current.b if math.dist(current.a, point) <= tol else current.a
        path.append(far)
        if current.handle not in handles:
            handles.append(current.handle)
        point = far
        nxt = [s for s in pool if id(s) not in used and min(math.dist(s.a, point), math.dist(s.b, point)) <= tol]
        current = nxt[0] if len(nxt) == 1 else None
    # merge collinear runs (a polyline may repeat points or split a straight run)
    out = [path[0]]
    for p in path[1:]:
        if math.dist(p, out[-1]) <= 1e-9:
            continue
        if len(out) >= 2 and _collinear(out[-2], out[-1], p):
            out[-1] = p
        else:
            out.append(p)
    return out, handles


def _collinear(a, b, c) -> bool:
    cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0])
    return abs(cross) <= 1e-6 * max(math.dist(a, b) * math.dist(b, c), 1.0)
