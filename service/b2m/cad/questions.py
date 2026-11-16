"""Import questions (PRD Step 9, stage 5): every finding of every sheet, and every input the build needs that the
drawings do not give, as questions an architect can answer.

Findings are deduplicated and grouped: the same finding repeated (mirrored flats, both sections, several sheets)
becomes one question listing each place (``instances``); a mark drawn at several widths is one question per mark.
Each question says where (sheet, handles, a short "where"), offers options when the answer is a choice, has an
``answer_type`` (see ``b2m.cad.answers``) and a severity:

- blocking: must be answered (or assumed) before building;
- check: the model can be built, but the answer changes it or its compliance (the user should look);
- info: said, not asked.

Input questions every import has: the project location (with towns the site plan names as suggestions, never set
from them), the governing rule set (derived from the location's profile), door heights per mark, window sills and
heads per mark (from sections where a section identifies the mark), wall height, lintels, and slab thickness or
storey heights when no section gives them, plus the clear-height check against the governing rules.

Question ids are stable (derived from what they ask), so re-running the import keeps the answers.
"""

from __future__ import annotations

import hashlib
import re
from collections import Counter, defaultdict
from dataclasses import dataclass
from typing import Any, Iterable

from shapely.geometry import Point as SPoint, Polygon

from b2m.cad import assumptions, crosssheet
from b2m.cad.building import Evidence as BEvidence, Finding, Opening, PlanRecognition, RecognisedBuilding
from b2m.cad.project import Evidence, ImportProject, Question, _now
from b2m.cad.recognise.text import OPEN_TO_SKY_RE
from b2m.rules import rule_set as load_rule_set

SEVERITIES = ("blocking", "check", "info")
_RANK = {s: i for i, s in enumerate(SEVERITIES)}
T2_ID = re.compile(r"^Q\d+$")  # questions from reading and classifying the sheets (b2m.cad.project.analyse)


@dataclass
class FRef:
    sheet: str
    finding: Finding

    @property
    def ref(self) -> str:
        return f"{self.sheet}#{self.finding.id}"


def _ev(e: BEvidence) -> Evidence:
    return Evidence(sheet=e.sheet, handles=list(e.handles), note=e.note)


def _slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def _span(ns: Iterable[int]) -> str:
    return crosssheet._span(list(ns))


def _q(id: str, topic: str, text: str, severity: str, evidence=(), options=(), where=None, instances=(),
       findings=(), answer_type="choice", key=None) -> Question:
    return Question(id=id, topic=topic, text=text, severity=severity, evidence=[_ev(e) if isinstance(e, BEvidence)
                                                                                  else e for e in evidence],
                    options=list(options), where=where, instances=list(instances), findings=list(findings),
                    answer_type=answer_type, key=key or {})


# ---------------------------------------------------------------- describing places

class Describe:
    """Plain words for sheets and places on them."""

    def __init__(self, b: RecognisedBuilding):
        self.b = b
        self.plans = {p.sheet: p for p in b.plans}
        self.sections = {s.sheet: s for s in b.sections}

    def sheet(self, sheet: str) -> str:
        p = self.plans.get(sheet)
        if p is not None:
            if p.role == "roof_plan":
                what = "roof plan"
            elif p.storeys == [0]:
                what = "ground floor plan"
            elif p.storeys:
                what = f"floor plan, storey{'s' if len(p.storeys) > 1 else ''} {_span(p.storeys)}"
            else:
                what = "floor plan"
            return f"{what} ({sheet})"
        if sheet in self.sections:
            return f"section {self.sections[sheet].name} ({sheet})"
        if any(f.sheet == sheet for f in self.b.foundations):
            return f"foundation sheet ({sheet})"
        if any(bl.sheet == sheet for bl in self.b.beam_layouts):
            return f"beam layout ({sheet})"
        return sheet or "the drawing set"

    def near(self, plan: PlanRecognition, point) -> str:
        """The rooms around a point: "between BED ROOM and TOILET", "in LIVING ROOM", or the coordinates."""
        pt = SPoint(point)
        names = []
        for r in plan.rooms:
            if r.boundary and len(r.boundary) >= 3:
                if Polygon(r.boundary).exterior.distance(pt) <= 400 or Polygon(r.boundary).contains(pt):
                    names.append(r.name)
        names = list(dict.fromkeys(names))
        if not names:
            close = sorted(plan.rooms, key=lambda r: pt.distance(SPoint(r.point)))
            names = [r.name for r in close[:1] if pt.distance(SPoint(r.point)) <= 4000]
            if names:
                return f"near {names[0]}"
            return f"at ({point[0]:.0f}, {point[1]:.0f})"
        if len(names) == 1:
            return f"in {names[0]}"
        return "between " + " and ".join([", ".join(names[:-1]), names[-1]])


def _openings_for(plan: PlanRecognition, f: Finding) -> list[Opening]:
    hs = set(f.evidence.handles)
    return [o for o in plan.openings if hs & set(o.evidence.handles)]


# ---------------------------------------------------------------- findings

def all_findings(b: RecognisedBuilding) -> list[FRef]:
    out = [FRef(p.sheet, f) for p in b.plans for f in p.findings]
    out += [FRef(s.sheet, f) for s in b.sections for f in s.findings]
    out += [FRef(fo.sheet, f) for fo in b.foundations for f in fo.findings]
    out += [FRef(bl.sheet, f) for bl in b.beam_layouts for f in bl.findings]
    out += [FRef(f.evidence.sheet, f) for f in b.findings]
    return out


def _worst(refs: list[FRef]) -> str:
    return min((r.finding.severity for r in refs), key=lambda s: _RANK[s])


def finding_questions(b: RecognisedBuilding, d: Describe) -> tuple[list[Question], dict]:
    """Questions from the findings; also returns what input questions need (section windows, door heights)."""
    refs = all_findings(b)
    by_topic: dict[str, list[FRef]] = defaultdict(list)
    for r in refs:
        by_topic[r.finding.topic].append(r)
    out: list[Question] = []
    extra: dict[str, Any] = {"window_findings": by_topic.pop("window_sill", [])}
    by_topic.pop("rule", None)  # clear heights are asked against the governing rules (clear_height_question)
    out += _mark_widths(b, d, by_topic.pop("mark_width_mismatch", []))
    out += _unmarked(b, d, by_topic.pop("opening_without_mark", []))
    out += _not_in_wall(b, d, by_topic.pop("door_not_in_wall", []))
    out += _rooms_share(b, d, by_topic.pop("rooms_share_space", []))
    beam = by_topic.pop("beam_depth", [])
    out += _beam_depth(b, d, [r for r in beam if r.sheet in d.sections])
    out += _tie_beam_depth(b, d, [r for r in beam if r.sheet not in d.sections])
    out += _roof_structure(b, d, by_topic.pop("roof_structure", []))
    out += _layers(b, d, by_topic.pop("foundation_layer", []))
    for topic, rs in by_topic.items():
        out += _generic(d, topic, rs)
    return out, extra


def _mark_widths(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    out = []
    by_mark: dict[tuple[str, str], list[tuple[PlanRecognition, Opening]]] = defaultdict(list)
    for p in b.plans:
        for o in p.openings:
            if o.mark_normalised:
                by_mark[(o.kind, o.mark_normalised)].append((p, o))
    for (kind, mark), items in sorted(by_mark.items(), key=lambda kv: kv[0][1]):
        widths = Counter(o.width_mm for _, o in items)
        if len(widths) < 2:
            continue
        order = [w for w, _ in sorted(widths.items(), key=lambda wc: (-wc[1], wc[0]))]
        keep, others = order[0], order[1:]
        sheets = sorted({p.sheet for p, _ in items})
        mine = [r for r in refs if re.search(rf"\bmarked {re.escape(mark)}\b", r.finding.text, re.I)]
        counts = ", ".join(f"{w:g} mm ({widths[w]}x)" for w in order)
        text = (f"{kind.capitalize()}s marked {mark} are drawn at {len(order)} widths: {counts}, on "
                f"{', '.join(d.sheet(s) for s in sheets)}. A mark is one {kind} type. Which width is {mark}?")
        split = (f"two types: {mark} {keep:g} mm and a new mark for the {others[0]:g} mm ones" if len(others) == 1
                 else f"separate types: {mark} {keep:g} mm and new marks for the "
                      f"{', '.join(f'{w:g}' for w in others)} mm ones")
        odd = [(p, o) for p, o in items if o.width_mm != keep]
        out.append(_q(
            f"mark-width-{_slug(mark)}", "mark_width", text, "blocking",
            evidence=[o.evidence for _, o in odd] + [r.finding.evidence for r in mine],
            options=[f"{w:g} mm" for w in order] + [split],
            where=f"the {sum(widths[w] for w in others)} {kind}s marked {mark} not {keep:g} mm wide",
            instances=[f"{o.id} {o.width_mm:g} mm, {d.near(p, o.center)} ({p.sheet})" for p, o in odd],
            findings=[r.ref for r in mine], answer_type="mark_width",
            key={"mark": mark, "kind": kind, "widths": order, "keep": keep}))
    return out


def _marks_of(b: RecognisedBuilding, kind: str) -> dict[str, list[float]]:
    marks: dict[str, set[float]] = defaultdict(set)
    for p in b.plans:
        for o in p.openings:
            if o.kind == kind and o.mark_normalised:
                marks[o.mark_normalised].add(o.width_mm)
    return {m: sorted(ws) for m, ws in sorted(marks.items())}


def _unmarked(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    groups: dict[tuple[str, float], list[tuple[FRef, PlanRecognition, Opening]]] = defaultdict(list)
    loose = []
    for r in refs:
        plan = d.plans.get(r.sheet)
        ops = _openings_for(plan, r.finding) if plan else []
        if not ops:
            loose.append(r)
            continue
        for o in ops:
            groups[(o.kind, o.width_mm)].append((r, plan, o))
    out = []
    for (kind, width), items in sorted(groups.items()):
        marks = _marks_of(b, kind)
        same = [m for m, ws in marks.items() if width in ws]
        options = [f"{m} ({', '.join(f'{w:g}' for w in marks[m])} mm)" for m in same + [m for m in marks if m not in same]]
        sheets = sorted({p.sheet for _, p, _ in items})
        n = len(items)
        out.append(_q(
            f"opening-mark-{kind}-{width:g}", "opening_without_mark",
            f"{n} {kind}{'s' if n > 1 else ''} {width:g} mm wide on {', '.join(d.sheet(s) for s in sheets)} "
            f"{'have' if n > 1 else 'has'} no mark. Which type {'are they' if n > 1 else 'is it'}?",
            "blocking", evidence=[o.evidence for _, _, o in items], options=options + ["a new mark (give it)"],
            where="; ".join(f"{o.id} {d.near(p, o.center)} ({p.sheet})" for _, p, o in items),
            instances=[f"{o.id} {d.near(p, o.center)} ({p.sheet})" for _, p, o in items],
            findings=sorted({r.ref for r, _, _ in items}), answer_type="opening_mark",
            key={"openings": [[p.sheet, o.id] for _, p, o in items], "kind": kind}))
    return out + _generic(d, "opening_without_mark", loose)


def _not_in_wall(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    groups: dict[float, list[tuple[FRef, PlanRecognition, Opening]]] = defaultdict(list)
    loose = []
    for r in refs:
        plan = d.plans.get(r.sheet)
        ops = _openings_for(plan, r.finding) if plan else []
        if not ops:
            loose.append(r)
        for o in ops:
            groups[o.width_mm].append((r, plan, o))
    out = []
    for width, items in sorted(groups.items()):
        n = len(items)
        sheets = sorted({p.sheet for _, p, _ in items})
        out.append(_q(
            f"door-not-in-wall-{width:g}", "door_not_in_wall",
            f"{n} door{'s' if n > 1 else ''} {width:g} mm wide on {', '.join(d.sheet(s) for s in sheets)} "
            f"close{'' if n > 1 else 's'} a gap between walls but lie{'' if n > 1 else 's'} in no wall (the leaf "
            "does not run along any wall's line). A door needs a wall to sit in: which wall holds "
            f"{'them' if n > 1 else 'it'}, or is a wall missing?", "blocking",
            evidence=[o.evidence for _, _, o in items],
            options=["each sits in the wall gap it closes (the gap's walls are its host)",
                     "a wall is missing across each gap", "other (describe)"],
            where="; ".join(f"{o.id} {d.near(p, o.center)} ({p.sheet})" for _, p, o in items),
            instances=[f"{o.id} {d.near(p, o.center)} ({p.sheet})" for _, p, o in items],
            findings=sorted({r.ref for r, _, _ in items}), answer_type="choice_or_text",
            key={"openings": [[p.sheet, o.id] for _, p, o in items]}))
    return out + _generic(d, "door_not_in_wall", loose)


def _name_key(name: str) -> str:
    return " ".join(sorted(name.upper().split()))


def _rooms_share(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    groups: dict[tuple, list[tuple[FRef, PlanRecognition, list]]] = defaultdict(list)
    loose = []
    for r in refs:
        plan = d.plans.get(r.sheet)
        hs = set(r.finding.evidence.handles)
        rooms = [rm for rm in plan.rooms if hs & set(rm.evidence.handles)] if plan else []
        if not rooms:
            loose.append(r)
            continue
        groups[tuple(sorted(_name_key(rm.name) for rm in rooms))].append((r, plan, rooms))
    out = []
    for key, items in sorted(groups.items()):
        first_rooms = items[0][2]
        variants: dict[str, list[str]] = defaultdict(list)
        for _, _, rooms in items:
            for rm in rooms:
                if rm.name not in variants[_name_key(rm.name)]:
                    variants[_name_key(rm.name)].append(rm.name)
        per = Counter(_name_key(rm.name) for rm in first_rooms)
        shown = []
        for k in dict.fromkeys(_name_key(rm.name) for rm in first_rooms):
            label = f'"{variants[k][0]}"' + (f" (x{per[k]})" if per[k] > 1 else "")
            if len(variants[k]) > 1:
                label += " (also written " + ", ".join(f'"{v}"' for v in variants[k][1:]) + ")"
            shown.append(label)
        areas = [m.group(1) for r, _, _ in items for m in [re.search(r"\(([\d.]+) m2\)", r.finding.text)] if m]
        n = len(items)
        sheets = sorted({p.sheet for _, p, _ in items})

        def where(r, p, rooms):
            xs = [rm.point[0] for rm in rooms]
            ys = [rm.point[1] for rm in rooms]
            area = re.search(r"\(([\d.]+) m2\)", r.finding.text)
            return (f"{', '.join(rm.name for rm in rooms)} around ({sum(xs) / len(xs):.0f}, {sum(ys) / len(ys):.0f})"
                    f"{', ' + area.group(1) + ' m2' if area else ''} ({p.sheet})")
        area_txt = (f", {min(areas, key=float)}-{max(areas, key=float)} m2 each" if len(set(areas)) > 1
                    else f", {areas[0]} m2" if areas else "")
        out.append(_q(
            f"rooms-share-space-{_slug('-'.join(key))}", "rooms_share_space",
            f"Rooms labelled {', '.join(shown)} share one enclosed space with no wall between them, in "
            f"{n} place{'s' if n > 1 else ''} on {', '.join(d.sheet(s) for s in sheets)}{area_txt}. Are they one "
            "room, or where does one end and the next begin (room separation lines for each place)?",
            "blocking", evidence=[rm.evidence for _, _, rooms in items for rm in rooms],
            options=["one room (give its name)", "separate rooms: give the separation lines for each place"],
            where="; ".join(where(*it) for it in items), instances=[where(*it) for it in items],
            findings=[r.ref for r, _, _ in items], answer_type="rooms",
            key={"instances": [{"ref": r.ref, "sheet": p.sheet, "rooms": [rm.id for rm in rooms]}
                               for r, p, rooms in items]}))
    return out + _generic(d, "rooms_share_space", loose)


def _sections_named(d: Describe, refs: list[FRef]) -> str:
    names = sorted({d.sections[r.sheet].name for r in refs if r.sheet in d.sections})
    return f"section{'s' if len(names) > 1 else ''} {' and '.join(names)}"


def _beam_depth(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    main = [r for r in refs if r.finding.id == "section-beam-depth"]
    out = []
    if main:
        groups: dict[tuple, list[FRef]] = defaultdict(list)
        for r in main:
            groups[tuple(r.finding.options)].append(r)
        for n, (opts, rs) in enumerate(groups.items()):
            s0 = d.sections[rs[0].sheet]
            v, t = s0.beam_depth_mm, s0.slab_thickness_mm
            overall = {opts[0]: v + t, opts[1]: v} if v is not None and t is not None and len(opts) == 2 else {}
            out.append(_q(
                "beam-depth" if n == 0 else f"beam-depth-{n + 1}", "beam_depth",
                f"{rs[0].finding.text} (dimensioned on {_sections_named(d, rs)}.)", "blocking",
                evidence=[r.finding.evidence for r in rs], options=list(opts),
                where=", ".join(d.sheet(r.sheet) for r in rs), findings=[r.ref for r in rs],
                key={"sections": [r.sheet for r in rs], "overall_by_option": overall,
                     "not_assumable": "beam_depth"}))
    return out + _generic(d, "beam_depth", [r for r in refs if r not in main])


def _tie_beam_depth(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    main = [r for r in refs if r.finding.id == "beam-depth-missing"]
    out = []
    if main:
        layouts = [bl for bl in b.beam_layouts if bl.sheet in {r.sheet for r in main}]
        n = sum(len(bl.beams) for bl in layouts)
        widths = sorted({bm.width_mm for bl in layouts for bm in bl.beams if bm.width_mm})
        out.append(_q(
            "tie-beam-depth", "beam_depth",
            f"The beam layout draws {n} beams ({', '.join(f'{w:g}' for w in widths)} mm wide) but does not give "
            "their depth. How deep are they (overall, mm)?", "blocking",
            evidence=[r.finding.evidence for r in main], where=", ".join(d.sheet(r.sheet) for r in main),
            findings=[r.ref for r in main], answer_type="length",
            key={"sets": "tie_beam_depth", "sheets": [r.sheet for r in main], "not_assumable": "tie_beam_depth"}))
    return out + _generic(d, "beam_depth", [r for r in refs if r not in main])


def _roof_structure(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    if not refs:
        return []
    heights = sorted({float(h) for r in refs for h in re.findall(r"(\d+(?:\.\d+)?)(?=[, ]*(?:mm|,))",
                                                                  r.finding.text.split("above roof")[0])})
    roof_rooms = [(p, rm) for p in b.plans if p.role == "roof_plan" for rm in p.rooms
                  if not OPEN_TO_SKY_RE.search(rm.name)]  # a terrace is the roof itself, not something on it
    text = (f"Something stands on the roof, dimensioned {', '.join(f'{h:g}' for h in heights)} mm above roof level on "
            f"{_sections_named(d, refs)}, and does not reach the building's edge. What is it")
    options = list(dict.fromkeys(o for r in refs for o in r.finding.options))
    evidence = [r.finding.evidence for r in refs]
    if roof_rooms:
        names = list(dict.fromkeys(rm.name for _, rm in roof_rooms))
        text += (f", and which height belongs to which? The roof plan names "
                 f"{' and '.join(chr(34) + n + chr(34) for n in names)} "
                 f"({', '.join(sorted({p.sheet for p, _ in roof_rooms}))}).")
        options = [f"{' and '.join(names)} (as the roof plan names them; say which height is which)"] + options
        evidence += [rm.evidence for _, rm in roof_rooms]
    text = text if text.endswith(".") else text + "?"
    return [_q("roof-structure", "roof_structure", text, "blocking", evidence=evidence, options=options,
               where=", ".join(d.sheet(r.sheet) for r in refs), findings=[r.ref for r in refs],
               answer_type="choice_or_text")]


def _layers(b: RecognisedBuilding, d: Describe, refs: list[FRef]) -> list[Question]:
    groups: dict[str, list[FRef]] = defaultdict(list)
    loose = []
    for r in refs:
        m = re.search(r'note "([^"]+)"', r.finding.text)
        if m and "thickness" in r.finding.text:
            groups[m.group(1).upper()].append(r)
        else:
            loose.append(r)
    out = []
    for layer, rs in groups.items():
        drawn = next((o for r in rs for o in r.finding.options if "drawn" in o), None)
        out.append(_q(
            f"layer-{_slug(layer)}", "foundation_layer",
            f"The note \"{layer}\" points into the floor build-up on {_sections_named(d, rs)} but gives no "
            "thickness. How thick is it (mm)?", "check", evidence=[r.finding.evidence for r in rs],
            options=list(dict.fromkeys(o for r in rs for o in r.finding.options)),
            where=", ".join(d.sheet(r.sheet) for r in rs), findings=[r.ref for r in rs], answer_type="length",
            key={"sets": "layer", "layer": layer, "sections": [r.sheet for r in rs], "assume": "take_drawn_value",
                 "drawn_option": drawn, "what": f"{layer.capitalize()} thickness"}))
    return out + _generic(d, "foundation_layer", loose)


def _template(text: str) -> str:
    t = re.sub(r'"[^"]*"', '"…"', text)
    t = re.sub(r"\b[\w.]*\d[\w.]*\b", "#", t)
    return t


def _generic(d: Describe, topic: str, refs: list[FRef]) -> list[Question]:
    groups: dict[str, list[FRef]] = defaultdict(list)
    for r in refs:
        groups[_template(r.finding.text)].append(r)
    out = []
    for tmpl, rs in groups.items():
        first = rs[0].finding
        sheets = list(dict.fromkeys(r.sheet for r in rs))
        if len(rs) == 1:
            text = first.text
        elif len({r.finding.text for r in rs}) == 1:
            text = f"{first.text} (the same on {', '.join(d.sheet(s) for s in sheets)}.)"
        else:
            text = f"{len(rs)} alike on {', '.join(d.sheet(s) for s in sheets)}: {first.text}"
        digest = hashlib.sha1(f"{topic}|{tmpl}".encode()).hexdigest()[:6]
        out.append(_q(
            f"{_slug(topic)}-{digest}", topic, text, _worst(rs), evidence=[r.finding.evidence for r in rs],
            options=list(dict.fromkeys(o for r in rs for o in r.finding.options)),
            where=", ".join(d.sheet(s) for s in sheets),
            instances=[f"{r.finding.text} ({r.sheet})" for r in rs] if len(rs) > 1 else [],
            findings=[r.ref for r in rs], answer_type="choice_or_text" if any(r.finding.options for r in rs)
            else "text"))
    return out


# ---------------------------------------------------------------- input questions

def location_question(project: ImportProject, suggestions: list[assumptions.Suggestion] | None) -> Question:
    options, evidence, where = [], [], None
    for s in suggestions or []:
        options.append(s.label)
        for sheet, handle, text in s.mentions:
            evidence.append(Evidence(sheet=sheet, handles=[handle], note=f"site plan text \"{text}\""))
    if suggestions:
        where = "; ".join(f"{s.label}: site plan text " + ", ".join(f"\"{t}\" ({sh})" for sh, _, t in s.mentions)
                          for s in suggestions)
    text = ("Where is the project: city or municipality, and state? B2M needs it before it can assume anything: "
            "the governing building rules and the typical values come from the location.")
    if suggestions:
        text += (" The site plan names " + ", ".join(s.place for s in suggestions)
                 + "; these are suggestions only, not taken as the location.")
    return _q("location", "location", text, "blocking", evidence=evidence,
              options=options + ["another place (give the city or municipality and the state)"], where=where,
              answer_type="location", key={"not_assumable": "location"})


def rule_set_question(project: ImportProject) -> Question:
    loc = project.inputs.location
    g = assumptions.governing(loc, project.inputs.values.get("country")) if loc else None
    if g is None:
        why = ("answer the location question first" if loc is None else
               "no location profile covers this location: name the rules (and which profile or values B2M should "
               "assume from)")
        return _q("rule-set", "rule_set", "Which building rules govern this project? They follow from the location: "
                  f"{why}.", "check", answer_type="rule_set", key={"not_assumable": "rule_set"})
    prof, refs = g
    missing = [r.name for r in refs if r.data is None]
    derived = " and ".join(r.name for r in refs)
    text = (f"Which building rules govern this project? From its location "
            f"({', '.join(x for x in (loc.city, loc.municipality, loc.state) if x)}), the {prof.name} profile gives "
            f"{derived}" + (f"; {', '.join(missing)} is not in B2M's rule data yet, so compliance checks use only "
                            f"{', '.join(r.name for r in refs if r.data)}" if missing else "") + ". Confirm, or name "
            "the rules.")
    option = f"{derived} [{' + '.join(r.id for r in refs)}]"
    return _q("rule-set", "rule_set", text, "check", options=[option, "other rules (name them)"],
              answer_type="rule_set",
              key={"derived": [r.id for r in refs], "option_ids": {option: [r.id for r in refs]},
                   "assume": "rule_set"})


def _by_mark(b: RecognisedBuilding, kinds: set[str]) -> dict[str, list[tuple[PlanRecognition, Opening]]]:
    out: dict[str, list] = defaultdict(list)
    for p in b.plans:
        for o in p.openings:
            if o.kind in kinds and o.mark_normalised and not crosssheet._is_lift_door(o):
                out[o.mark_normalised].append((p, o))
    return dict(sorted(out.items()))


def door_height_questions(b: RecognisedBuilding, d: Describe) -> list[Question]:
    out = []
    section_doors = [(s, h) for s in b.sections for h in s.door_heights_mm]
    for mark, items in _by_mark(b, {"door"}).items():
        widths = sorted({o.width_mm for _, o in items})
        sheets = sorted({p.sheet for p, _ in items})
        text = (f"How high are the doors marked {mark} ({', '.join(f'{w:g}' for w in widths)} mm wide, "
                f"{len(items)} on {', '.join(d.sheet(s) for s in sheets)})? The plans give widths only")
        options = []
        if section_doors:
            text += ("; the sections dimension door heights of "
                     + ", ".join(f"{h:g} mm (section {s.name})" for s, h in section_doors)
                     + " without saying which mark")
            options = list(dict.fromkeys(f"{h:g} mm (as section {s.name} dimensions a door)" for s, h in section_doors))
        else:
            text += ", and no section dimensions a door height"
        out.append(_q(f"door-height-{_slug(mark)}", "door_height", text + ".", "blocking",
                      evidence=[o.evidence for _, o in items[:4]], options=options,
                      where=f"doors marked {mark}", answer_type="door_height",
                      key={"mark": mark, "kind": "door", "assume": "door_height_mm"}))
    lifts = [(p, o) for p in b.plans for o in p.openings if crosssheet._is_lift_door(o)]
    if lifts:
        widths = sorted({o.width_mm for _, o in lifts})
        sheets = sorted({p.sheet for p, _ in lifts})
        out.append(_q("door-height-lift", "door_height",
                      f"How high are the {len(lifts)} lift landing doors ({', '.join(f'{w:g}' for w in widths)} mm "
                      f"wide, on {', '.join(d.sheet(s) for s in sheets)})? The drawings give widths only; lift "
                      "doors come from the lift maker's data.", "blocking",
                      evidence=[o.evidence for _, o in lifts[:4]], where="lift landing doors",
                      answer_type="door_height", key={"lift": True, "not_assumable": "lift_door_height"}))
    return out


def window_questions(b: RecognisedBuilding, d: Describe, window_findings: list[FRef]) -> list[Question]:
    out = []
    shown = [(s, k, sill, head, hs) for s in b.sections for k, sill, head, hs in crosssheet.section_windows(s)]
    identified = {r.id: r for r in b.resolved if r.topic == "window_size"}
    for mark, items in _by_mark(b, {"window", "ventilator"}).items():
        kinds = sorted({o.kind for _, o in items})
        widths = sorted({o.width_mm for _, o in items})
        sheets = sorted({p.sheet for p, _ in items})
        res = [r for r in identified.values() if re.search(rf"\bmarked {re.escape(mark)}:", r.text)]
        noun = "windows" if kinds == ["window"] else "ventilators" if kinds == ["ventilator"] else "openings"
        text = (f"What are the sill and head heights (above the floor) of the {noun} marked {mark} "
                f"({', '.join(f'{w:g}' for w in widths)} mm wide, {len(items)} on "
                f"{', '.join(d.sheet(s) for s in sheets)})? The plans give widths only")
        options, severity, evidence = [], "blocking", [o.evidence for _, o in items[:4]]
        if res:
            o0 = items[0][1]
            text = (f"Section heights are taken for the {noun} marked {mark}: sill {o0.sill_mm:g} mm, head "
                    f"{o0.sill_mm + o0.height_mm:g} mm ({res[0].text}) Confirm, or give the heights")
            options.append(f"sill {o0.sill_mm:g} mm, head {o0.sill_mm + o0.height_mm:g} mm (as the section shows)")
            severity = "check"
            evidence += [e for r in res for e in r.evidence]
        elif shown:
            text += ("; " + "; ".join(f"section {s.name} dimensions a window in storey {k} at sill {sill:g} mm, head "
                                      f"{head:g} mm" for s, k, sill, head, _ in shown)
                     + ", without saying which mark")
            options += list(dict.fromkeys(f"sill {sill:g} mm, head {head:g} mm (as section {s.name} shows a window "
                                          f"in storey {k})" for s, k, sill, head, _ in shown))
            evidence += [Evidence(sheet=s.sheet, handles=hs, note=f"window in storey {k}") for s, k, _, _, hs in shown]
        out.append(_q(f"window-size-{_slug(mark)}", "window_size", text + ".", severity, evidence=evidence,
                      options=options, where=f"{noun} marked {mark}", findings=[r.ref for r in window_findings],
                      answer_type="window_size", key={"mark": mark, "kinds": kinds, "assume": "window"}))
    return out


def building_questions(b: RecognisedBuilding, d: Describe) -> list[Question]:
    out = [
        _q("wall-height", "wall_height",
           "How high do the walls run? The plans draw walls in plan only and the sections give floor-to-floor "
           "heights, slab and beam depths, not where the walls stop.", "blocking",
           options=["floor to the underside of the slab above (beams within the wall thickness)",
                    "floor to the underside of the beam where one runs above, else to the slab",
                    "other (give the height)"],
           answer_type="choice_or_text", key={"assume": "wall_height", "what": "wall height"}),
        _q("lintel", "lintel",
           "Lintels over doors and windows: a lintel over each opening, or a continuous lintel band, and at what "
           "level? The drawings do not show lintels.", "check",
           options=["a lintel over each opening, at its head", "a continuous lintel band at door-head level",
                    "none to model"], answer_type="choice_or_text", key={"assume": "lintel", "what": "lintels"}),
    ]
    if b.sections and not any(s.slab_thickness_mm for s in b.sections):
        out.append(_q("slab-thickness", "slab_thickness", "How thick are the floor and roof slabs? No section "
                      "dimensions the slab.", "blocking", evidence=[BEvidence(sheet=s.sheet) for s in b.sections],
                      answer_type="length", key={"sets": "slab_thickness", "not_assumable": "slab_thickness"}))
    if not b.sections:
        out.append(_q("no-section", "storey_height", "The drawing set has no section: what are the storey heights "
                      "(floor to floor), the slab thickness and the plinth height?", "blocking",
                      answer_type="text", key={"not_assumable": "storey_height"}))
    elif not any(s.storey_heights_mm for s in b.sections):
        out.append(_q("storey-heights", "storey_height", "No section dimensions the storey heights (floor to "
                      "floor). What are they, bottom up?", "blocking", answer_type="text",
                      evidence=[BEvidence(sheet=s.sheet) for s in b.sections],
                      key={"not_assumable": "storey_height"}))
    return out


def clear_height_question(project: ImportProject, b: RecognisedBuilding) -> list[Question]:
    """Floor to floor less the slab, against the clear-height rules of the governing rule sets (NBC 2016 until
    they are known): a compliance check, never a blocker for modelling."""
    from b2m.cad.structure import clear_height_findings

    ids = (project.inputs.rule_set or "").split("+") if project.inputs.rule_set else []
    prof = assumptions.profile_for(project.inputs.location, project.inputs.values.get("country"))
    datas = []
    for i in ids:
        ref = prof.rule_sets.get(i) if prof else None
        data = ref.data if ref else (i if i in ("nbc2016", "kolkata") else None)
        if data:
            datas.append(data)
    provisional = not datas
    datas = datas or ["nbc2016"]
    found = []
    for data in datas:
        if not any(r.get("type") == "clear_height" for r in load_rule_set(data).get("rules", [])):
            continue
        for s in b.sections:
            found += [(s, f) for f in clear_height_findings(s, data)]
    if not found:
        return []
    groups: dict[str, list] = defaultdict(list)
    for s, f in found:
        groups[f.id].append((s, f))
    out = []
    for fid, items in groups.items():
        f = items[0][1]
        names = " and ".join(sorted({s.name for s, _ in items}))
        text = f.text.replace("The section's storeys", f"Section{'s' if len(items) > 1 else ''} {names}: the storeys")
        if provisional:
            text += " (Checked against NBC 2016 until the governing rules are confirmed.)"
        out.append(_q(f"clear-height-{_slug(fid)}", "clear_height", text, "check",
                      evidence=[f.evidence for _, f in items], options=f.options,
                      where=", ".join(f"section {s.name} ({s.sheet})" for s, _ in items),
                      answer_type="choice_or_text", key={"rule": fid, "not_assumable": "clear_height"}))
    return out


# ---------------------------------------------------------------- assembling

def build(project: ImportProject, raw: RecognisedBuilding, applied: RecognisedBuilding | None = None,
          suggestions: list[assumptions.Suggestion] | None = None) -> list[Question]:
    """Every question for the import: inputs first, then the findings, then the build inputs; blocking first.
    ``raw`` is the recognised building (findings), ``applied`` the same with answers applied (marks, heights)."""
    applied = applied or raw
    d = Describe(raw)
    found, extra = finding_questions(raw, d)
    da = Describe(applied)
    qs = [location_question(project, suggestions), rule_set_question(project)]
    qs += found
    qs += door_height_questions(applied, da) + window_questions(applied, da, extra["window_findings"])
    qs += building_questions(applied, da) + clear_height_question(project, applied)
    seen, out = set(), []
    for q in qs:
        if q.id in seen:
            n = 2
            while f"{q.id}-{n}" in seen:
                n += 1
            q.id = f"{q.id}-{n}"
        seen.add(q.id)
        out.append(q)
    return sorted(out, key=lambda q: _RANK[q.severity])


def update(project: ImportProject, raw: RecognisedBuilding,
           suggestions: list[assumptions.Suggestion] | None = None) -> list[Question]:
    """Replace the import questions with a fresh list, keeping every answer given (by question id), the questions
    from reading the sheets, and answered questions that no longer arise (the record)."""
    from b2m.cad import answers

    old = {q.id: q for q in project.questions}
    if suggestions is None and "location" in old:  # keep the suggestions found when the sheets were read
        keep = old["location"]
    else:
        keep = None
    applied = answers.apply(project, raw)
    new = build(project, raw, applied, suggestions)
    for q in new:
        prev = old.get(q.id)
        if q.id == "location" and keep is not None:
            q.options, q.evidence, q.where, q.text = keep.options, keep.evidence, keep.where, keep.text
        if prev is not None and not prev.open:
            q.answer, q.answered_by, q.at = prev.answer, prev.answered_by, prev.at
    new_ids = {q.id for q in new}
    t2 = [q for q in project.questions if T2_ID.match(q.id)]
    for q in t2:  # a sheet whose role is unknown cannot be recognised: that must be settled first
        if q.topic == "sheet_role" and q.options:
            q.severity = "blocking"
    orphans = [q for q in project.questions if not T2_ID.match(q.id) and q.id not in new_ids and not q.open]
    project.questions = t2 + new + orphans
    sync_inputs(project)
    return project.questions


def sync_inputs(project: ImportProject) -> None:
    """Inputs set outside the questions (e.g. the location through the API) answer their questions; the rule-set
    question follows the location."""
    ids = {q.id for q in project.questions}
    if "location" in ids and project.inputs.location is not None:
        q = project.question("location")
        loc = project.inputs.location.model_dump()
        if q.open or q.answer != loc:
            q.answer, q.answered_by, q.at = loc, "user", _now()
    if "rule-set" in ids:
        q = project.question("rule-set")
        fresh = rule_set_question(project)
        if q.open:
            q.text, q.options, q.key = fresh.text, fresh.options, fresh.key


def follow_ups(project: ImportProject, q: Question) -> list[Question]:
    """Questions an answer creates: a new mark (split or given to unmarked openings) needs its heights asked."""
    marks = []
    if q.answer_type == "mark_width" and isinstance(q.answer, dict) and q.answer.get("split"):
        marks = [(m.upper(), q.key.get("kind")) for m in q.answer.get("new_marks", {}).values()]
    elif q.answer_type == "opening_mark" and isinstance(q.answer, dict):
        marks = [(q.answer["mark"].upper(), q.key.get("kind"))]
    out = []
    ids = {x.id for x in project.questions}
    for mark, kind in marks:
        if kind == "door":
            qid, text, at, key = (f"door-height-{_slug(mark)}", f"How high are the doors marked {mark} (a mark given "
                                  f"by answer {q.id})?", "door_height",
                                  {"mark": mark, "kind": "door", "assume": "door_height_mm"})
        elif kind in ("window", "ventilator"):
            qid, text, at, key = (f"window-size-{_slug(mark)}", f"What are the sill and head heights (above the "
                                  f"floor) of the openings marked {mark} (a mark given by answer {q.id})?",
                                  "window_size", {"mark": mark, "kinds": [kind], "assume": "window"})
        else:
            continue
        if qid in ids:
            continue
        nq = _q(qid, at, text, "blocking", where=f"openings marked {mark}", answer_type=at, key=key)
        project.questions.append(nq)
        ids.add(qid)
        out.append(nq)
    return out


def grouped(project: ImportProject) -> list[dict[str, Any]]:
    """The project's questions by severity, blocking first: [{"severity", "questions": [question dicts]}]."""
    sync_inputs(project)
    out = []
    for sev in SEVERITIES:
        qs = [q for q in project.questions if q.severity == sev]
        if qs:
            out.append({"severity": sev, "questions": [q.model_dump(mode="json") for q in qs]})
    return out
