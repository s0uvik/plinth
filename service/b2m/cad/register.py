"""Cross-sheet registration (PRD Step 9, T4): every sheet's drawing coordinates into one building frame.

The frame is the typical floor plan's coordinates (the floor plan covering most storeys; else the lowest floor plan).
Each other sheet is matched to it by features both draw: column centres (with their size), grid and centre lines,
grid nodes (column points on a beam layout), outline corners and section-mark ends (with their letter).

For each quarter turn, candidate translations come from pairs of like features (and from 1-D alignment of the
lines); the candidate matching the most features wins and is refined by least squares (a small rotation and the
translation) on the matched pairs. The residual is the worst mismatch left; above the tolerance it is a finding.
When another transform matches as many features, the sheet is symmetric: if the sheet's whole content lands in the
same place either way the choice cannot matter (said, not asked); otherwise it is a question.

Sections register vertically: elevation 0 is the datum (PL, else GL); a section without level marks takes the
datum of a section whose storey levels it repeats. Section cut lines come from the marks on the plans, moved into
the frame; marks of one section on several plans are compared.

Plan features come from ``PlanRecognition`` (T3): columns, grids, wall axes, the outline and the section marks
(``PlanRecognition.section_marks``, found by ``b2m.cad.marks`` for every sheet). ``provisional_plan`` is the
fallback for a plan T3 cannot recognise: its simplest features (solid filled rectangles as columns, centre lines as
grids, the section marks) so the sheet can still be placed.
"""

from __future__ import annotations

import math
from collections import Counter
from dataclasses import dataclass, field

from b2m.cad import marks as section_marks
from b2m.cad.annot import TOL, flatten, name_of, segments, solid_boxes
from b2m.cad.building import (BeamLayoutRecognition, Column, Evidence, Finding, FoundationRecognition, Grid,
                              PlanRecognition, Registration, SectionRecognition)
from b2m.cad.building import SectionMark as SectionMarkModel
from b2m.cad.sheet import Sheet

REGISTER_TOL = 5.0  # mm: matched features must coincide this well (PRD gate: 1 mm, reported as residual)
MIN_MATCHES = 3  # fewer matched features than this cannot fix a sheet's position
TOP_CANDIDATES = 12

Point = tuple[float, float]


# ---------------------------------------------------------------- features

@dataclass
class FPoint:
    kind: str  # "column", "node", "corner", "mark"
    label: str | None
    xy: Point
    handles: list[str]


@dataclass
class FLine:
    kind: str  # "axis" (grid, centre line, wall or beam axis)
    a: Point
    b: Point
    handles: list[str]


@dataclass
class Features:
    sheet: str
    points: list[FPoint] = field(default_factory=list)
    lines: list[FLine] = field(default_factory=list)

    def __len__(self) -> int:
        return len(self.points) + len(self.lines)


def _size_label(w: float, d: float) -> str:
    a, b = sorted((round(w), round(d)))
    return f"{a}x{b}"


def plan_features(plan: PlanRecognition, marks: list[section_marks.SectionMark] | None = None) -> Features:
    """The adapter T3's output plugs into: columns, grids (and their nodes), wall axes, outline corners, marks
    (the plan's own section marks unless others are given)."""
    f = Features(plan.sheet)
    for c in plan.columns:
        f.points.append(FPoint("column", _size_label(c.width_mm, c.depth_mm), c.center, c.evidence.handles))
    for g in plan.grids:
        f.lines.append(FLine("axis", g.start, g.end, g.evidence.handles))
    for w in plan.walls:
        f.lines.append(FLine("axis", w.start, w.end, w.evidence.handles))
    f.points += _nodes([ln for ln in f.lines], plan.grids)
    for p in plan.outline or []:
        f.points.append(FPoint("corner", None, p, []))
    _add_marks(f, marks if marks is not None else [section_marks.from_model(m) for m in plan.section_marks])
    return f


def beam_layout_features(layout: BeamLayoutRecognition) -> Features:
    f = Features(layout.sheet)
    for g in layout.grids:
        f.lines.append(FLine("axis", g.start, g.end, g.evidence.handles))
    for p, ev in zip(layout.column_points, layout.column_evidence or [None] * len(layout.column_points)):
        f.points.append(FPoint("node", None, p, ev.handles if ev else []))
    return f


def beam_layout_content(layout: BeamLayoutRecognition) -> Features:
    """Everything the sheet says, for the symmetry test: features plus the beams themselves."""
    f = beam_layout_features(layout)
    for b in layout.beams:
        f.lines.append(FLine(f"beam {b.width_mm}", b.start, b.end, b.evidence.handles))
    return f


def foundation_features(found: FoundationRecognition) -> Features:
    """Columns as drawn on the footing plan. The sheet's own section marks are not used: they belong to the
    footing sections on that sheet, not (necessarily) to the building's sections."""
    f = Features(found.sheet)
    for c in found.plan_columns:
        f.points.append(FPoint("column", _size_label(c.width_mm, c.depth_mm), c.center, c.evidence.handles))
    return f


def _add_marks(f: Features, marks) -> None:
    for m in marks:
        for p in (m.path[0], m.path[-1]):
            f.points.append(FPoint("mark", m.label, p, m.handles[:2]))


def _nodes(lines: list[FLine], grids: list[Grid]) -> list[FPoint]:
    """Where grid lines cross (within both lines' extents)."""
    out = []
    gl = [FLine("axis", g.start, g.end, g.evidence.handles) for g in grids]
    for i, a in enumerate(gl):
        for b in gl[i + 1:]:
            p = _intersect(a, b)
            if p:
                out.append(FPoint("node", None, p, a.handles[:1] + b.handles[:1]))
    return out


def _intersect(a: FLine, b: FLine) -> Point | None:
    d1 = (a.b[0] - a.a[0], a.b[1] - a.a[1])
    d2 = (b.b[0] - b.a[0], b.b[1] - b.a[1])
    det = d1[0] * d2[1] - d1[1] * d2[0]
    if abs(det) < 1e-9:
        return None
    t = ((b.a[0] - a.a[0]) * d2[1] - (b.a[1] - a.a[1]) * d2[0]) / det
    u = ((b.a[0] - a.a[0]) * d1[1] - (b.a[1] - a.a[1]) * d1[0]) / det
    if -1e-6 <= t <= 1 + 1e-6 and -1e-6 <= u <= 1 + 1e-6:
        return (a.a[0] + t * d1[0], a.a[1] + t * d1[1])
    return None


def provisional_plan(sheet: Sheet, role: str = "floor_plan", storeys: list[int] | None = None
                     ) -> tuple[PlanRecognition, list[section_marks.SectionMark]]:
    """A stand-in for T3's plan recognition with only what registration needs: columns (solid filled rectangles
    drawn directly on the sheet, not inside blocks) and grids (centre lines that are not section marks)."""
    src = name_of(sheet)
    marks, findings = section_marks.find_marks(src, sheet.drawing_entities)
    taken = {h for m in marks for h in m.handles}
    top = [e for e in sheet.drawing_entities]
    columns = []
    for n, box in enumerate(solid_boxes(e for e in flatten(top) if e.parent is None), 1):
        columns.append(Column(id=f"P{n}", center=box.center, width_mm=box.width, depth_mm=box.height,
                              evidence=Evidence(sheet=src, handles=box.handles)))
    grids = []
    for s in segments(flatten(top)):
        if s.centre_styled() and s.handle not in taken:
            grids.append(Grid(id=f"L{len(grids) + 1}", start=s.a, end=s.b, evidence=Evidence(sheet=src, handles=[s.handle])))
    plan = PlanRecognition(sheet=src, role=role if role in ("floor_plan", "roof_plan") else "floor_plan",
                           storeys=storeys or [], columns=columns, grids=grids, findings=findings,
                           section_marks=[section_marks.to_model(m, src) for m in marks])
    return plan, marks


# ---------------------------------------------------------------- transforms

@dataclass
class Transform:
    quarter: int  # quarter turns counter-clockwise
    fine_deg: float
    t: Point

    @property
    def degrees(self) -> float:
        return self.quarter * 90.0 + self.fine_deg

    def apply(self, p: Point) -> Point:
        r = math.radians(self.degrees)
        c, s = math.cos(r), math.sin(r)
        return (c * p[0] - s * p[1] + self.t[0], s * p[0] + c * p[1] + self.t[1])


def _quarter(p: Point, k: int) -> Point:
    x, y = p
    for _ in range(k % 4):
        x, y = -y, x
    return (x, y)


def _orientation(line: FLine, tol: float = 1e-6) -> str | None:
    dx, dy = line.b[0] - line.a[0], line.b[1] - line.a[1]
    length = math.hypot(dx, dy)
    if length == 0:
        return None
    if abs(dy) / length < tol:
        return "h"
    if abs(dx) / length < tol:
        return "v"
    return None


@dataclass
class Match:
    transform: Transform
    points: list[tuple[FPoint, FPoint]]
    lines: list[tuple[FLine, FLine]]
    residual: float = 0.0

    @property
    def score(self) -> int:
        return len(self.points) + len(self.lines)


def _compatible(a: FPoint, b: FPoint) -> bool:
    return a.kind == b.kind and a.label == b.label


def _evaluate(ref: Features, mov: Features, tr: Transform, tol: float) -> Match:
    pts = []
    by_kind: dict[tuple, list[FPoint]] = {}
    for r in ref.points:
        by_kind.setdefault((r.kind, r.label), []).append(r)
    for m in mov.points:
        p = tr.apply(m.xy)
        cands = by_kind.get((m.kind, m.label), [])
        best = min(cands, key=lambda r: math.dist(r.xy, p), default=None)
        if best is not None and math.dist(best.xy, p) <= tol:
            pts.append((m, best))
    lines = []
    ref_axes = [(r, _orientation(r)) for r in ref.lines if r.kind == "axis"]
    for m in mov.lines:
        if m.kind != "axis":
            continue
        a, b = tr.apply(m.a), tr.apply(m.b)
        o = _orientation(FLine("axis", a, b, []), 1e-3)
        if o is None:
            continue
        i = 0 if o == "v" else 1
        c = (a[i] + b[i]) / 2
        best = min(((abs(((r.a[i] + r.b[i]) / 2) - c), r) for r, ro in ref_axes if ro == o),
                   key=lambda x: x[0], default=None)
        if best is not None and best[0] <= tol:
            lines.append((m, best[1]))
    return Match(tr, pts, lines)


def _candidates(ref: Features, mov: Features, k: int, tol: float) -> list[Point]:
    votes: Counter = Counter()
    by_kind: dict[tuple, list[FPoint]] = {}
    for r in ref.points:
        by_kind.setdefault((r.kind, r.label), []).append(r)
    cell = max(tol, 1e-6)
    for m in mov.points:
        p = _quarter(m.xy, k)
        for r in by_kind.get((m.kind, m.label), []):
            votes[(round((r.xy[0] - p[0]) / cell), round((r.xy[1] - p[1]) / cell))] += 1
    out = [(kx * cell, ky * cell) for (kx, ky), _ in votes.most_common(TOP_CANDIDATES)]
    xs, ys = Counter(), Counter()
    rv = [(r.a[0] + r.b[0]) / 2 for r in ref.lines if r.kind == "axis" and _orientation(r) == "v"]
    rh = [(r.a[1] + r.b[1]) / 2 for r in ref.lines if r.kind == "axis" and _orientation(r) == "h"]
    for m in mov.lines:
        if m.kind != "axis":
            continue
        a, b = _quarter(m.a, k), _quarter(m.b, k)
        o = _orientation(FLine("axis", a, b, []))
        if o == "v":
            for x in rv:
                xs[round((x - (a[0] + b[0]) / 2) / cell)] += 1
        elif o == "h":
            for y in rh:
                ys[round((y - (a[1] + b[1]) / 2) / cell)] += 1
    for kx, _ in xs.most_common(5):
        for ky, _ in ys.most_common(5):
            out.append((kx * cell, ky * cell))
    if xs and not ys:
        out += [(kx * cell, ky * cell) for kx, _ in xs.most_common(3) for ky in {round(c[1] / cell) for c in out[:3]}]
    return out


def _refine(match: Match) -> Match:
    """Least squares for a small extra rotation and the translation, on the matched pairs (after the quarter turn)."""
    k = match.transform.quarter
    rows: list[tuple[float, float, float, float]] = []  # coefficient of (phi, tx, ty) and target
    for m, r in match.points:
        p = _quarter(m.xy, k)
        rows.append((-p[1], 1.0, 0.0, r.xy[0] - p[0]))
        rows.append((p[0], 0.0, 1.0, r.xy[1] - p[1]))
    for m, r in match.lines:
        a, b = _quarter(m.a, k), _quarter(m.b, k)
        mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
        if _orientation(r) == "v":
            rows.append((-mid[1], 1.0, 0.0, (r.a[0] + r.b[0]) / 2 - mid[0]))
        else:
            rows.append((mid[0], 0.0, 1.0, (r.a[1] + r.b[1]) / 2 - mid[1]))
    if not rows:
        return match
    # a rotation needs two separate constraints on each axis or two points; otherwise keep it at zero
    distinct = len({(round(m.xy[0]), round(m.xy[1])) for m, _ in match.points})
    has_x = sum(1 for row in rows if row[1]) >= 2
    has_y = sum(1 for row in rows if row[2]) >= 2
    solve_phi = distinct >= 2 or (has_x and has_y and len(match.lines) >= 3)
    sol = _lstsq(rows, solve_phi)
    phi, tx, ty = sol
    tr = Transform(k, math.degrees(phi), (tx, ty))
    # exact translation for the final rotation (the linearised one is off by second-order terms)
    if match.points:
        dx = [r.xy[0] - _rot(m.xy, tr.degrees)[0] for m, r in match.points]
        dy = [r.xy[1] - _rot(m.xy, tr.degrees)[1] for m, r in match.points]
        tr.t = (sum(dx) / len(dx), sum(dy) / len(dy))
    return Match(tr, match.points, match.lines)


def _rot(p: Point, deg: float) -> Point:
    r = math.radians(deg)
    return (math.cos(r) * p[0] - math.sin(r) * p[1], math.sin(r) * p[0] + math.cos(r) * p[1])


def _lstsq(rows, solve_phi: bool) -> tuple[float, float, float]:
    cols = [0, 1, 2] if solve_phi else [1, 2]
    n = len(cols)
    ata = [[sum(row[cols[i]] * row[cols[j]] for row in rows) for j in range(n)] for i in range(n)]
    atb = [sum(row[cols[i]] * row[3] for row in rows) for i in range(n)]
    for i in range(n):  # an unconstrained translation component stays 0
        if ata[i][i] == 0:
            ata[i][i] = 1.0
    x = _solve(ata, atb)
    out = [0.0, 0.0, 0.0]
    for i, c in enumerate(cols):
        out[c] = x[i]
    return out[0], out[1], out[2]


def _solve(a: list[list[float]], b: list[float]) -> list[float]:
    n = len(b)
    m = [row[:] + [b[i]] for i, row in enumerate(a)]
    for i in range(n):
        p = max(range(i, n), key=lambda r: abs(m[r][i]))
        m[i], m[p] = m[p], m[i]
        if abs(m[i][i]) < 1e-12:
            continue
        for r in range(n):
            if r != i:
                f = m[r][i] / m[i][i]
                for c in range(i, n + 1):
                    m[r][c] -= f * m[i][c]
    return [m[i][n] / m[i][i] if abs(m[i][i]) > 1e-12 else 0.0 for i in range(n)]


def _residual(match: Match) -> float:
    tr = match.transform
    worst = 0.0
    for m, r in match.points:
        worst = max(worst, math.dist(tr.apply(m.xy), r.xy))
    for m, r in match.lines:
        a, b = tr.apply(m.a), tr.apply(m.b)
        i = 0 if _orientation(r) == "v" else 1
        c = (r.a[i] + r.b[i]) / 2
        worst = max(worst, abs(a[i] - c), abs(b[i] - c))
    return worst


def best_match(ref: Features, mov: Features, tol: float = REGISTER_TOL) -> tuple[Match | None, list[Match]]:
    """The best transform and the others that match as many features."""
    matches: list[Match] = []
    for k in range(4):
        seen = set()
        for t in _candidates(ref, mov, k, tol):
            key = (round(t[0] / tol), round(t[1] / tol))
            if key in seen:
                continue
            seen.add(key)
            m = _evaluate(ref, mov, Transform(k, 0.0, t), tol)
            if m.score:
                matches.append(m)
    if not matches:
        return None, []
    top = max(m.score for m in matches)
    best_ones = [m for m in matches if m.score == top]
    refined = []
    for m in best_ones:
        r = _refine(m)
        r = _evaluate(ref, mov, r.transform, tol)
        r = _refine(r)
        r.residual = _residual(r)
        refined.append(r)
    refined.sort(key=lambda m: (-m.score, m.residual))
    best = refined[0]
    others = []
    for m in refined[1:]:
        if m.transform.quarter != best.transform.quarter or math.dist(m.transform.t, best.transform.t) > tol:
            if all(o.transform.quarter != m.transform.quarter or math.dist(o.transform.t, m.transform.t) > tol
                   for o in others):
                others.append(m)
    return best, others


def _same_content(content: Features, a: Transform, b: Transform, tol: float) -> bool:
    """The sheet's content lands in the same place under both transforms."""
    pa = [(p.kind, p.label, a.apply(p.xy)) for p in content.points]
    pb = [(p.kind, p.label, b.apply(p.xy)) for p in content.points]
    for kind, label, p in pa:
        if not any(k2 == kind and l2 == label and math.dist(p, q) <= tol for k2, l2, q in pb):
            return False
    la = [(ln.kind, a.apply(ln.a), a.apply(ln.b)) for ln in content.lines]
    lb = [(ln.kind, b.apply(ln.a), b.apply(ln.b)) for ln in content.lines]
    for kind, p, q in la:
        if not any(k2 == kind and ((math.dist(p, r) <= tol and math.dist(q, s) <= tol)
                                   or (math.dist(p, s) <= tol and math.dist(q, r) <= tol)) for k2, r, s in lb):
            return False
    return True


def register(ref: Features, mov: Features, content: Features | None = None, tol: float = REGISTER_TOL
             ) -> tuple[Registration | None, list[Finding]]:
    """Register one sheet (mov) into the frame of ref."""
    findings: list[Finding] = []
    best, others = best_match(ref, mov, tol)
    if best is None or best.score < MIN_MATCHES:
        findings.append(Finding(
            id=f"register-failed-{mov.sheet}", topic="registration", severity="blocking",
            text=f"{mov.sheet} could not be placed on {ref.sheet}: "
                 f"{best.score if best else 0} features match (at least {MIN_MATCHES} are needed).",
            evidence=Evidence(sheet=mov.sheet)))
        return None, findings
    kinds = Counter(m.kind for m, _ in best.points) + Counter("lines" for _ in best.lines)
    method = ", ".join(f"{v} {k}" for k, v in sorted(kinds.items()))
    handles = sorted({h for m, _ in best.points for h in m.handles} | {h for m, _ in best.lines for h in m.handles})
    reg = Registration(sheet=mov.sheet, rotation_deg=_z(round(best.transform.degrees, 6)),
                       translation=(round(best.transform.t[0], 3), round(best.transform.t[1], 3)),
                       method=f"matched {method} with {ref.sheet}", residual_mm=round(best.residual, 3),
                       evidence=Evidence(sheet=mov.sheet, handles=handles,
                                         note=f"{best.score} of {len(mov)} features matched"))
    if best.residual > tol:
        findings.append(Finding(
            id=f"register-residual-{mov.sheet}", topic="registration", severity="check",
            text=f"{mov.sheet} lands on {ref.sheet} with features up to {best.residual:.1f} mm apart "
                 f"(tolerance {tol:g} mm).", evidence=reg.evidence))
    if others:
        whole = content or mov
        harmless = all(_same_content(whole, best.transform, o.transform, tol) for o in others)
        alts = [f"rotate {o.transform.degrees:g}°, move ({o.transform.t[0]:.0f}, {o.transform.t[1]:.0f})"
                for o in others]
        if harmless:
            findings.append(Finding(
                id=f"register-symmetric-{mov.sheet}", topic="registration", severity="info",
                text=f"{mov.sheet} is symmetric: {len(others)} other placement(s) match as many features "
                     f"({'; '.join(alts)}) and put everything on the sheet in the same place, so the choice does "
                     f"not matter; the one with the least rotation is used.", evidence=reg.evidence))
            best_least = min([best] + others, key=lambda m: (m.transform.quarter % 4, m.residual))
            if best_least is not best:
                reg.rotation_deg = _z(round(best_least.transform.degrees, 6))
                reg.translation = (round(best_least.transform.t[0], 3), round(best_least.transform.t[1], 3))
                reg.residual_mm = round(best_least.residual, 3)
        else:
            findings.append(Finding(
                id=f"register-ambiguous-{mov.sheet}", topic="registration", severity="check",
                text=f"{mov.sheet} matches {ref.sheet} equally well in more than one way, and the placements "
                     "differ for what the sheet shows. Which is right?", evidence=reg.evidence,
                options=[f"rotate {best.transform.degrees:g}°, move ({best.transform.t[0]:.0f}, "
                         f"{best.transform.t[1]:.0f})"] + alts))
    return reg, findings


def identity(sheet: str) -> Registration:
    return Registration(sheet=sheet, method="building frame (this sheet's own coordinates)", residual_mm=0.0,
                        evidence=Evidence(sheet=sheet, note="frame sheet"))


def transform_of(reg: Registration) -> Transform:
    deg = reg.rotation_deg
    k = round(deg / 90) % 4
    return Transform(k, deg - round(deg / 90) * 90, reg.translation)


# ---------------------------------------------------------------- sections

def register_sections(sections: list[tuple[SectionRecognition, float | None]]) -> tuple[list[Registration],
                                                                                       list[Finding]]:
    """Vertical registration: each section's drawing y of its datum. A section without level marks takes the
    datum of a marked section whose floor levels it repeats."""
    regs, findings = [], []
    marked = [(s, y) for s, y in sections if y is not None and s.datum not in ("", "lowest floor (unlabelled)")]
    ref = marked[0] if marked else None
    for s, y in sections:
        if y is None:
            continue
        if s.datum not in ("", "lowest floor (unlabelled)"):
            regs.append(Registration(sheet=s.sheet, frame="vertical", translation=(0.0, round(-y, 3)),
                                     method=f"datum {s.datum} mark", residual_mm=0.0,
                                     evidence=Evidence(sheet=s.sheet, handles=_datum_handles(s))))
            if ref and s is not ref[0] and s.storey_heights_mm != ref[0].storey_heights_mm:
                findings.append(Finding(
                    id=f"sections-storeys-differ-{s.sheet}", topic="storey_height", severity="check",
                    text=f"{s.sheet} and {ref[0].sheet} give different storey heights "
                         f"({s.storey_heights_mm} and {ref[0].storey_heights_mm}).",
                    evidence=Evidence(sheet=s.sheet)))
            continue
        if ref is None:
            findings.append(Finding(id=f"section-datum-unknown-{s.sheet}", topic="datum", severity="check",
                                    text=f"{s.sheet} has no level marks and no section with marks to take its datum "
                                         "from; its levels are from its lowest floor.",
                                    evidence=Evidence(sheet=s.sheet)))
            regs.append(Registration(sheet=s.sheet, frame="vertical", translation=(0.0, round(-y, 3)),
                                     method="lowest floor (no level marks)", residual_mm=0.0,
                                     evidence=Evidence(sheet=s.sheet)))
            continue
        mine = _floor_levels(s)
        theirs = _floor_levels(ref[0])
        offset = theirs[0] - mine[0] if mine and theirs else None
        if offset is not None and len(mine) == len(theirs):
            residual = max(abs(a + offset - b) for a, b in zip(mine, theirs))
        else:
            residual = None
        if residual is None or residual > TOL:
            findings.append(Finding(id=f"section-datum-mismatch-{s.sheet}", topic="datum", severity="check",
                                    text=f"{s.sheet} has no level marks and its floor levels do not repeat those of "
                                         f"{ref[0].sheet}; its datum is not known.", evidence=Evidence(sheet=s.sheet)))
            continue
        s.datum = ref[0].datum
        for lv in s.levels:
            lv.elevation_mm = round(lv.elevation_mm + offset, 1)
        findings.append(Finding(id=f"section-datum-from-{s.sheet}", topic="datum", severity="info",
                                text=f"{s.sheet} has no level marks; its floor levels repeat {ref[0].sheet}'s, so its "
                                     f"levels are given from {ref[0].datum} as there.", evidence=Evidence(sheet=s.sheet)))
        regs.append(Registration(sheet=s.sheet, frame="vertical", translation=(0.0, round(-y + offset, 3)),
                                 method=f"floor levels matched to {ref[0].sheet}", residual_mm=round(residual, 3),
                                 evidence=Evidence(sheet=s.sheet)))
    return regs, findings


def _floor_levels(s: SectionRecognition) -> list[float]:
    return [lv.elevation_mm for lv in sorted(s.levels, key=lambda lv: lv.elevation_mm)
            if lv.kind in ("plinth", "floor", "roof") or (lv.kind == "ground" and lv.storey is not None)]


def _datum_handles(s: SectionRecognition) -> list[str]:
    lv = next((lv for lv in s.levels if lv.elevation_mm == 0 and lv.name == s.datum), None)
    return lv.evidence.handles if lv else []


def place_cut_lines(sections: list[SectionRecognition], marks_by_sheet: dict[str, list],
                    regs: dict[str, Registration], frame: str | None, tol: float = REGISTER_TOL) -> list[Finding]:
    """Each section's cut line from the plans' marks (``PlanRecognition.section_marks`` or the finder's own
    marks), in the building frame where the plan is registered."""
    marks_by_sheet = {sheet: [section_marks.from_model(m) if isinstance(m, SectionMarkModel) else m for m in ms]
                      for sheet, ms in marks_by_sheet.items()}
    findings = []
    for s in sections:
        label = section_marks.mark_label(s.name)
        found = [(sheet, m) for sheet, ms in marks_by_sheet.items() for m in ms if m.label == label]
        if not found:
            findings.append(Finding(id=f"section-cut-missing-{s.sheet}", topic="section_mark", severity="check",
                                    text=f"No plan shows where section {s.name} is cut (marks \"{label}\").",
                                    evidence=Evidence(sheet=s.sheet)))
            continue
        placed = []
        for sheet, m in found:
            reg = regs.get(sheet)
            if reg is None:
                continue
            tr = transform_of(reg)
            path = [tr.apply(p) for p in m.cut]
            view = _rot(m.view, reg.rotation_deg)
            placed.append((sheet, m, path, view))
        frame_first = sorted(placed, key=lambda x: x[0] != frame)
        if frame_first:
            sheet, m, path, view = frame_first[0]
            s.cut_path = [(round(x, 1), round(y, 1)) for x, y in path]
            s.cut_line = (s.cut_path[0], s.cut_path[-1]) if len(path) == 2 else None
            s.cut_sheet = frame
            s.view_direction = (_z(round(view[0], 6)), _z(round(view[1], 6)))
        else:
            sheet, m = found[0]
            s.cut_path = [(round(x, 1), round(y, 1)) for x, y in m.cut]
            s.cut_line = (s.cut_path[0], s.cut_path[-1]) if len(m.cut) == 2 else None
            s.cut_sheet = sheet
            s.view_direction = (_z(round(m.view[0], 6)), _z(round(m.view[1], 6)))
            findings.append(Finding(id=f"section-cut-unregistered-{s.sheet}", topic="section_mark", severity="check",
                                    text=f"Section {s.name}'s cut is shown on {sheet}, which could not be placed in "
                                         "the building frame; the cut is given in that sheet's coordinates.",
                                    evidence=Evidence(sheet=sheet, handles=m.handles)))
        if s.cut_line is None and s.cut_path:
            findings.append(Finding(id=f"section-cut-jogged-{s.sheet}", topic="section_mark", severity="info",
                                    text=f"Section {s.name} is a jogged section ({len(s.cut_path) - 1} legs); its "
                                         "cut is given as a path.", evidence=Evidence(sheet=s.sheet)))
        sheets = sorted({sheet for sheet, _ in found})
        if len(sheets) > 1:
            worst = 0.0
            for _, _, path, _ in placed[1:]:
                ref_path = placed[0][2]
                if len(path) != len(ref_path):
                    worst = math.inf
                    break
                worst = max([worst] + [math.dist(a, b) for a, b in zip(path, ref_path)])
            agree = worst <= tol
            findings.append(Finding(
                id=f"section-cut-several-{s.sheet}", topic="section_mark", severity="info" if agree else "check",
                text=f"Section {s.name} is marked on {len(sheets)} sheets ({', '.join(sheets)})"
                     + (f"; placed in the building frame they agree within {worst:.2f} mm." if agree and len(placed) > 1
                        else "; they do not all coincide in the building frame." if not agree else "."),
                evidence=Evidence(sheet=s.sheet, handles=[h for _, m in found for h in m.handles[:1]])))
    return findings


def _z(v: float) -> float:
    """No negative zero in output."""
    return 0.0 if v == 0 else v
