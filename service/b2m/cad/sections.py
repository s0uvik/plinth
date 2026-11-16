"""Section recognition (PRD Step 9, T4): levels and heights from a building section sheet.

Heights come only from dimensions; what a dimension means comes from what its two ends touch, never from its value:

- the main chain is the longest run of vertical dimensions laid end to end; its points are candidate levels;
- level marks ("PL", "GL", ...) name the line they sit on; the datum is PL, else GL, else the lowest floor;
- slab thickness is a dimension from a chain level down to the very next line below it;
- a floor is a chain level at or above the datum with that slab line under it; the highest floor is the roof;
- above the roof, a line that reaches the building's edge is a parapet top; anything else is a structure on the
  roof (a question: lift machine room, stair cover, tank...);
- below the ground line, the lowest long line is the founding level; a dimension standing on it is the footing;
- a dimension from a slab soffit down to a horizontal edge is a beam (downstand);
- a dimension spanning a framed rectangle inside a storey is an opening: a door if it starts at the floor, else a
  window, whose sill and head are then measured from the floor;
- notes with leaders into the ground-floor build-up ("150 MM THICK PCC") are the layers under the floor.

Anything else is listed as unexplained. Geometry is converted with the drawing's scale (written value per drawing
unit, from the dimensions themselves).

    python -m b2m.cad.sections <folder | drawing.dwg | drawing.dxf> [...] [--json]

prints, for the whole drawing set: section levels and heights, foundation, rebar, beams and the registration of
every sheet into one building frame, with the findings (see ``b2m.cad.structure``).
"""

from __future__ import annotations

import math
import re
import sys
from dataclasses import dataclass, field

from b2m.cad.annot import (TOL, HIndex, Measure, arrowheads, flat, flatten, leaders, level_marks, median, measures, name_of,
                           segments, text_box, texts, level_kind)
from b2m.cad.building import Evidence, Finding, Level, SectionRecognition
from b2m.cad.sheet import Sheet

EDGE_SHARE = 0.1  # a line above the roof ending within this share of the roof's width from its end is at the edge
LONG_SHARE = 0.25  # lines at least this share of the roof's width count when looking for the bottom of the drawing
SCALE_AGREE = 0.01  # dimensions whose scale differs by more than this share belong to another view (a detail)
_LAYER_NOTE = [re.compile(r"^(\d+(?:\.\d+)?)\s*MM\.?\s*(?:THICK|THK\.?)\s+(.+?)\s*$", re.I),
               re.compile(r"^(.+?)\s+(\d+(?:\.\d+)?)\s*MM\.?\s*(?:THICK|THK\.?)?\s*$", re.I)]


def recognise_section(sheet: Sheet, name: str | None = None) -> SectionRecognition:
    return _Section(sheet, name).run()


@dataclass
class _Dim:
    m: Measure
    meaning: str | None = None


@dataclass
class _Section:
    sheet: Sheet
    name: str | None
    findings: list[Finding] = field(default_factory=list)

    def __post_init__(self):
        self.src = name_of(self.sheet)
        self.ents = flatten(self.sheet.drawing_entities)
        all_segs = segments(self.ents)
        self.leaders = leaders([t for t in texts(self.ents) if not level_kind(t.text)], all_segs,
                               arrows=arrowheads(self.ents))
        annotation = {h for ld in self.leaders.values() for h in ld.handles}
        self.segs = [s for s in all_segs if s.handle not in annotation]  # the drawing without leader lines
        self.h = HIndex(self.segs)
        self.dims = [_Dim(m) for m in measures(self.ents)]
        self.levels: list[Level] = []
        self.known: dict[float, float] = {}  # drawing y -> mm above the chain's bottom, from dimensions

    # ------------------------------------------------------------ helpers
    def find(self, fid: str, topic: str, text: str, handles: list[str], severity: str = "check",
             options: list[str] | None = None, note: str | None = None) -> None:
        self.findings.append(Finding(id=fid, topic=topic, text=text, severity=severity, options=options or [],
                                     evidence=Evidence(sheet=self.src, handles=handles, note=note)))

    def key(self, y: float) -> float | None:
        for k in self.known:
            if abs(k - y) <= self.tol:
                return k
        return None

    def elev(self, y: float) -> tuple[float, bool]:
        """mm above the chain's bottom; True when it follows from dimensions, False when measured on the drawing."""
        k = self.key(y)
        if k is not None:
            return self.known[k], True
        return (y - self.y0) * self.s, False

    def rel(self, y: float) -> float:
        return round(self.elev(y)[0] - self.elev(self.datum_y)[0], 1)

    def dims_at(self, y: float) -> list[str]:
        return [d.m.handle for d in self.dims if d.m.axis == "v" and (abs(d.m.lo - y) <= self.tol
                                                                      or abs(d.m.hi - y) <= self.tol)]

    def line_at(self, y: float) -> list[str]:
        segs = self.h.at(y, self.tol)
        return [max(segs, key=lambda s: s.length).handle] if segs else []

    def level(self, name: str, kind: str, y: float, storey: int | None = None, extra: list[str] | None = None,
              note: str | None = None) -> Level:
        handles = list(dict.fromkeys((extra or []) + self.dims_at(y) + self.line_at(y)))
        lv = Level(name=name, elevation_mm=self.rel(y), kind=kind, storey=storey,
                   evidence=Evidence(sheet=self.src, handles=handles, note=note))
        self.levels.append(lv)
        return lv

    # ------------------------------------------------------------ main
    def run(self) -> SectionRecognition:
        rec = SectionRecognition(sheet=self.src, name=self.name or "?", datum="")
        rotation = (self.sheet.title_rotation or 0.0) % 360
        if min(rotation, 360 - rotation) > 0.5:
            self.find("section-turned", "section", f"The section's title is turned {rotation:g}°: the section is "
                      "drawn turned on the sheet, which this recogniser does not read. Its levels are not taken.",
                      [self.sheet.title_handle or ""], "blocking")
            rec.findings = self.findings
            return rec
        vertical = [d for d in self.dims if d.m.axis == "v" and d.m.scale]
        if not vertical:
            self.find("section-no-heights", "storey_height", "The section has no vertical dimensions, so no "
                      "heights can be read from it.", [], "blocking")
            rec.findings = self.findings
            return rec
        self.s = median([d.m.scale for d in vertical])
        self.tol = TOL / self.s
        same = [d for d in vertical if abs(d.m.scale / self.s - 1) <= SCALE_AGREE]
        for d in vertical:
            if d not in same:
                d.meaning = None  # stays unexplained, with a note below
        chain = self.chain(same)
        self.y0 = chain[0].m.lo
        acc = 0.0
        self.known[self.y0] = 0.0
        for d in chain:
            acc += d.m.value
            self.known[d.m.hi] = acc
            d.meaning = "storey chain"
        self.propagate(same)
        chain_ys = [self.y0] + [d.m.hi for d in chain]

        marks = self.marks()
        plinth, ground = marks.get("plinth"), marks.get("ground")
        if plinth:
            self.datum_y, rec.datum = plinth.y, plinth.text
        elif ground:
            self.datum_y, rec.datum = ground.y, ground.text
        else:
            self.datum_y = None

        t = self.slab(same, chain_ys)
        rec.slab_thickness_mm = t
        floors = self.floors(chain_ys, t, plinth)
        if self.datum_y is None:
            self.datum_y = floors[0] if floors else chain_ys[0]
            rec.datum = "lowest floor (unlabelled)"
            self.find("section-no-datum", "datum", "The section has no level marks (PL, GL); its levels are given "
                      "from its lowest floor. Registration ties them to a section that has marks.",
                      [], "info")
        roof = floors[-1] if len(floors) >= 2 else None
        for i, y in enumerate(floors[:-1] if roof is not None else floors):
            mark = next((m for m in marks.values() if abs(m.y - y) <= self.tol), None)
            kind = {"plinth": "plinth", "ground": "ground"}.get(mark.kind, "floor") if mark else "floor"
            self.level(mark.text if mark else f"storey {i} floor", kind, y, i, mark.handles if mark else None)
        if roof is not None:
            self.level("roof", "roof", roof)
            rec.storey_heights_mm = [round(self.elev(b)[0] - self.elev(a)[0], 1) for a, b in zip(floors, floors[1:])]
        else:
            self.find("section-no-storeys", "storey_height", "No two floor levels could be found in the section's "
                      "dimension chain, so its storey heights are not known.", [d.m.handle for d in chain], "blocking")

        if roof is not None:
            rec.parapet_height_mm = self.above_roof(chain_ys, roof, same)
        self.below(rec, chain_ys, marks, same, floors)
        if t is not None and floors:
            rec.beam_depth_mm = self.beams(same, floors, t)
            self.openings(rec, same, floors, t)
        rec.foundation_layers = self.layer_notes(floors)

        unexplained = [d for d in self.dims if d.meaning is None]
        for d in unexplained:
            m = d.m
            if m.axis != "v":
                why = "a horizontal dimension (a width or offset), not a height" if m.axis == "h" else "an oblique dimension"
            elif d not in same:
                why = f"drawn at another scale (1 drawing unit = {m.scale:g} mm), a detail"
            else:
                why = f"vertical, from {self.rel(m.lo):g} to {self.rel(m.hi):g} mm; what it spans is not recognised"
            rec.unexplained.append(Evidence(sheet=self.src, handles=[m.handle], note=f"{m.text}: {why}"))
        total = len(self.dims)
        self.find("section-dimension-coverage", "dimensions",
                  f"{total} dimensions on the section: {total - len(unexplained)} interpreted, "
                  f"{len(unexplained)} left unexplained.", [d.m.handle for d in unexplained], "info")
        rec.levels = sorted(self.levels, key=lambda lv: lv.elevation_mm)
        rec.findings = self.findings
        return rec

    # ------------------------------------------------------------ steps
    def chain(self, dims: list[_Dim]) -> list[_Dim]:
        """The storey chain: the longest run of dimensions laid end to end on one dimension line, then extended
        up and down by dimensions on other lines while exactly one continues it (a choice is not made by guess)."""
        lines: dict[float, list[_Dim]] = {}
        for d in dims:
            k = next((k for k in lines if abs(k - d.m.line[0]) <= self.tol), d.m.line[0])
            lines.setdefault(k, []).append(d)
        best: list[_Dim] = []
        for group in lines.values():
            run = self._longest(group)
            if (len(run), run[-1].m.hi - run[0].m.lo) > (len(best), best[-1].m.hi - best[0].m.lo if best else 0):
                best = run
        while True:
            below = [d for d in dims if d not in best and abs(d.m.hi - best[0].m.lo) <= self.tol]
            if len({(round(d.m.lo), round(d.m.hi)) for d in below}) != 1:
                break
            best.insert(0, below[0])
        while True:
            above = [d for d in dims if d not in best and abs(d.m.lo - best[-1].m.hi) <= self.tol]
            if len({(round(d.m.lo), round(d.m.hi)) for d in above}) != 1:
                break
            best.append(above[0])
        return best

    def _longest(self, dims: list[_Dim]) -> list[_Dim]:
        order = sorted(dims, key=lambda d: (d.m.lo, d.m.hi))
        best: list[tuple[int, float, int | None]] = []
        for i, d in enumerate(order):
            cand = (1, d.m.hi - d.m.lo, None)
            for j in range(i):
                if abs(order[j].m.hi - d.m.lo) <= self.tol:
                    n, span, _ = best[j]
                    if (n + 1, span + d.m.hi - d.m.lo) > cand[:2]:
                        cand = (n + 1, span + d.m.hi - d.m.lo, j)
            best.append(cand)
        i = max(range(len(order)), key=lambda k: best[k][:2])
        out = []
        while i is not None:
            out.append(order[i])
            i = best[i][2]
        return out[::-1]

    def propagate(self, dims: list[_Dim]) -> None:
        """Every height reachable from the chain through dimensions gets its dimensioned elevation."""
        changed = True
        while changed:
            changed = False
            for d in dims:
                lo, hi = self.key(d.m.lo), self.key(d.m.hi)
                if lo is not None and hi is None:
                    self.known[d.m.hi] = self.known[lo] + d.m.value
                    changed = True
                elif hi is not None and lo is None:
                    self.known[d.m.lo] = self.known[hi] - d.m.value
                    changed = True
                elif lo is not None and hi is not None and abs(self.known[hi] - self.known[lo] - d.m.value) > TOL:
                    self.find(f"section-dims-disagree-{d.m.handle}", "dimensions",
                              f"Dimension {d.m.text} disagrees with the chain: the chain gives "
                              f"{self.known[hi] - self.known[lo]:g} mm between the same points.",
                              [d.m.handle], "check")

    def marks(self) -> dict:
        found = level_marks(self.ents, self.h)
        out = {}
        for kind in {m.kind for m in found}:
            group = [m for m in found if m.kind == kind]
            ys = sorted({round(m.y, 3) for m in group})
            if max(ys) - min(ys) > self.tol:
                self.find(f"section-marks-{kind}", "datum", f"The \"{group[0].text}\" marks on the section sit on "
                          f"different lines ({', '.join(f'{(y - self.y0) * self.s:g}' for y in ys)} mm above the "
                          "chain's bottom); none of them is used.", [h for m in group for h in m.handles], "check")
                continue
            if any(m.candidates > 1 for m in group):
                self.find(f"section-mark-line-{kind}", "datum", f"The \"{group[0].text}\" mark has more than one "
                          "line beside it; the nearest is used.", [h for m in group for h in m.handles], "check")
            out[kind] = group[0]
            out[kind].handles = [h for m in group for h in m.handles]
        return out

    def slab(self, dims: list[_Dim], chain_ys: list[float]) -> float | None:
        found = []
        for d in dims:
            if d.meaning or not any(abs(d.m.hi - y) <= self.tol for y in chain_ys):
                continue
            if self.datum_y is not None and d.m.hi < self.datum_y - self.tol:
                continue
            under = self.h.below(d.m.hi_point[0], d.m.hi)
            if under is not None and abs(under - d.m.lo) <= self.tol:
                found.append(d)
        values = sorted({round(d.m.value, 1) for d in found})
        for d in found:
            d.meaning = "slab thickness"
        if len(values) == 1:
            return values[0]
        if values:
            self.find("section-slab-several", "slab_thickness", "The section dimensions slabs of different "
                      f"thickness ({', '.join(f'{v:g}' for v in values)} mm); which applies where is not settled.",
                      [d.m.handle for d in found], "check", [f"{v:g} mm" for v in values])
        else:
            self.find("section-slab-missing", "slab_thickness", "No dimension gives the slab thickness.",
                      [], "check")
        return None

    def floors(self, chain_ys: list[float], t: float | None, plinth) -> list[float]:
        base = self.datum_y if self.datum_y is not None else min(chain_ys)
        cands = [y for y in chain_ys if y >= base - self.tol]
        if t is None:
            return cands
        out = []
        for y in cands:
            if plinth is not None and abs(y - plinth.y) <= self.tol:
                out.append(y)
                continue
            top = self.h.at(y, self.tol)
            under = self.h.at(y - t / self.s, self.tol)
            if any(a.x0 <= b.x1 and b.x0 <= a.x1 for a in top for b in under):
                out.append(y)
        return out

    def at_edge(self, y: float, roof: float) -> bool:
        ext = self.h.extent(roof)
        if not ext:
            return False
        reach = EDGE_SHARE * (ext[1] - ext[0])
        return any(abs(s.x0 - ext[0]) <= reach or abs(s.x1 - ext[1]) <= reach for s in self.h.at(y, self.tol))

    def above_roof(self, chain_ys: list[float], roof: float, dims: list[_Dim]) -> float | None:
        parapet = None
        others: dict[float, list[str]] = {}
        for y in [y for y in chain_ys if y > roof + self.tol]:
            if parapet is None and self.at_edge(y, roof):
                parapet = y
            else:
                others.setdefault(y, [])
        for d in dims:
            if d.meaning or d.m.lo < roof - self.tol:
                continue
            if parapet is None and abs(d.m.lo - roof) <= self.tol and self.at_edge(d.m.hi, roof):
                parapet = d.m.hi
                d.meaning = "parapet"
                continue
            if parapet is not None and abs(d.m.lo - roof) <= self.tol and abs(d.m.hi - parapet) <= self.tol:
                d.meaning = "parapet"
                continue
            d.meaning = "structure above the roof"
            others.setdefault(d.m.hi, []).append(d.m.handle)
        height = None
        if parapet is not None:
            self.level("parapet top", "parapet_top", parapet)
            height = round(self.elev(parapet)[0] - self.elev(roof)[0], 1)
        if others:
            handles = []
            for y, hs in sorted(others.items()):
                up = round(self.elev(y)[0] - self.elev(roof)[0], 1)
                self.level(f"top of structure above roof (+{up:g})", "other", y, extra=hs)
                handles += hs
            ups = ", ".join(f"{self.elev(y)[0] - self.elev(roof)[0]:g}" for y in sorted(others))
            self.find("section-above-roof", "roof_structure",
                      f"Something stands on the roof, dimensioned {ups} mm above roof level and not reaching the "
                      "building's edge. What is it?", handles, "check",
                      ["lift machine room / lift overrun", "stair cover (mumty)", "water tank", "other"])
        return height

    def below(self, rec: SectionRecognition, chain_ys, marks, dims, floors) -> None:
        plinth, ground = marks.get("plinth"), marks.get("ground")
        if ground and not (plinth and abs(plinth.y - ground.y) <= self.tol):
            self.level(ground.text, "ground", ground.y, extra=ground.handles)
        if plinth and ground:
            span = [d for d in dims if abs(d.m.lo - ground.y) <= self.tol and abs(d.m.hi - plinth.y) <= self.tol]
            height, dimensioned = self.elev(plinth.y)[0] - self.elev(ground.y)[0], bool(span)
            if not span:
                ok = self.key(plinth.y) is not None and self.key(ground.y) is not None
                dimensioned = ok
            rec.plinth_height_mm = round(height, 1) if dimensioned else None
            for d in span:
                d.meaning = d.meaning or "plinth height"
            if not dimensioned:
                self.find("section-plinth-measured", "plinth_height", f"The plinth height is not dimensioned; the "
                          f"drawing shows {height:g} mm between {ground.text} and {plinth.text}.",
                          plinth.handles + ground.handles, "check", [f"{height:g} mm (as drawn)"])
        ext = self.h.extent(floors[-1]) if floors else None
        width = (ext[1] - ext[0]) if ext else max((s.length for s in self.h.segs), default=0.0)
        top = ground.y if ground else self.datum_y
        long = [s.a[1] for s in self.h.segs if s.length >= LONG_SHARE * width and s.a[1] < top - self.tol]
        if not long:
            return
        bottom = min(long)
        depth, dimensioned = self.elev(top)[0] - self.elev(bottom)[0], self.key(bottom) is not None
        ref = ground.text if ground else rec.datum
        self.level(f"founding level ({depth:g} below {ref})", "foundation", bottom,
                   note=None if dimensioned else "measured on the drawing, not dimensioned")
        if not dimensioned:
            self.find("section-founding-measured", "foundation", f"The founding level is not dimensioned; the "
                      f"drawing shows it {depth:g} mm below {ref}.", self.line_at(bottom), "check")
        for y in chain_ys:
            if bottom + self.tol < y < top - self.tol:
                self.level("foundation level", "foundation", y)
        for d in dims:
            if d.meaning or abs(d.m.lo - bottom) > self.tol or d.m.hi >= top - self.tol:
                continue
            d.meaning = "foundation thickness"
            self.level(f"top of foundation ({d.m.value:g} thick)", "foundation", d.m.hi, extra=[d.m.handle])
        for d in dims:
            if d.meaning == "storey chain" and d.m.hi <= top + self.tol and d.m.lo < top - self.tol:
                d.meaning = "storey chain (below ground)"

    def beams(self, dims: list[_Dim], floors: list[float], t: float) -> float | None:
        soffits = [y - t / self.s for y in floors]
        found = []
        for d in dims:
            if d.meaning or not any(abs(d.m.hi - y) <= self.tol for y in soffits):
                continue
            if self.h.touching(d.m.lo_point, self.tol):
                found.append(d)
                d.meaning = "beam depth"
        values = sorted({round(d.m.value, 1) for d in found})
        if len(values) == 1:
            v = values[0]
            self.find("section-beam-depth", "beam_depth", f"Beams are dimensioned {v:g} mm below the {t:g} mm slab "
                      f"soffit, so {v + t:g} mm deep overall from floor level. Which is the beam depth?",
                      [d.m.handle for d in found], "check",
                      [f"{v:g} mm below the slab ({v + t:g} mm overall)", f"{v:g} mm overall"])
            return v
        if values:
            self.find("section-beam-several", "beam_depth", "Beams of different depth are dimensioned "
                      f"({', '.join(f'{v:g}' for v in values)} mm below the slab); which is which is not settled.",
                      [d.m.handle for d in found], "check", [f"{v:g} mm" for v in values])
        else:
            self.find("section-beam-missing", "beam_depth", "No dimension gives a beam depth.", [], "check")
        return None

    def openings(self, rec: SectionRecognition, dims: list[_Dim], floors: list[float], t: float) -> None:
        for d in dims:
            if d.meaning:
                continue
            storey = next((i for i, (a, b) in enumerate(zip(floors, floors[1:]))
                           if a - self.tol <= d.m.lo and d.m.hi <= b - t / self.s + self.tol), None)
            if storey is None or not self.framed(d.m):
                continue
            floor = floors[storey]
            sill, sill_dim = self.elev(d.m.lo)[0] - self.elev(floor)[0], self.key(d.m.lo) is not None
            if abs(sill) <= TOL:
                d.meaning = "door height"
                rec.door_heights_mm.append(d.m.value)
                self.level(f"door head, storey {storey}", "lintel", d.m.hi, storey, [d.m.handle])
                continue
            d.meaning = "window height"
            head = sill + d.m.value
            rec.window_sills_mm.append(round(sill, 1))
            rec.window_heads_mm.append(round(head, 1))
            note = None if sill_dim else "sill measured on the drawing from the floor level, not dimensioned"
            self.level(f"window sill, storey {storey}", "sill", d.m.lo, storey, [d.m.handle], note)
            self.level(f"window head, storey {storey}", "lintel", d.m.hi, storey, [d.m.handle], note)
            if not sill_dim:
                self.find(f"section-window-sill-{d.m.handle}", "window_sill",
                          f"A {d.m.value:g} mm high window is dimensioned in storey {storey}; its sill is not "
                          f"dimensioned and measures {sill:g} mm above the floor on the drawing (head {head:g}).",
                          [d.m.handle], "check", [f"sill {sill:g} mm (as drawn)"])

    def framed(self, m: Measure) -> bool:
        """The dimension spans a drawn frame: horizontal edges at both ends that overlap, and at least two more
        horizontal lines between them inside that width (the frame and glazing of a window or door)."""
        lows, highs = self.h.touching(m.lo_point, self.tol), self.h.touching(m.hi_point, self.tol)
        for a in lows:
            for b in highs:
                x0, x1 = max(a.x0, b.x0), min(a.x1, b.x1)
                if x1 - x0 <= self.tol:
                    continue
                inner = [s for s in self.h.segs if m.lo + self.tol < s.a[1] < m.hi - self.tol
                         and s.x0 >= x0 - self.tol and s.x1 <= x1 + self.tol]
                if len(inner) >= 2:
                    return True
        return False

    def layer_notes(self, floors: list[float]) -> list[tuple[str, float]]:
        if not floors:
            return []
        top = floors[0]
        out = []
        for note in texts(self.ents):
            if level_kind(note.text):
                continue
            ld = self.leaders.get(note.handle)
            if not ld:
                continue
            tips = [p for p in ld.tips if p[1] <= top + self.tol]
            if not tips:
                continue
            tip = tips[0]
            text = flat(note.text)
            thickness = material = None
            m = _LAYER_NOTE[0].match(text)
            if m:
                thickness, material = float(m.group(1)), m.group(2)
            else:
                m2 = _LAYER_NOTE[1].match(text)
                if m2 and re.search(r"MM", text, re.I):
                    thickness, material = float(m2.group(2)), m2.group(1)
            above = min((s.a[1] for s in self.h.segs if s.a[1] > tip[1] + self.tol
                         and s.x0 - self.tol <= tip[0] <= s.x1 + self.tol), default=None)
            below = self.h.below(tip[0], tip[1])
            drawn = (above - below) * self.s if above is not None and below is not None else None
            handles = [note.handle, *ld.handles]
            if thickness is None:
                options = [f"{drawn:g} mm (the drawn layer)"] if drawn else []
                self.find(f"section-layer-{note.handle}", "foundation_layer",
                          f"The note \"{text}\" points into the floor build-up but gives no thickness.",
                          handles, "check", options)
                continue
            if drawn is not None and abs(drawn - thickness) > TOL:
                self.find(f"section-layer-drawn-{note.handle}", "foundation_layer",
                          f"The note \"{text}\" says {thickness:g} mm; the layer it points at is drawn "
                          f"{drawn:g} mm thick.", handles, "check")
            out.append((tip[1], material.upper(), thickness))
        return [(mat, th) for _, mat, th in sorted(out, key=lambda o: -o[0])]


def _flat_name(sheet: Sheet) -> str | None:
    from b2m.cad import classify
    c = classify.classify(sheet.title, name_of(sheet))
    return c.name


def main(argv: list[str] | None = None) -> int:
    from b2m.cad import structure
    return structure.main(argv)


if __name__ == "__main__":
    sys.exit(main())
