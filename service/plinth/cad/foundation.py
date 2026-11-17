"""Foundation recognition (PRD Step 9, T4): footings, founding depth, typical column sections and rebar notes.

- Footings: rectangles on the footing plan that hold column marks (solid filled rectangles). One column: isolated;
  several in one row: combined; several rows: raft. Sizes are the dimensions spanning the rectangle, else measured.
- Founding depth and footing depth: in a footing section (a view with a GL mark), the dimension from GL down to the
  bottom of the footing, and the dimension standing on that bottom. Values are as written, whatever scale the view
  is drawn at. They apply to every footing only when there is one footing, or all footings are alike; otherwise the
  question is asked.
- Typical column sections: rectangles holding bar circles; their size is the dimensions spanning them.
- Rebar notes (also dimension texts such as "<>mm Clear Cover") are parsed and tied to the member their leader points
  into: a column section, the footing in a footing section, or the column rising from it.

Sections on this sheet ("SECTION- AA") are this sheet's own views, never building sections: they are named with the
sheet ("8.Foundation Details.dxf#AA") in evidence and never matched to building section sheets.
"""

from __future__ import annotations

import math
from dataclasses import dataclass

from plinth.cad import marks as section_marks
from plinth.cad import rebar as rebar_notes
from plinth.cad.annot import (LEADER_REACH, TOL, Box, HIndex, arrowheads, flat, flatten, inside, leaders, level_kind,
                           level_marks, line_boxes, measures, name_of, outline_boxes, rect_distance, segments,
                           solid_boxes, texts)
from plinth.cad.building import Column, Evidence, Finding, Footing, FoundationRecognition, RebarSpec
from plinth.cad.sheet import Sheet

MIN_BARS = 3  # a column section shows at least this many bars (a 3-bar column is the smallest there is)


def recognise_foundation(sheet: Sheet) -> FoundationRecognition:
    return _Foundation(sheet).run()


@dataclass
class _Member:
    name: str
    rect: tuple[float, float, float, float]
    kind: str  # "footing", "column", "column_section"


class _Foundation:
    def __init__(self, sheet: Sheet):
        self.sheet = sheet
        self.src = name_of(sheet)
        self.ents = flatten(sheet.drawing_entities)
        self.segs = segments(self.ents)
        self.h = HIndex(self.segs)
        self.dims = measures(self.ents)
        self.used: set[str] = set()
        self.findings: list[Finding] = []
        self.arrows = arrowheads(self.ents)

    def find(self, fid, topic, text, handles, severity="check", options=None):
        self.findings.append(Finding(id=fid, topic=topic, text=text, severity=severity, options=options or [],
                                     evidence=Evidence(sheet=self.src, handles=handles)))

    def run(self) -> FoundationRecognition:
        rec = FoundationRecognition(sheet=self.src)
        cols = [b for b in solid_boxes(self.ents) if b.width > TOL and b.height > TOL]
        frames: list[Box] = []
        for box in outline_boxes(self.ents) + line_boxes(self.segs):  # a polyline's edges also form a line box
            if not any(all(abs(a - b) <= TOL for a, b in zip(box.rect, f.rect)) for f in frames):
                frames.append(box)
        circles = [e for e in self.ents if e.kind == "circle"]
        footings = self.footings(frames, cols, rec)
        self.depths(rec, footings)
        sections = self.column_sections(frames, cols, circles, rec)
        self.rebar(rec, footings, sections, circles)
        own, mark_findings = section_marks.find_marks(self.src, self.sheet.drawing_entities)
        if own:
            labels = ", ".join(sorted({m.label for m in own}))
            self.find("foundation-own-sections", "section_mark",
                      f"The footing plan carries its own section marks ({labels}); the sections drawn on this sheet "
                      f"belong to it ({self.src}#..) and are not the building's sections of the same name.",
                      [h for m in own for h in m.handles[:2]], "info")
        self.findings += [f for f in mark_findings if f.severity != "info"]
        for m in self.dims:
            if m.handle not in self.used:
                rec.unexplained.append(Evidence(sheet=self.src, handles=[m.handle], note=f"{m.text}: not interpreted"))
        rec.findings = self.findings
        return rec

    # ------------------------------------------------------------ footings
    def footings(self, frames: list[Box], cols: list[Box], rec: FoundationRecognition) -> list[tuple[Footing, Box]]:
        holding = []
        for f in frames:
            inner = [c for c in cols if inside(c.center, f.rect) and c.width < f.width and c.height < f.height]
            if inner:
                holding.append((f, inner))
        # innermost frames only: a frame holding another frame's columns is not itself a footing
        holding = [(f, inner) for f, inner in holding
                   if not any(g is not f and inside(g.center, f.rect) and g.width * g.height < f.width * f.height
                              and set(map(id, gi)) <= set(map(id, inner)) for g, gi in holding)]
        out = []
        column_ids: dict[int, str] = {}
        for f, inner in holding:
            for c in inner:
                if id(c) not in column_ids:
                    cid = f"C{len(column_ids) + 1}"
                    column_ids[id(c)] = cid
                    rec.plan_columns.append(Column(id=cid, center=_r(c.center), width_mm=_r1(c.width),
                                                   depth_mm=_r1(c.height),
                                                   evidence=Evidence(sheet=self.src, handles=c.handles)))
        for n, (f, inner) in enumerate(sorted(holding, key=lambda fi: (fi[0].rect[1], fi[0].rect[0])), 1):
            fid = f"F{n}"
            width, wdims = self.size(f, "h")
            length, ldims = self.size(f, "v")
            xs = {round(c.center[0]) for c in inner}
            ys = {round(c.center[1]) for c in inner}
            kind = "isolated" if len(inner) == 1 else "raft" if len(xs) > 1 and len(ys) > 1 else "combined"
            handles = f.handles + wdims + ldims
            note = None
            if not wdims or not ldims:
                note = "size measured on the drawing where no dimension spans the footing"
                self.find(f"footing-size-{fid}", "footing_size", f"Footing {fid} is not dimensioned on "
                          f"{'both sides' if not wdims and not ldims else 'one side'}; its drawn size is "
                          f"{width:g} x {length:g} mm.", f.handles, "check")
            offsets = self.offsets(f, inner)
            if offsets:
                handles += offsets
                note = (note + "; " if note else "") + "projections beyond the columns dimensioned"
            footing = Footing(id=fid, kind=kind, center=_r(f.center), width_mm=width, length_mm=length,
                              column_id=column_ids[id(inner[0])] if kind == "isolated" else None,
                              evidence=Evidence(sheet=self.src, handles=handles, note=note))
            out.append((footing, f))
        rec.footings = [fo for fo, _ in out]
        if not out:
            self.find("footing-none", "footing", "No footing could be found on the foundation sheet (a rectangle "
                      "around column marks).", [], "blocking")
        return out

    def size(self, f: Box, axis: str) -> tuple[float, list[str]]:
        """The written size across a frame, from a dimension whose ends lie on its two opposite sides."""
        lo, hi = (f.rect[0], f.rect[2]) if axis == "h" else (f.rect[1], f.rect[3])
        for m in self.dims:
            if m.axis != axis:
                continue
            tol = TOL / (m.scale or 1)
            if abs(m.lo - lo) <= tol and abs(m.hi - hi) <= tol:
                self.used.add(m.handle)
                return m.value, [m.handle]
        return _r1(hi - lo), []

    def offsets(self, f: Box, inner: list[Box]) -> list[str]:
        """Dimensions from a footing edge to a column face (the footing's projection beyond the column)."""
        out = []
        faces_x = {v for c in inner for v in (c.rect[0], c.rect[2])}
        faces_y = {v for c in inner for v in (c.rect[1], c.rect[3])}
        for m in self.dims:
            if m.handle in self.used or m.axis not in ("h", "v"):
                continue
            edges = (f.rect[0], f.rect[2]) if m.axis == "h" else (f.rect[1], f.rect[3])
            faces = faces_x if m.axis == "h" else faces_y
            ends = (m.lo, m.hi)
            if (any(abs(e - g) <= TOL for e in ends for g in edges)
                    and any(abs(e - c) <= TOL for e in ends for c in faces)):
                self.used.add(m.handle)
                out.append(m.handle)
        return out

    # ------------------------------------------------------------ founding depth
    def depths(self, rec: FoundationRecognition, footings) -> None:
        self.bands: list[tuple[tuple[float, float, float, float], float, list[str]]] = []
        founding, thick = [], []
        for gl in [m for m in level_marks(self.ents, self.h) if m.kind == "ground"]:
            for m in self.dims:
                if m.axis != "v" or not m.scale:
                    continue
                tol = TOL / m.scale
                if abs(m.hi - gl.y) > tol or m.handle in self.used:
                    continue
                bottom = m.lo
                self.used.add(m.handle)
                founding.append((m, gl))
                for t in self.dims:
                    if (t.axis == "v" and t.handle not in self.used and abs(t.lo - bottom) <= tol
                            and t.hi < gl.y - tol):
                        self.used.add(t.handle)
                        thick.append(t)
                        line = self.h.touching(t.lo_point, tol) + self.h.touching(m.lo_point, tol)
                        x0 = min([s.x0 for s in line] + [m.lo_point[0], t.lo_point[0]])
                        x1 = max([s.x1 for s in line] + [m.lo_point[0], t.lo_point[0]])
                        self.bands.append(((x0, bottom, x1, t.hi), gl.y, [m.handle, t.handle, *gl.handles]))
        depth_values = sorted({m.value for m, _ in founding})
        thick_values = sorted({t.value for t in thick})
        if not footings:
            return
        alike = len({(fo.width_mm, fo.length_mm) for fo, _ in footings}) == 1
        handles = [m.handle for m, _ in founding] + [t.handle for t in thick]
        if len(depth_values) == 1 and len(thick_values) <= 1 and alike:
            for fo, _ in footings:
                fo.founding_depth_mm = depth_values[0]
                fo.depth_mm = thick_values[0] if thick_values else None
                fo.evidence.handles += [h for h in handles if h not in fo.evidence.handles]
            if not thick_values:
                self.find("footing-depth-missing", "footing_depth", "The footing section gives the founding depth "
                          "but no footing thickness.", handles, "check")
        elif founding:
            self.find("footing-depth-which", "footing_depth",
                      f"Footing sections give founding depths {', '.join(f'{v:g}' for v in depth_values)} and "
                      f"thicknesses {', '.join(f'{v:g}' for v in thick_values) or '-'} mm; which footing each applies "
                      "to is not settled.", handles, "check")
        else:
            self.find("footing-depth-none", "footing_depth", "No dimension from GL to the bottom of a footing; the "
                      "founding depth is not known.", [], "blocking")

    # ------------------------------------------------------------ column sections
    def column_sections(self, frames, cols, circles, rec) -> list[tuple[Column, Box, list]]:
        out = []
        for f in frames:
            bars = [c for c in circles if inside(c.points[0], f.rect)]
            if len(bars) < MIN_BARS or any(inside(c.center, f.rect) for c in cols):
                continue
            width, wd = self.size(f, "h")
            depth, dd = self.size(f, "v")
            if not wd or not dd:
                self.find(f"column-section-size-{f.handles[0]}", "column_size", "A column section is not "
                          f"dimensioned on both sides; it is drawn {width:g} x {depth:g} drawing units.",
                          f.handles, "check")
            cid = f"CS{len(out) + 1}"
            col = Column(id=cid, center=_r(f.center), width_mm=width, depth_mm=depth,
                         evidence=Evidence(sheet=self.src, handles=f.handles + wd + dd + [b.handle for b in bars],
                                           note=f"typical column section with {len(bars)} bars drawn"))
            rec.column_sections.append(col)
            out.append((col, f, bars))
        return out

    # ------------------------------------------------------------ rebar
    def rebar(self, rec, footings, sections, circles) -> None:
        notes = [t for t in texts(self.ents) if not level_kind(t.text)]
        lds = leaders(notes, self.segs, arrows=self.arrows)
        items: list[tuple[str, list[tuple[float, float]], list[str], float]] = []
        for note in notes:
            parsed = rebar_notes.parse(flat(note.text))
            if parsed.is_rebar:
                ld = lds.get(note.handle)
                items.append((note.handle, ld.tips if ld else [], [note.handle] + (ld.handles if ld else []),
                              (note.height or 0.0) * LEADER_REACH))
        for m in self.dims:
            if m.overridden and rebar_notes.parse(m.text).is_rebar:
                self.used.add(m.handle)
                items.append((m.handle, [m.p1, m.p2], [m.handle], 0.0))
        texts_by = {t.handle: flat(t.text) for t in notes}
        for handle, tips, handles, reach in items:
            text = texts_by.get(handle) or next(m.text for m in self.dims if m.handle == handle)
            parsed = rebar_notes.parse(text)
            members, near = self.members_at(tips, footings, sections, reach)
            if near:
                self.find(f"rebar-near-{handle}", "rebar", f"The note \"{text}\" points just outside "
                          f"{', '.join(m for m, _ in members)}; it is tied to the nearest member drawn.",
                          handles, "info")
            if not members:
                self.find(f"rebar-unlinked-{handle}", "rebar", f"The note \"{text}\" could not be tied to a member "
                          "(no leader into a footing or column drawing).", handles, "check")
                members = [("unlinked", None)]
            if parsed.bar_mark:
                self.find(f"rebar-bar-mark-{handle}", "rebar", f"The note \"{text}\" gives the bar as "
                          f"\"{parsed.bar_mark}\", a bar number: its diameter depends on the convention.",
                          handles, "check", [f"{parsed.bar_mark[1:]} mm", "US/ASTM bar size"])
            for member, section in members:
                count = parsed.count
                role = parsed.role
                note = None
                if section is not None and parsed.bar_dia_mm and role in (None, "main"):
                    bars = [c for c in section[2] if any(math.dist(c.points[0], t) <= (c.radius or 0) * 2 + TOL
                                                         for t in tips)]
                    if bars or role is None:
                        role = role or "longitudinal"
                        if count is None:
                            count = len(section[2])
                            note = f"count = the {count} bars drawn in the section"
                rec.rebar.append(RebarSpec(member=member, text=text, bar_dia_mm=parsed.bar_dia_mm,
                                           spacing_mm=parsed.spacing_mm, count=count, legs=parsed.legs, role=role,
                                           cover_mm=parsed.cover_mm,
                                           evidence=Evidence(sheet=self.src, handles=handles, note=note)))

    def members_at(self, tips, footings, sections, reach: float) -> tuple[list[tuple[str, tuple | None]], bool]:
        """Members the tips point into; a tip just outside every member (within reach) takes the nearest one."""
        regions: list[tuple[str, tuple, tuple | None]] = []
        for col, f, bars in sections:
            regions.append((_column_name(col), f.rect, (col, f, bars)))
        name = _footing_name(footings)
        for rect, gl_y, _ in self.bands:
            regions.append((name, rect, None))
            regions.append((f"column on {name}", (rect[0], rect[3], rect[2], gl_y + (gl_y - rect[1])), None))
        out: list[tuple[str, tuple | None]] = []
        near = False
        for tip in tips:
            hit = next(((n, sec) for n, rect, sec in regions if inside(tip, rect, TOL)), None)
            if hit is None and regions and reach > 0:
                d, n, sec = min(((rect_distance(tip, rect), n, sec) for n, rect, sec in regions), key=lambda x: x[0])
                if d <= reach:
                    hit, near = (n, sec), True
            if hit and hit[0] not in [o[0] for o in out]:
                out.append(hit)
        return out, near


def _column_name(col: Column) -> str:
    a, b = sorted((col.width_mm, col.depth_mm))
    return f"column {a:g}x{b:g}"


def _footing_name(footings) -> str:
    if len(footings) == 1:
        fo = footings[0][0]
        return f"{fo.kind} {fo.id}" if fo.kind != "unknown" else fo.id
    return "footing"


def _r(p) -> tuple[float, float]:
    return (round(p[0], 1), round(p[1], 1))


def _r1(v: float) -> float:
    return round(v, 1)
