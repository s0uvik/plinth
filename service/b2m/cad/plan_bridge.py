"""The seam between the import questions (T5) and the build-plan converter (T6, ``b2m.cad.to_plan``).

T6 refuses to guess: every value an element needs that the drawings do not give comes back as a ``Missing`` entry
with an answer id ("door_height:D1", "rooms:<sheet>:<finding>", "opening_host:<sheet>:<opening>" ...). The import
questions ask most of these already, grouped for an architect. This module

- translates the answered import questions into T6's answer map (``plan_answers``), so one answer serves both;
- gives T6 the recognised building with the answers that change the drawings' own facts applied (marks, widths,
  heights, layers), leaving rooms to T6's room answers (``building_for_plan``);
- runs T6 and turns whatever it still misses into import questions with T6's own ids (``merge``), unless an open
  import question already asks it (then the import question lists the T6 ids it covers, and the user is asked
  once); a finding question that T6 asks in its own, answerable form is replaced by T6's question, keeping the
  finding's evidence.
"""

from __future__ import annotations

import json
from typing import Any

from b2m.cad import answers as answers_mod
from b2m.cad.building import RecognisedBuilding
from b2m.cad.project import Evidence, ImportProject, Question

# T6 topics whose question replaces a finding question of the same topic (asked in a form the plan can use)
REPLACES = {"projection_kind": "projection_kind", "room_not_enclosed": "room_not_enclosed"}


def rules_for(project: ImportProject) -> str:
    """The b2m.rules data T6 checks against: the first governing rule set B2M has data for, else NBC 2016."""
    from b2m.cad import assumptions

    prof = assumptions.profile_for(project.inputs.location, project.inputs.values.get("country"))
    for rid in (project.inputs.rule_set or "").split("+"):
        ref = prof.rule_sets.get(rid) if prof else None
        if ref and ref.data:
            return ref.data
    return "nbc2016"


def building_for_plan(project: ImportProject, raw: RecognisedBuilding) -> RecognisedBuilding:
    return answers_mod.apply(project, raw, rooms=False)


def _openings(b: RecognisedBuilding):
    for p in b.plans:
        for o in p.openings:
            yield p.sheet, o


def _lift(o) -> bool:
    return o.kind == "door" and o.swing == "sliding" and (o.evidence.note or "").startswith("lift landing door")


def t6_ids(q: Question, b: RecognisedBuilding) -> dict[str, Any]:
    """T6's answer ids this import question settles, with the value its answer gives each (None while open)."""
    a, k = q.answer, q.key
    out: dict[str, Any] = {}
    if q.answer_type == "door_height":
        h = a["height_mm"] if isinstance(a, dict) else None
        if k.get("lift"):
            for _, o in _openings(b):
                if _lift(o):
                    out[f"door_height:door-{o.width_mm:g}-sliding"] = h
        else:
            out[f"door_height:{k['mark']}"] = h
    elif q.answer_type == "window_size":
        sill = a["sill_mm"] if isinstance(a, dict) else None
        head = a["head_mm"] if isinstance(a, dict) else None
        out[f"window_sill:{k['mark']}"] = sill
        out[f"window_height:{k['mark']}"] = None if sill is None else round(head - sill, 1)
    elif q.answer_type == "mark_width":
        out[f"{k.get('kind', 'door')}_width:{k['mark']}"] = a.get("width_mm") if isinstance(a, dict) else None
    elif q.answer_type == "opening_mark":
        for sheet, oid in k.get("openings", []):
            out[f"opening_mark:{sheet}:{oid}"] = a["mark"].upper() if isinstance(a, dict) else None
    elif q.topic == "door_not_in_wall":
        for sheet, oid in k.get("openings", []):
            out[f"opening_host:{sheet}:{oid}"] = a if q.answered_by else None
    elif q.answer_type == "rooms":
        for inst in k.get("instances", []):
            sheet, fid = inst["ref"].split("#", 1)
            if not isinstance(a, dict):
                value = None
            elif a.get("skip"):
                value = "skip"
            elif a.get("one_room"):
                value = {"mode": "one_room", **({"name": a["name"]} if a.get("name") else {})}
            else:
                value = {"mode": "separate", "lines": [[[x1, y1], [x2, y2]] for x1, y1, x2, y2
                                                       in a.get("lines", {}).get(inst["ref"], [])]}
            out[f"rooms:{sheet}:{fid}"] = value
    elif k.get("sets") == "tie_beam_depth":
        for sheet in k.get("sheets", []):
            out[f"beam_depth:{sheet}"] = a["value_mm"] if isinstance(a, dict) else None
    elif q.answer_type == "plan_input":
        out[q.id] = a
    return out


def plan_answers(project: ImportProject, b: RecognisedBuilding) -> dict[str, Any]:
    """T6's answer map from the answered import questions (and the user's other inputs)."""
    out: dict[str, Any] = dict(project.inputs.values)
    for q in project.questions:
        if q.open:
            continue
        out.update({k: v for k, v in t6_ids(q, b).items() if v is not None})
    return out


def to_plan(project: ImportProject, raw: RecognisedBuilding):
    """T6's ImportPlan for the import as answered so far."""
    from b2m.cad.to_plan import to_plan as t6

    b = building_for_plan(project, raw)
    return t6(b, plan_answers(project, b), rules=rules_for(project))


def _elements(q: Question, b: RecognisedBuilding) -> set[str]:
    out = {f"{s}#{i}" for s, i in q.key.get("openings", [])}
    for inst in q.key.get("instances", []):
        out |= {f"{inst['sheet']}#{r}" for r in inst.get("rooms", [])}
    return out


def _handles(b: RecognisedBuilding, refs: list[str]) -> set[str]:
    want = set(refs)
    out: set[str] = set()
    for p in b.plans:
        for group in (p.openings, p.rooms, p.projections, p.walls, p.stairs):
            for e in group:
                if f"{p.sheet}#{e.id}" in want:
                    out |= set(e.evidence.handles)
    return out


def _option_text(o: Any) -> str:
    return o if isinstance(o, str) else f"{o:g} mm" if isinstance(o, (int, float)) else json.dumps(o)


def merge(project: ImportProject, raw: RecognisedBuilding, old: dict[str, Question] | None = None) -> list[Question]:
    """Run T6 and add what it still misses as questions (T6's ids), asked once. Returns the questions added."""
    old = old or {}
    result = to_plan(project, raw)
    b = building_for_plan(project, raw)
    mine = [q for q in project.questions if not q.id.count(":")]
    covered: dict[str, Question] = {}
    for q in mine:
        ids = t6_ids(q, b)
        q.key = {**q.key, "plan_ids": sorted(ids)} if ids else q.key
        if q.open:
            for i in ids:
                covered[i] = q
            for e in _elements(q, b):
                covered.setdefault(f"element:{e}", q)
    added = []
    project.questions = [q for q in project.questions if not (q.id.count(":") and q.open)]  # re-derived below
    have = {q.id for q in project.questions}
    for m in result.missing:
        if m.id in have:
            continue
        by = covered.get(m.id) or next((covered[f"element:{e}"] for e in m.elements if f"element:{e}" in covered
                                        and covered[f"element:{e}"].topic in _COMPATIBLE.get(m.topic, ())), None)
        if by is not None:
            by.key = {**by.key, "plan_ids": sorted(set(by.key.get("plan_ids", [])) | {m.id})}
            continue
        hs = _handles(b, m.elements)
        replaced = [q for q in project.questions if q.open and REPLACES.get(m.topic) == q.topic
                    and hs & {h for e in q.evidence for h in e.handles}]
        findings = sorted({f for q in replaced for f in q.findings})
        sheets = sorted({e.split("#")[0] for e in m.elements})
        nq = Question(
            id=m.id, topic=m.topic, text=m.text, severity="blocking",
            evidence=[Evidence(sheet=s, handles=sorted(_handles(b, [e for e in m.elements if e.startswith(s + "#")])),
                               note="needed to build") for s in sheets],
            options=[_option_text(o) for o in m.options], where=", ".join(m.elements[:6])
            + (f" and {len(m.elements) - 6} more" if len(m.elements) > 6 else ""),
            instances=list(m.elements) if len(m.elements) > 1 else [], findings=findings,
            answer_type="plan_input", key={"unit": m.unit, "options_raw": m.options, "elements": m.elements})
        prev = old.get(m.id)
        if prev is not None and not prev.open:
            nq.answer, nq.answered_by, nq.at = prev.answer, prev.answered_by, prev.at
        for q in replaced:
            if q in project.questions:
                project.questions.remove(q)
        project.questions.append(nq)
        have.add(m.id)
        added.append(nq)
    return added


# T6 topics an open import question about the same elements already asks (the import question settles them)
_COMPATIBLE = {
    "door_height": ("opening_without_mark", "door_height", "mark_width"),
    "window_height": ("opening_without_mark", "window_size", "mark_width"),
    "window_sill": ("opening_without_mark", "window_size", "mark_width"),
    "door_width": ("mark_width",),
    "window_width": ("mark_width",),
    "opening_host": ("door_not_in_wall",),
    "rooms_share_space": ("rooms_share_space",),
}
