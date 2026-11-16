"""The door/window estimate of an imported project (PRD Step 9, stage 10): what was counted, measured by the office's
written rules (``b2m.estimate``), checked against the drawings and, given the office's own workbook, against it.

    out = make_estimate(project, building, out_dir, run=run, measured=measured, reference="office.xlsx")

Where the counts come from (``source`` in the result):
- ``model``: the built model, read back (``model_quantities_record`` runs inside the run's Revit session): every door
  and window with its Type Mark, width, height and level, every railing with its host. Levels map to storeys through
  the plan's op sources (which sheet and storey each op was built from).
- ``drawings``: no run given: the recognised building with the answers applied (marks, widths, heights as answered).

Nothing is guessed. What the model and the drawings do not say is a question whose id is the answer key read here
(``project.inputs.values``):
- ``estimate:material:<mark>``: asked for every door/window mark of a kind the rules measure by material. Answer one
  material for all ("wood"), counts per storey ({"wood": 12, "pvc": 8}), either of those per storey kind
  ({"ground": "pvc", "typical": {"wood": 12, "pvc": 8}}), or "exclude" (not in this estimate).
- ``estimate:railings:<what>``: asked for stairs with railings and for balconies / verandahs when the rules measure
  railings; the drawings give no railing lengths. Answer {"kind", "subkind", "length_mm", "count", "storey"} (or a
  list of them; kind defaults to <what>, storey to the whole building), or "none".
The engine's own questions (a mark with two sizes, per-storey counts that differ, an opening no rule measures), the
cross-check's disagreements with the drawings and every reference row that differs become questions too, with ids
derived from what they say, so an answer stays with its question across runs.

An unmarked type is named in the estimate as the user named it (a mark given by answer, as typed: "Entrance"), else by
its plan type name. A door on the roof storey is in the zone "terrace".
"""

from __future__ import annotations

import hashlib
import html
import json
import re
from collections import Counter, defaultdict
from pathlib import Path
from typing import Any

from b2m.cad import answers as answers_mod
from b2m.cad.building import RecognisedBuilding
from b2m.cad.project import ImportProject
from b2m.estimate import (DrawingObservation, Opening, ProjectInfo, Quantities, Railing, Storey, build_estimate,
                          compare_workbooks, crosscheck, load_rules, write_xlsx)
from b2m.estimate.ruleset import Rules

RULES_KEY = "estimate:rules"
DEFAULT_RULES = "office-default"
ROOF_ZONE = "terrace"
XLSX, JSON_FILE = "estimate.xlsx", "estimate.json"
CROSSCHECK_HTML, COMPARISON_HTML = "estimate-crosscheck.html", "estimate-comparison.html"
EXCLUDE = ("exclude", "none", "leave out", "not in this estimate")


def _qid(prefix: str, text: str) -> str:
    return f"{prefix}:{hashlib.sha1(text.encode('utf-8')).hexdigest()[:8]}"


# ------------------------------------------------------------------------------------------------ storeys

class _Storeys:
    """Storey indexes and kinds from the sheets: a floor plan standing for several storeys is the typical floor, the
    ground storey is 0, the roof is one above the highest floor."""

    def __init__(self, project: ImportProject):
        self.by_sheet: dict[str, list[int]] = {}
        floors: set[int] = set()
        typical: set[int] = set()
        roofs = []
        for s in project.sheets:
            if s.role == "floor_plan" and s.storeys:
                self.by_sheet[s.file] = list(s.storeys)
                floors |= set(s.storeys)
                if len(s.storeys) > 1:
                    typical |= set(s.storeys)
            elif s.role == "roof_plan":
                roofs.append(s.file)
        self.roof = max(floors) + 1 if floors else 1
        for f in roofs:
            self.by_sheet[f] = [self.roof]
        self.kinds = {i: ("typical" if i in typical else "ground" if i == 0 else "other") for i in sorted(floors)}
        self.kinds[self.roof] = "roof"

    def of(self, storey: str | None) -> int | None:
        if storey in (None, ""):
            return None
        if storey == "roof":
            return self.roof
        try:
            return int(storey)
        except ValueError:
            return None

    def info(self, name: str, location: str | None) -> ProjectInfo:
        names = {"ground": "Ground floor", "roof": "Roof"}
        return ProjectInfo(name=name, location=location, storeys=[
            Storey(index=i, name=names.get(k, f"Storey {i}"), kind=k) for i, k in sorted(self.kinds.items())])


def _span(storeys: list[int]) -> str:
    s = sorted(set(storeys))
    if len(s) == 1:
        return f"storey {s[0]}"
    return f"storeys {s[0]}-{s[-1]}" if s == list(range(s[0], s[-1] + 1)) else f"storeys {s}"


def _names(project: ImportProject) -> dict[str, str]:
    """Marks the user gave by answer, as typed (the model's Type Marks are upper case)."""
    out = {}
    for q in project.questions:
        a = q.answer
        if q.open or not isinstance(a, dict):
            continue
        if q.answer_type == "opening_mark" and a.get("mark"):
            out[str(a["mark"]).upper()] = str(a["mark"])
        elif q.answer_type == "mark_width" and a.get("split"):
            for m in (a.get("new_marks") or {}).values():
                out[str(m).upper()] = str(m)
    return out


# ------------------------------------------------------------------------------------------------ counting

def model_quantities_record(revit, run) -> dict[str, Any]:
    """Inside the run's Revit session: read back every door, window and railing the run built (compact records)."""
    from b2m.imported import selfcheck
    from b2m.imported.to_plan import ImportPlan

    result = ImportPlan.model_validate(run.load("import_plan"))
    created = (run.load("run") or {}).get("created") or {}
    by_key = result.plan.by_key()
    keys = {created[k]: k for k, op in by_key.items() if op.op in ("door", "window", "railing") and k in created}
    records = selfcheck.read_back(revit, list(keys))
    out = []
    for element_id, key in keys.items():
        r = records.get(int(element_id)) or {}
        op = by_key[key]
        if not r.get("exists", True):
            continue
        if op.op == "railing":
            out.append({"op_key": key, "kind": "railing", "id": element_id,
                        "host_id": (r.get("railing") or {}).get("host_id")})
            continue
        o = r.get("opening") or {}
        out.append({"op_key": key, "kind": op.op, "id": element_id, "type_mark": r.get("type_mark"),
                    "type": op.type, "width_mm": o.get("width_mm"), "height_mm": o.get("height_mm"),
                    "level": r.get("level"), "material": r.get("material")})
    return {"source": "model", "read": len(records), "elements": out}


def _from_model(measured: dict[str, Any], run, storeys: _Storeys) -> tuple[list[dict], int, list[str]]:
    from b2m.imported.to_plan import ImportPlan

    result = ImportPlan.model_validate(run.load("import_plan"))
    level_storey: dict[str, Counter] = defaultdict(Counter)
    for key, op in result.plan.by_key().items():
        level = getattr(op, "level", None)
        src = result.sources.get(key)
        if level and src is not None and storeys.of(src.storey) is not None:
            level_storey[level][storeys.of(src.storey)] += 1
    items, railings, notes = [], 0, []
    for e in measured.get("elements") or []:
        if e["kind"] == "railing":
            railings += 1
            continue
        storey = level_storey[e.get("level")].most_common(1)[0][0] if level_storey.get(e.get("level")) else None
        if storey is None or not e.get("width_mm") or not e.get("height_mm"):
            notes.append(f"{e['kind']} {e['id']} ({e.get('type_mark') or e.get('type')}): no storey or size read back")
            continue
        items.append({"kind": e["kind"], "mark": e.get("type_mark"), "type": e.get("type"), "width_mm": e["width_mm"],
                      "height_mm": e["height_mm"], "storey": storey, "material": e.get("material"),
                      "source": f"model element {e['id']}"})
    return items, railings, notes


def _from_drawings(project: ImportProject, building: RecognisedBuilding, storeys: _Storeys
                   ) -> tuple[list[dict], int, list[str]]:
    applied = answers_mod.apply(project, building)
    items, railings, notes = [], 0, []
    for p in applied.plans:
        on = storeys.by_sheet.get(p.sheet) or []
        railings += sum(1 for s in p.stairs) * len(on)
        for o in p.openings:
            if o.kind not in ("door", "window"):
                continue
            mark = o.mark_normalised or (o.mark.upper() if o.mark else None)
            if not o.height_mm:
                notes.append(f"{o.kind} {o.id} on {p.sheet} ({mark or 'unmarked'}): height not answered yet")
                continue
            for s in on:
                items.append({"kind": o.kind, "mark": mark, "type": f"{o.kind}-{o.width_mm:g}",
                              "width_mm": o.width_mm, "height_mm": o.height_mm, "storey": s, "material": None,
                              "source": f"{p.sheet}#{o.id}"})
    return items, railings, notes


# ------------------------------------------------------------------------------------------------ answers

def _material_rules(rules: Rules) -> dict[str, list[str]]:
    """Kinds the rules measure by material, with the materials they name."""
    out: dict[str, list[str]] = defaultdict(list)
    for sec in rules.sections:
        for g in sec.groups:
            if g.select.kind and g.select.material and g.select.material not in out[g.select.kind]:
                out[g.select.kind].append(g.select.material)
    return dict(out)


def _railing_kinds(rules: Rules) -> list[str]:
    return list(dict.fromkeys(g.select.railing for sec in rules.sections for g in sec.groups if g.select.railing))


def _split(answer: Any, storey_kind: str, count: int) -> dict[str, int] | str | None:
    """{material: count} for one storey from a material answer; "exclude"; or None when it does not say."""
    if isinstance(answer, dict) and set(answer) <= {"ground", "typical", "roof", "other"}:
        answer = answer.get(storey_kind)
    if isinstance(answer, str):
        text = answer.strip().lower()
        return "exclude" if text in EXCLUDE else {text: count}
    if isinstance(answer, dict) and answer and all(isinstance(v, int) and v >= 0 for v in answer.values()):
        return {str(k).lower(): v for k, v in answer.items() if v} if sum(answer.values()) == count else None
    return None


def _railings(answer: Any, what: str) -> list[Railing] | None:
    if isinstance(answer, str) and answer.strip().lower() in EXCLUDE:
        return []
    entries = answer if isinstance(answer, list) else [answer] if isinstance(answer, dict) else None
    if not entries:
        return None
    out = []
    for e in entries:
        if not isinstance(e, dict) or not e.get("length_mm") or not e.get("count"):
            return None
        out.append(Railing(kind=str(e.get("kind") or what), subkind=e.get("subkind"), length_mm=float(e["length_mm"]),
                           count=int(e["count"]), storey=e.get("storey"), source=f"answer estimate:railings:{what}"))
    return out


# ------------------------------------------------------------------------------------------------ the estimate

def quantities(project: ImportProject, building: RecognisedBuilding, rules: Rules, *, run=None,
               measured: dict[str, Any] | None = None) -> tuple[Quantities, list[dict[str, Any]], dict[str, Any]]:
    """The estimate's quantities, the questions they leave, and where they came from."""
    storeys = _Storeys(project)
    values = project.inputs.values
    if measured is not None and run is not None:
        items, stair_railings, notes = _from_model(measured, run, storeys)
        source = {"source": "model", "run_id": run.id, "elements_read": measured.get("read"),
                  "note": "doors, windows and railings read back from the built model"}
    else:
        items, stair_railings, notes = _from_drawings(project, building, storeys)
        source = {"source": "drawings", "note": "no build given: the recognised building with the answers applied"}
    names = _names(project)
    by_material = _material_rules(rules)
    questions: list[dict[str, Any]] = []

    groups: dict[tuple, dict[int, int]] = defaultdict(lambda: defaultdict(int))
    srcs: dict[tuple, list[str]] = defaultdict(list)
    for it in items:
        mark = it["mark"] or it["type"]
        mark = names.get(str(mark).upper(), mark)
        zone = ROOF_ZONE if storeys.kinds.get(it["storey"]) == "roof" and it["kind"] == "door" else None
        key = (it["kind"], mark, float(it["width_mm"]), float(it["height_mm"]), zone, it.get("material"))
        groups[key][it["storey"]] += 1
        srcs[key].append(it["source"])

    openings: list[Opening] = []
    for (kind, mark, w, h, zone, material), per_storey in groups.items():
        src = srcs[(kind, mark, w, h, zone, material)]
        source_text = f"{source['source']}: {len(src)} ({src[0]}{' ...' if len(src) > 1 else ''})"
        if material or kind not in by_material:
            openings += [Opening(mark=mark, kind=kind, width_mm=w, height_mm=h, storey=s, zone=zone, material=material,
                                 count=n, source=source_text) for s, n in sorted(per_storey.items())]
            continue
        qid = f"estimate:material:{mark}"
        answer = values.get(qid)
        unsettled = []
        for s, n in sorted(per_storey.items()):
            split = _split(answer, storeys.kinds.get(s, "other"), n) if answer is not None else None
            if split == "exclude":
                continue
            if split is None:
                unsettled.append((s, n))
                continue
            openings += [Opening(mark=mark, kind=kind, width_mm=w, height_mm=h, storey=s, zone=zone, material=m,
                                 count=c, source=f"{source_text}; material: answer {qid}") for m, c in split.items()]
        if unsettled:
            where = ", ".join(f"{n} on storey {s}" for s, n in unsettled)
            questions.append({
                "id": qid, "topic": "material", "options": by_material[kind] + ["exclude"],
                "text": (f"What material are the {kind}s marked {mark} ({w:g} x {h:g} mm): {where}? The estimate "
                         f"measures {kind}s by material ({', '.join(by_material[kind])}) and neither the drawings nor "
                         "the model say it. Answer one material for all, counts per storey such as "
                         '{"wood": 12, "pvc": 8}, either per storey kind such as {"ground": "pvc", "typical": '
                         '{"wood": 12, "pvc": 8}}, or "exclude" to leave them out of this estimate.'),
                "instances": [f"storey {s}: {n}" for s, n in unsettled]})

    railings: list[Railing] = []
    if _railing_kinds(rules):
        things = {"stair": stair_railings}
        for p in building.plans:
            for pr in p.projections:
                if pr.kind in ("balcony", "verandah"):
                    things[pr.kind] = things.get(pr.kind, 0) + len(storeys.by_sheet.get(p.sheet) or [1])
        for what, n in things.items():
            if not n:
                continue
            qid = f"estimate:railings:{what}"
            got = _railings(values.get(qid), what) if values.get(qid) is not None else None
            if got is None:
                seen = (f"{n} stair railings in the model (one per stair)" if what == "stair" and source["source"] ==
                        "model" else f"{n} {what}{'s' if n != 1 else ''} on the plans")
                questions.append({
                    "id": qid, "topic": "railings", "options": ["none"],
                    "text": (f"Which {what} railings does the estimate measure? There are {seen}, but the drawings "
                             "give no railing lengths. Answer {\"kind\": ..., \"subkind\": ..., \"length_mm\": ..., "
                             "\"count\": ...} (or a list of them; count over the whole building unless a storey is "
                             f"given; kind defaults to {what}), or \"none\".")})
                continue
            railings += got

    q = Quantities(project=storeys.info(project.inputs.values.get("project_name") or project.id,
                                        project.inputs.location.city if project.inputs.location else None),
                   openings=openings, railings=railings)
    source["notes"] = notes
    return q, questions, source


def observations(project: ImportProject, building: RecognisedBuilding) -> list[DrawingObservation]:
    """What the drawings show, per plan sheet: mark texts per mark, and door symbols per drawn width."""
    storeys = _Storeys(project)
    out = []
    for p in building.plans:
        on = storeys.by_sheet.get(p.sheet)
        if not on:
            continue
        marks = Counter((o.kind, o.mark) for o in p.openings if o.kind in ("door", "window") and o.mark)
        for (kind, mark), n in sorted(marks.items()):
            out.append(DrawingObservation(source=f"{p.sheet}: mark {mark!r} x{n}", storeys=on, kind=kind, mark=mark,
                                          count_per_storey=n))
        widths = Counter(round(o.width_mm) for o in p.openings if o.kind == "door")
        for w, n in sorted(widths.items()):
            out.append(DrawingObservation(source=f"{p.sheet}: doors drawn {w} wide x{n}", storeys=on, kind="door",
                                          width_mm=w, count_per_storey=n))
    return out


def make_estimate(project: ImportProject, building: RecognisedBuilding, out_dir: Path, *, run=None,
                  measured: dict[str, Any] | None = None, reference: str | Path | None = None) -> dict[str, Any]:
    """Quantities -> estimate.xlsx (+ estimate.json), the cross-check against the drawings and, given the office's
    workbook, the comparison with it. Returns the paths, summaries and every question (stage question items)."""
    out_dir = Path(out_dir)
    rules_name = project.inputs.values.get(RULES_KEY) or DEFAULT_RULES
    rules = load_rules(rules_name)
    q, questions, source = quantities(project, building, rules, run=run, measured=measured)
    est = build_estimate(q, rules)
    xlsx = write_xlsx(est, rules, out_dir / XLSX)
    for eq in est.questions:
        questions.append({"id": _qid(f"estimate:{eq.topic}", eq.text), "topic": eq.topic, "text": eq.text,
                          "instances": eq.evidence})
    check = crosscheck(q, observations(project, building))
    for f in check.findings:
        if f.question:
            questions.append({"id": _qid("estimate:crosscheck", f.subject), "topic": "crosscheck",
                              "text": f.question, "instances": [f"drawing: {f.drawing}", f"estimate: {f.estimate}"]})
    _write_html(out_dir / CROSSCHECK_HTML, "Estimate quantities vs the drawings", check.text())
    comparison = None
    if reference:
        report = compare_workbooks(xlsx, reference, est)
        _write_html(out_dir / COMPARISON_HTML, "Estimate vs the office's workbook", report.text())
        for text in report.questions():
            row = re.match(r"Row (\d+)", text)
            questions.append({"id": f"estimate:reference:row-{row.group(1) if row else _qid('', text)[1:]}",
                              "topic": "reference", "text": text})
        comparison = {"reference": str(reference), "exact": report.exact, "rows_compared": report.rows_compared,
                      "cells_compared": report.cells_compared, "counts": report.counts(),
                      "differences": [{"cell": d.cell, "kind": d.kind, "column": d.column, "context": d.context,
                                       "reference": d.reference, "generated": d.generated, "cosmetic": d.cosmetic,
                                       "note": d.note} for d in report.differences]}
    (out_dir / JSON_FILE).write_text(json.dumps({
        "rules": rules_name, "source": source, "quantities": q.model_dump(mode="json"),
        "values": est.values(), "questions": questions}, indent=1, ensure_ascii=False, default=str), encoding="utf-8")
    return {"xlsx": str(xlsx), "rules": rules_name, **{"source": source["source"], "source_detail": source},
            "rows": len(est.rows), "questions": questions,
            "crosscheck": {"findings": [f.model_dump() for f in check.findings], "unverified": check.unverified},
            "comparison": comparison}


def _write_html(path: Path, title: str, text: str) -> None:
    path.write_text(f"<!doctype html><html><head><meta charset=\"utf-8\"><title>{html.escape(title)}</title>"
                    "<style>body{font:14px system-ui,sans-serif;margin:24px}pre{white-space:pre-wrap}</style></head>"
                    f"<body><h1>{html.escape(title)}</h1><pre>{html.escape(text)}</pre></body></html>",
                    encoding="utf-8")
