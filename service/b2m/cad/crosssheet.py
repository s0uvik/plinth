"""Cross-sheet resolution (PRD Step 9, T5): what one sheet leaves open and another settles, once every sheet is in
one building frame (registration, T4).

- An unmarked opening on one plan that sits in the wall of a lift shaft labelled on another plan (and, when that
  plan draws one there, at the same place as its lift landing door) is a lift landing door. The finding asking for
  its mark is dropped and a ``Resolution`` keeps the evidence from both sheets.
- A section whose founding level is only measured, not dimensioned, is settled by another section that dimensions
  the founding level at the same elevation (both on the same datum).
- The storeys the floor plans cover must be the storeys the sections show: each storey on exactly one plan, and
  the same set as the sections' floors. A mismatch is a blocking finding; agreement is said (info).

Nothing here changes a value the drawings give; it only settles what one sheet left open, with both sheets'
evidence, or raises a finding.
"""

from __future__ import annotations

import math

from shapely.geometry import Point as SPoint, Polygon

from b2m.cad import register
from b2m.cad.building import Evidence, Finding, Opening, PlanRecognition, RecognisedBuilding, Resolution, Wall

TOL = register.REGISTER_TOL  # mm: features of two registered sheets coincide this well
LEVEL_TOL = 1.0  # mm: two dimensioned levels are the same level


def transforms(building: RecognisedBuilding) -> dict[str, register.Transform]:
    """Plan-frame transforms of every registered sheet, by sheet name."""
    return {r.sheet: register.transform_of(r) for r in building.registrations if r.frame == "plan"}


def run(building: RecognisedBuilding) -> RecognisedBuilding:
    """All cross-sheet resolutions and checks, in place. Returns the building."""
    resolve_lift_doors(building)
    resolve_founding_level(building)
    section_window_heights(building)
    building.findings += storey_findings(building)
    return building


# ---------------------------------------------------------------- lift landing doors

def resolve_lift_doors(building: RecognisedBuilding, tol: float = TOL) -> list[Resolution]:
    tfs = transforms(building)
    lifts = []  # (sheet, shaft, polygon in the frame)
    doors = []  # (sheet, opening, centre in the frame): lift landing doors a plan recognised itself
    for plan in building.plans:
        tr = tfs.get(plan.sheet)
        if tr is None:
            continue
        for s in plan.shafts:
            if s.kind == "lift" and len(s.outline) >= 3:
                lifts.append((plan.sheet, s, Polygon([tr.apply(p) for p in s.outline])))
        for o in plan.openings:
            if _is_lift_door(o):
                doors.append((plan.sheet, o, tr.apply(o.center)))
    out = []
    for plan in building.plans:
        tr = tfs.get(plan.sheet)
        if tr is None:
            continue
        walls = {w.id: w for w in plan.walls}
        for o in plan.openings:
            if o.mark or _is_lift_door(o) or o.wall_id not in walls:
                continue
            wall = walls[o.wall_id]
            ends = [tr.apply(p) for p in _ends(o, wall)]
            centre = tr.apply(o.center)
            reach = wall.thickness_mm / 2 + tol
            for sheet, shaft, poly in lifts:
                if sheet == plan.sheet:
                    continue
                if poly.contains(SPoint(centre)) or poly.exterior.distance(SPoint(centre)) > reach:
                    continue
                if not all(poly.buffer(reach).contains(SPoint(e)) for e in ends):
                    continue
                twin = [(s2, d) for s2, d, c in doors if s2 != plan.sheet and math.dist(c, centre) <= 2 * tol]
                out.append(_settle_lift_door(building, plan, o, sheet, shaft, twin))
                break
    return out


def _is_lift_door(o: Opening) -> bool:
    return o.kind == "door" and o.swing == "sliding" and bool(o.evidence.note) and \
        o.evidence.note.startswith("lift landing door")


def _ends(o: Opening, wall: Wall) -> list[tuple[float, float]]:
    dx, dy = wall.end[0] - wall.start[0], wall.end[1] - wall.start[1]
    length = math.hypot(dx, dy) or 1.0
    ux, uy = dx / length * o.width_mm / 2, dy / length * o.width_mm / 2
    return [(o.center[0] - ux, o.center[1] - uy), (o.center[0] + ux, o.center[1] + uy)]


def _settle_lift_door(building: RecognisedBuilding, plan: PlanRecognition, o: Opening, sheet: str, shaft,
                      twin: list) -> Resolution:
    label = f"\"{shaft.label}\"" if shaft.label else shaft.id
    was = o.kind
    o.kind, o.swing = "door", "sliding"
    o.evidence = o.evidence.model_copy(update={"note": f"lift landing door: in the wall of lift shaft {label} drawn "
                                                       f"on {sheet} (settled across sheets)"})
    settled = [f for f in plan.findings if f.topic == "opening_without_mark"
               and set(f.evidence.handles) & set(o.evidence.handles)]
    plan.findings = [f for f in plan.findings if f not in settled]
    evidence = [o.evidence, shaft.evidence] + [d.evidence for _, d in twin]
    text = (f"The unmarked {was} {o.id} ({o.width_mm:g} mm) on {plan.sheet} sits in the wall of lift shaft {label} "
            f"drawn on {sheet}")
    text += (f", where {twin[0][0]} draws a lift landing door ({twin[0][1].id}) at the same place" if twin else "")
    text += ": it is a lift landing door (sliding), not a window needing a mark."
    res = Resolution(id=f"lift-door-{plan.sheet}-{o.id}", topic="opening_without_mark", text=text,
                     findings=[f"{plan.sheet}#{f.id}" for f in settled], evidence=evidence)
    building.resolved.append(res)
    return res


# ---------------------------------------------------------------- founding level

def resolve_founding_level(building: RecognisedBuilding) -> list[Resolution]:
    out = []
    for s in building.sections:
        open_ = [f for f in s.findings if f.id == "section-founding-measured"]
        mine = _founding(s)
        if not open_ or mine is None:
            continue
        for t in building.sections:
            theirs = _founding(t)
            if t is s or theirs is None or t.datum != s.datum or any(f.id == "section-founding-measured"
                                                                     for f in t.findings):
                continue
            if abs(theirs.elevation_mm - mine.elevation_mm) > LEVEL_TOL:
                continue
            s.findings = [f for f in s.findings if f not in open_]
            res = Resolution(
                id=f"founding-level-{s.sheet}", topic="foundation",
                text=f"Section {s.name} does not dimension its founding level (it measures "
                     f"{mine.elevation_mm:g} mm from {s.datum}); section {t.name} dimensions it at the same level "
                     f"({theirs.name}), so the founding level is {theirs.elevation_mm:g} mm from {s.datum}.",
                findings=[f"{s.sheet}#{f.id}" for f in open_],
                evidence=[mine.evidence, theirs.evidence] + [f.evidence for f in open_])
            building.resolved.append(res)
            out.append(res)
            break
    return out


def _founding(s):
    lv = [lv for lv in s.levels if lv.kind == "foundation" and lv.name.startswith("founding")]
    return min(lv, key=lambda lv: lv.elevation_mm) if lv else None


# ---------------------------------------------------------------- window heights from sections

def section_windows(s) -> list[tuple[int, float, float, list[str]]]:
    """(storey, sill, head, handles) of each window a section dimensions, heights above that storey's floor."""
    floors = {lv.storey: lv.elevation_mm for lv in s.levels
              if lv.storey is not None and lv.kind in ("ground", "plinth", "floor")}
    out = []
    for k in sorted({lv.storey for lv in s.levels if lv.kind == "sill" and lv.storey is not None}):
        sills = [lv for lv in s.levels if lv.kind == "sill" and lv.storey == k]
        heads = [lv for lv in s.levels if lv.kind == "lintel" and lv.storey == k]
        if len(sills) == 1 and len(heads) == 1 and k in floors:
            out.append((k, round(sills[0].elevation_mm - floors[k], 1), round(heads[0].elevation_mm - floors[k], 1),
                        sills[0].evidence.handles + heads[0].evidence.handles))
    return out


def section_window_heights(building: RecognisedBuilding, tol: float = TOL) -> list[Resolution]:
    """A window a section dimensions is identified when the section's cut crosses, on the plan of that storey,
    windows of one mark only: that mark takes the section's sill and head (a resolution; the question about the
    mark's heights becomes a check)."""
    from shapely.geometry import LineString

    tfs = transforms(building)
    out = []
    for s in building.sections:
        if not s.cut_path or s.cut_sheet not in tfs or len(s.cut_path) < 2:
            continue
        cut = LineString(s.cut_path)  # in the building frame (cut_sheet is the frame sheet when registered)
        for storey, sill, head, handles in section_windows(s):
            crossed = []
            for plan in building.plans:
                tr = tfs.get(plan.sheet)
                if tr is None or storey not in plan.storeys:
                    continue
                walls = {w.id: w for w in plan.walls}
                for o in plan.openings:
                    if o.kind not in ("window", "ventilator"):
                        continue
                    half = walls[o.wall_id].thickness_mm / 2 if o.wall_id in walls else 0.0
                    if cut.distance(SPoint(tr.apply(o.center))) <= half + tol:
                        crossed.append((plan, o))
            marks = {o.mark_normalised for _, o in crossed}
            if len(marks) != 1 or None in marks:
                continue
            mark = marks.pop()
            for plan in building.plans:
                for o in plan.openings:
                    if o.kind in ("window", "ventilator") and o.mark_normalised == mark:
                        o.sill_mm, o.height_mm = sill, round(head - sill, 1)
            res = Resolution(
                id=f"window-heights-{s.sheet}-{storey}", topic="window_size",
                text=f"Section {s.name} dimensions a window in storey {storey} (sill {sill:g} mm, head {head:g} mm "
                     f"above the floor) and its cut crosses only windows marked {mark} on "
                     f"{', '.join(sorted({p.sheet for p, _ in crossed}))}: {mark} takes these heights.",
                evidence=[Evidence(sheet=s.sheet, handles=handles, note=f"storey {storey} window")]
                + [o.evidence for _, o in crossed])
            building.resolved.append(res)
            out.append(res)
    return out


# ---------------------------------------------------------------- storeys

def storey_findings(building: RecognisedBuilding) -> list[Finding]:
    """Each storey on one floor plan, and the plans' storeys the same as the sections' floors."""
    out: list[Finding] = []
    floors = [p for p in building.plans if p.role == "floor_plan"]
    if not floors:
        return out
    by_storey: dict[int, list[str]] = {}
    for p in floors:
        for n in p.storeys:
            by_storey.setdefault(n, []).append(p.sheet)
    for n, sheets in sorted(by_storey.items()):
        if len(sheets) > 1:
            out.append(Finding(id=f"storey-on-several-plans-{n}", topic="storeys", severity="blocking",
                               text=f"Storey {n} is drawn on {len(sheets)} floor plans ({', '.join(sheets)}). Which "
                                    f"plan is storey {n}?",
                               evidence=Evidence(sheet=sheets[0], note=", ".join(sheets)), options=sheets))
    covered = sorted(by_storey)
    plans_said = "; ".join(f"{p.sheet} covers {_span(p.storeys) or 'no storey'}" for p in floors)
    for s in building.sections:
        shown = section_storeys(s)
        if not shown:
            continue
        ev = Evidence(sheet=s.sheet, handles=[h for lv in s.levels if lv.storey is not None
                                              for h in lv.evidence.handles][:12],
                      note=f"floor levels of storeys {_span(shown)}")
        if shown == covered:
            out.append(Finding(id=f"storeys-agree-{s.sheet}", topic="storeys", severity="info",
                               text=f"Section {s.name} shows {len(shown)} storeys ({_span(shown)}) and the floor "
                                    f"plans cover the same storeys ({plans_said}).", evidence=ev))
            continue
        missing = [n for n in shown if n not in covered]
        extra = [n for n in covered if n not in shown]
        parts = []
        if missing:
            parts.append(f"no floor plan covers storey{'s' if len(missing) > 1 else ''} {_span(missing)}")
        if extra:
            parts.append(f"the plans cover storey{'s' if len(extra) > 1 else ''} {_span(extra)}, which the section "
                         "does not show")
        out.append(Finding(
            id=f"storeys-disagree-{s.sheet}", topic="storeys", severity="blocking",
            text=f"Section {s.name} shows {len(shown)} storeys ({_span(shown)}) but {' and '.join(parts)} "
                 f"({plans_said}). Which is right?", evidence=ev,
            options=[f"the section is right ({len(shown)} storeys)",
                     f"the plans are right ({len(covered)} storeys: {_span(covered)})", "other"]))
    return out


def section_storeys(s) -> list[int]:
    """The storeys whose floors the section shows (storey numbers on its floor levels, else its storey heights)."""
    numbered = sorted({lv.storey for lv in s.levels if lv.storey is not None
                       and lv.kind in ("ground", "plinth", "floor")})
    if numbered:
        return numbered
    return list(range(len(s.storey_heights_mm)))


def _span(ns: list[int]) -> str:
    ns = sorted(ns)
    if not ns:
        return ""
    if len(ns) > 2 and ns == list(range(ns[0], ns[-1] + 1)):
        return f"{ns[0]}-{ns[-1]}"
    return ", ".join(str(n) for n in ns)
