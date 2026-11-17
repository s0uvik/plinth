"""Beam layout recognition (PRD Step 9, T4): beams, grids and column points from a beam (tie beam) layout sheet.

- Grids are the centre lines (centre linetype or a centre/grid/axis layer); collinear pieces are one grid line.
- A beam is drawn as two edge lines either side of a centre line: along each centre line, wherever the nearest
  edge on each side is at the same distance, there is beam, as wide as the edges are apart. Pieces of the same width
  joined across a crossing centre line (where the edges stop for the crossing beam) are one beam, and a beam that
  stops just short of a crossing centre line runs to it.
- Column points are the drawing's POINT entities.
- Depth is never read from a plan: it is None unless a dimension on the sheet gives it, and a question otherwise.
"""

from __future__ import annotations

import math
from collections import Counter

from plinth.cad import marks as section_marks
from plinth.cad.annot import TOL, Seg, flatten, measures, name_of, segments
from plinth.cad.building import Beam, BeamLayoutRecognition, Evidence, Finding, Grid
from plinth.cad.sheet import Sheet


def recognise_beam_layout(sheet: Sheet, storey: int | None = None) -> BeamLayoutRecognition:
    src = name_of(sheet)
    ents = flatten(sheet.drawing_entities)
    marks, mark_findings = section_marks.find_marks(src, sheet.drawing_entities)
    mark_handles = {h for m in marks for h in m.handles}
    segs = [s for s in segments(ents) if s.handle not in mark_handles]
    centre = [s for s in segs if s.centre_styled()]
    edges = [s for s in segs if not s.centre_styled()]
    rec = BeamLayoutRecognition(sheet=src, storey=storey, findings=list(mark_findings))

    axes = _axes(centre)
    for i, (direction, offset, pieces) in enumerate(axes, 1):
        start, end = _ends(direction, offset, pieces)
        rec.grids.append(Grid(id=f"G{i}", start=start, end=end,
                              evidence=Evidence(sheet=src, handles=sorted({s.handle for s in pieces}))))
    if rec.grids:
        rec.findings.append(Finding(id="beam-layout-grid-names", topic="grid", severity="info",
                                    text=f"{len(rec.grids)} centre lines are taken as grid lines; none is named "
                                         "(no grid bubbles on the sheet).",
                                    evidence=Evidence(sheet=src, handles=[g.evidence.handles[0] for g in rec.grids])))

    used: set[str] = set()
    for direction, offset, pieces in axes:
        crossings = [_cross(direction, offset, d2, o2) for d2, o2, _ in axes if abs(_dot(direction, d2)) < 1e-6]
        crossings = [c for c in crossings if c is not None]
        for t0, t1, width, handles in _beams_along(direction, offset, pieces, edges, crossings):
            p0 = _at(direction, offset, t0)
            p1 = _at(direction, offset, t1)
            used |= set(handles)
            rec.beams.append(Beam(id=f"B{len(rec.beams) + 1}", start=_r(p0), end=_r(p1), width_mm=round(width, 1),
                                  evidence=Evidence(sheet=src, handles=sorted(set(handles)))))
    if rec.beams:
        widths = sorted({b.width_mm for b in rec.beams})
        rec.findings.append(Finding(
            id="beam-depth-missing", topic="beam_depth", severity="check",
            text=f"{len(rec.beams)} beams ({', '.join(f'{w:g}' for w in widths)} mm wide) are drawn; the sheet does "
                 "not give their depth.", evidence=Evidence(sheet=src, handles=sorted({h for b in rec.beams for h in b.evidence.handles}))))
    else:
        rec.findings.append(Finding(id="beam-none", topic="beam", severity="blocking",
                                    text="No beam (two edge lines along a centre line) was found on the sheet.",
                                    evidence=Evidence(sheet=src)))
    for s in edges:  # end caps and junction lines lie inside a beam's outline
        if s.handle not in used and any(_in_beam(s.a, b) and _in_beam(s.b, b) for b in rec.beams):
            used.add(s.handle)
    unclaimed = [s for s in edges if s.handle not in used]
    for s in unclaimed:
        rec.unexplained.append(Evidence(sheet=src, handles=[s.handle], note="line not part of a beam"))

    layers: Counter = Counter()
    for e in ents:
        if e.kind == "point":
            rec.column_points.append(_r(e.points[0]))
            rec.column_evidence.append(Evidence(sheet=src, handles=[e.handle], note=f"layer {e.layer}"))
            layers[e.layer] += 1
    if len(layers) > 1:
        rec.findings.append(Finding(
            id="beam-layout-point-layers", topic="column", severity="check",
            text="Column points are drawn on " + ", ".join(f"{k} ({v})" for k, v in sorted(layers.items()))
                 + "; the sheet does not say what tells them apart (column type, size?).",
            evidence=Evidence(sheet=src, handles=[ev.handles[0] for ev in rec.column_evidence][:3]),
            options=["different column types", "different column sizes", "no meaning"]))
    off = [ev.handles[0] for p, ev in zip(rec.column_points, rec.column_evidence)
           if not any(_on_axis(p, d, o, TOL) for d, o, _ in axes)]
    if axes and off:
        rec.findings.append(Finding(id="beam-layout-points-off-grid", topic="column", severity="check",
                                    text=f"{len(off)} column points do not lie on any centre line.",
                                    evidence=Evidence(sheet=src, handles=off)))

    for m in measures(ents):
        ends = (m.p1, m.p2)
        on_grid = all(any(_on_axis(p, d, o, TOL) for d, o, _ in axes) for p in ends)
        if not on_grid:
            rec.unexplained.append(Evidence(sheet=src, handles=[m.handle],
                                            note=f"{m.text}: does not run between centre lines"))
    return rec


# ---------------------------------------------------------------- geometry along axes

def _direction(s: Seg) -> tuple[float, float]:
    dx, dy = s.b[0] - s.a[0], s.b[1] - s.a[1]
    length = math.hypot(dx, dy)
    d = (dx / length, dy / length)
    if d[0] < -1e-9 or (abs(d[0]) <= 1e-9 and d[1] < 0):
        d = (-d[0], -d[1])
    return (round(d[0], 6), round(d[1], 6))


def _dot(a, b) -> float:
    return a[0] * b[0] + a[1] * b[1]


def _normal(d) -> tuple[float, float]:
    return (-d[1], d[0])


def _offset(d, p) -> float:
    return _dot(_normal(d), p)


def _along(d, p) -> float:
    return _dot(d, p)


def _at(d, offset, t) -> tuple[float, float]:
    n = _normal(d)
    return (n[0] * offset + d[0] * t, n[1] * offset + d[1] * t)


def _on_axis(p, d, offset, tol) -> bool:
    return abs(_offset(d, p) - offset) <= tol


def _cross(d1, o1, d2, o2) -> float | None:
    """Where axis 2 crosses axis 1, as a position along axis 1."""
    n1, n2 = _normal(d1), _normal(d2)
    det = n1[0] * n2[1] - n1[1] * n2[0]
    if abs(det) < 1e-9:
        return None
    x = (o1 * n2[1] - o2 * n1[1]) / det
    y = (n1[0] * o2 - n2[0] * o1) / det
    return _along(d1, (x, y))


def _axes(centre: list[Seg]) -> list[tuple[tuple[float, float], float, list[Seg]]]:
    """Centre lines grouped into axes: same direction and offset."""
    out: list[tuple[tuple[float, float], float, list[Seg]]] = []
    for s in centre:
        d = _direction(s)
        o = _offset(d, s.a)
        for i, (d2, o2, pieces) in enumerate(out):
            if d2 == d and abs(o2 - o) <= TOL:
                pieces.append(s)
                break
        else:
            out.append((d, o, [s]))
    out.sort(key=lambda a: (a[0], a[1]))
    return out


def _ends(d, offset, pieces) -> tuple[tuple[float, float], tuple[float, float]]:
    ts = [_along(d, p) for s in pieces for p in (s.a, s.b)]
    return _r(_at(d, offset, min(ts))), _r(_at(d, offset, max(ts)))


def _beams_along(d, offset, pieces, edges, crossings) -> list[tuple[float, float, float, list[str]]]:
    """Beam runs along one centre line: (start, end, width, edge handles)."""
    parallel = []
    for s in edges:
        if _direction(s) != d:
            continue
        o = _offset(d, s.a)
        t0, t1 = sorted((_along(d, s.a), _along(d, s.b)))
        parallel.append((o - offset, t0, t1, s.handle))
    out = []
    for piece in pieces:
        l0, l1 = sorted((_along(d, piece.a), _along(d, piece.b)))
        cuts = sorted({l0, l1} | {t for _, t0, t1, _ in parallel for t in (t0, t1) if l0 < t < l1})
        spans: list[tuple[float, float, float | None, list[str]]] = []
        for a, b in zip(cuts, cuts[1:]):
            mid = (a + b) / 2
            covering = [(o, h) for o, t0, t1, h in parallel if t0 - TOL <= mid <= t1 + TOL and abs(o) > TOL]
            below = max(((o, h) for o, h in covering if o < 0), default=None)
            above = min(((o, h) for o, h in covering if o > 0), default=None)
            width = None
            handles: list[str] = []
            junction = any(a - TOL <= c <= b + TOL for c in crossings)  # inside a crossing beam
            if below and above and abs(above[0] + below[0]) <= TOL and not junction:
                width = above[0] - below[0]
                handles = [below[1], above[1]]
            spans.append((a, b, width, handles))
        out += _runs(spans, crossings, l0, l1)
    return out


def _runs(spans, crossings, l0, l1) -> list[tuple[float, float, float, list[str]]]:
    """Join beam spans of one width across gaps that a crossing centre line passes through."""
    runs: list[list] = []
    current = None
    gap: list = []
    for a, b, width, handles in spans:
        if width is None:
            gap.append((a, b))
            continue
        if current and abs(current[2] - width) <= TOL and gap and _crossed(gap, crossings):
            current[1] = b
            current[3] += handles
        elif current and abs(current[2] - width) <= TOL and not gap:
            current[1] = b
            current[3] += handles
        else:
            if current:
                runs.append(current)
            current = [a, b, width, list(handles)]
            current.append(gap)  # the gap before this run
        gap = []
    if current:
        runs.append(current)
    out = []
    for i, run in enumerate(runs):
        start, end, width, handles = run[0], run[1], run[2], run[3]
        before = [c for c in crossings if (runs[i - 1][1] if i else l0) - TOL <= c <= start + TOL]
        after = [c for c in crossings if end - TOL <= c <= (runs[i + 1][0] if i + 1 < len(runs) else l1) + TOL]
        if before:
            start = max(before) if max(before) >= start - width else start
        if after:
            end = min(after) if min(after) <= end + width else end
        out.append((start, end, width, handles))
    return out


def _crossed(gap, crossings) -> bool:
    lo, hi = min(a for a, _ in gap), max(b for _, b in gap)
    return any(lo - TOL <= c <= hi + TOL for c in crossings)


def _in_beam(p, beam: Beam) -> bool:
    length = math.dist(beam.start, beam.end)
    d = ((beam.end[0] - beam.start[0]) / length, (beam.end[1] - beam.start[1]) / length)
    v = (p[0] - beam.start[0], p[1] - beam.start[1])
    t, n = _dot(v, d), _dot(v, _normal(d))
    half = (beam.width_mm or 0.0) / 2
    return -half - TOL <= t <= length + half + TOL and abs(n) <= half + TOL


def _r(p) -> tuple[float, float]:
    return (round(p[0], 1), round(p[1], 1))
