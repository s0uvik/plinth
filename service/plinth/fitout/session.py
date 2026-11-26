"""A fit-out on a built model, planned and built (PRD Step 10): the one flow the command line
(``scripts/fitout_build.py``), the HTTP endpoint and the MCP tool share, so each says the same about the same model.

    inputs = session.prepare(store, building_run, "west-bengal", answers=answers,        # the run store only
                             systems=["plumbing", "electrical", "hvac", "fire"], furniture_run=earlier_run)
    planned = session.plan(revit, inputs, document_title)     # families, the layouts, the plan: nothing is built
    planned.report      # JSON-able: questions (with the MEP layout's), assumptions, what is left out and why, op
                        # counts, the MEP notice, counts per system, unplaced, unrouted, the audit, the water sizing
    planned.plan        # the BuildPlan (None when nothing can be built)
    record = session.build(planned, store, document_title)   # its own run on top of the building's run
    # or planned in one call: session.plan_fitout(revit, store, building_run, document_title, rules, ...)

What it does, in order:
1. ``prepare`` reads the building's run (built: its plan and what it created; the import plan's sources say which
   shafts are ducts), the rules, the answers and the measurements, and builds the room model, so a wrong run, level,
   flat or system stops before Revit is asked anything. ``furniture_run``: an earlier fit-out run of the same building
   whose furniture is already in the model; its answers are the defaults (the answers given win), its built
   furniture is joined, never placed again. ``exclude_runs``: earlier fit-out runs (a pilot) whose elements are
   joined, not built again.
2. ``plan``: the families (measured), the furniture layout on their real footprints, the MEP layout on that layout
   (when an MEP system is asked for) and the plan (``to_plan.plan_furniture``). On a furniture run, the furniture
   laid out now must be what that run built (same key: same level, family type, point within 1 mm and turn within
   0.1 degree); else the plan stops with the list (``Drift``): different answers or measurements give another
   layout, and runs would end where nothing stands. Then the systems not asked for, and outside a flat pilot
   (``flat``, on ``level``), are left out (``to_plan.prune``: whatever joins them goes too). A pilot keeps the flat's
   rooms' records and the MEP records of the flat, with what they join (the branches, stacks and mains their pipes
   tee into, so the joins are tried), nothing else.
3. ``build``: the plan as its own run (``BuildService``), with the add-in commands it needs; the run keeps the report
   as ``fitout_report`` (building run, furniture run, systems, rules, answers). Joins and circuits Revit refuses are
   the run's issues (built_with_issues), never silent.

The MEP layout is an indicative coordination model: every report says so (``NOTICE``); an MEP engineer designs and
checks the real systems.
"""

from __future__ import annotations

import math
import re
from collections import Counter
from pathlib import Path
from typing import Any, Callable, Iterable, Mapping

from pydantic import BaseModel, ConfigDict

from plinth.builds import BuildService
from plinth.fitout import families, mep, mep_plan, rooms, rules as fitout_rules, to_plan
from plinth.fitout.families import Measurement
from plinth.fitout.mepcore import NOTICE
from plinth.fitout.rooms import RoomModel
from plinth.fitout.rules import FitoutRules
from plinth.plan import BuildPlan, FamilyOp
from plinth.revit import RevitClient
from plinth.revit.commands import REQUIRED_COMMANDS, fitout_commands
from plinth.revit.types import TypeMap
from plinth.runs import RunStore

SYSTEMS = mep_plan.SYSTEMS
MEP_SYSTEMS = SYSTEMS[1:]
BUILT = ("built", "built_with_issues")
# What planning reads in the document (measure_family_types is used when the add-in has it).
PLAN_COMMANDS = ["get_available_family_types", "get_type_parameters", "list_template_files", "list_types_in_file"]
# A question of the MEP layout that only one system's layout needs (by its topic); the rest shape every system.
QUESTION_SYSTEM = {"fitout_ac": "hvac", "fitout_fire": "fire", "fitout_tank": "plumbing", "fitout_stack": "plumbing",
                   "fitout_meter_room": "electrical"}
POINT_TOL_MM, TURN_TOL_DEG = 1.0, 0.1


class SessionError(ValueError):
    """What stops a fit-out before anything is built, said so the user can act on it."""


class Drift(SessionError):
    """The furniture laid out now is not what the furniture run built."""

    def __init__(self, run_id: str, mismatches: list[str]):
        self.mismatches = mismatches
        super().__init__(f"the furniture laid out now differs from what run {run_id} built ({len(mismatches)} items; "
                         "different answers or measurements?):\n  " + "\n  ".join(mismatches))


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class Inputs(_Model):
    building_run: str
    building: BuildPlan
    built: dict[str, int]
    rules: FitoutRules
    model: RoomModel
    answers: dict[str, Any]  # the furniture run's, overridden by those given
    measurements: list[Measurement] | None = None
    measured_level: str | None = None
    systems: list[str]
    shaft_kinds: dict[str, str] = {}
    furniture_run: str | None = None
    furniture_document: str | None = None
    furniture_ops: dict[str, FamilyOp] = {}  # what the furniture run built, as its plan placed it
    earlier: dict[str, int] = {}  # key -> id of what the furniture run and the excluded runs built
    exclude_runs: list[str] = []
    flat: str | None = None
    level: str | None = None


class Planned(_Model):
    report: dict[str, Any]
    plan: BuildPlan | None = None
    ready: bool = False
    answers: dict[str, Any] = {}


# ------------------------------------------------------------------------------------------------ prepare

def load_rules(name: str | FitoutRules) -> FitoutRules:
    """A rule set by id, or the one of a location profile."""
    if isinstance(name, FitoutRules):
        return name
    try:
        return fitout_rules.load(name)
    except (KeyError, ValueError, FileNotFoundError):
        found = fitout_rules.for_profile(name)
        if found is None:
            raise SessionError(f"no fit-out rule set or location profile named {name!r}") from None
        return found


def systems_of(given: Iterable[str] | str | None) -> list[str]:
    """The systems asked for (a list, or "plumbing,electrical"), in SYSTEMS order; all when none is given."""
    if given is None:
        return list(SYSTEMS)
    names = [s.strip().lower() for s in (given.split(",") if isinstance(given, str) else given) if s.strip()]
    unknown = sorted(set(names) - set(SYSTEMS))
    if unknown or not names:
        raise SessionError(f"systems are {', '.join(SYSTEMS)} (got {', '.join(unknown) or 'none'})")
    return [s for s in SYSTEMS if s in names]


def _built_run(store: RunStore, run_id: str, what: str):
    run = store.get(run_id)
    if run is None:
        raise SessionError(f"no run {run_id} in {store.root}")
    record = run.load("run") or {}
    if record.get("status") not in BUILT:
        raise SessionError(f"{what} run {run_id} is {record.get('status')}: the fit-out goes on a built model")
    return run, record


def prepare(store: RunStore, building_run: str, rules: str | FitoutRules, *, answers: Mapping[str, Any] | None = None,
            measurements: str | Path | dict | list | None = None, measured_level: str | None = None,
            systems: Iterable[str] | str | None = None, furniture_run: str | None = None,
            exclude_runs: Iterable[str] = (), flat: str | None = None, level: str | None = None) -> Inputs:
    """Everything the plan needs that is not in Revit, checked (see the module docstring); SessionError otherwise."""
    run, record = _built_run(store, building_run, "the building's")
    building = BuildPlan.model_validate(run.load("plan"))
    ruleset = load_rules(rules)
    chosen = systems_of(systems)
    given = {k: v for k, v in (answers or {}).items() if not k.startswith("_")}

    merged, furniture_ops, earlier, furniture_document = dict(given), {}, {}, None
    if furniture_run:
        frun, frecord = _built_run(store, furniture_run, "the furniture")
        saved = frun.load("fitout_report") or {}
        if saved.get("building_run") not in (None, building_run):
            raise SessionError(f"run {furniture_run} is a fit-out of {saved['building_run']}, not of {building_run}")
        created = frecord.get("created") or {}
        fplan = BuildPlan.model_validate(frun.load("plan"))
        furniture_ops = {op.key: op for op in fplan.ops if isinstance(op, FamilyOp) and op.key in created}
        earlier = {k: created[k] for k in furniture_ops}
        merged = {**(saved.get("answers") or {}), **given}
        furniture_document = frecord.get("document_title")
    for rid in exclude_runs:
        _, xrecord = _built_run(store, rid, "the excluded")
        earlier.update({k: v for k, v in (xrecord.get("created") or {}).items() if "#" not in k})

    levels = to_plan.levels_of(building)
    if measured_level and measured_level not in levels:
        raise SessionError(f"measured level {measured_level!r} is not a level of the building ({', '.join(levels)})")
    if level and not flat:
        raise SessionError("a level narrows a flat pilot: give the flat too")
    if level and level not in levels:
        raise SessionError(f"level {level!r} is not a level of the building ({', '.join(levels)})")
    given_measurements = None
    if measurements is not None:
        at = [levels[measured_level]] if measured_level else list(levels.values())
        given_measurements = families.load_measurements(measurements, level_elevations=at)

    model = rooms.build(building, ruleset, merged)
    if flat and not any(r.flat == flat and (not level or r.level == level) for r in model.rooms):
        raise SessionError(f"no rooms of flat {flat!r}" + (f" on {level}" if level else "")
                           + f"; flats: {sorted({str(r.flat) for r in model.rooms if r.flat})}")
    import_plan = run.load("import_plan") or {}
    return Inputs(building_run=building_run, building=building, built=record.get("created") or {}, rules=ruleset,
                  model=model, answers=merged, measurements=given_measurements, measured_level=measured_level,
                  systems=chosen, shaft_kinds=mep.shaft_kinds_from_sources(import_plan.get("sources") or {}),
                  furniture_run=furniture_run, furniture_document=furniture_document, furniture_ops=furniture_ops,
                  earlier=earlier, exclude_runs=list(exclude_runs), flat=flat, level=level)


# ------------------------------------------------------------------------------------------------ plan

def plan(revit: Any, inputs: Inputs, document: str | None) -> Planned:
    """Plan the fit-out in the open document (its title checked); nothing is built (see the module docstring)."""
    title = revit.confirm_document(document)
    if inputs.furniture_document and title != inputs.furniture_document:
        raise SessionError(f"run {inputs.furniture_run} was built in {inputs.furniture_document!r}, not in {title!r}")
    with_mep = any(s in MEP_SYSTEMS for s in inputs.systems)
    found = to_plan.plan_furniture(revit, inputs.building, inputs.built, inputs.model, inputs.rules,
                                   answers=inputs.answers, measurements=inputs.measurements, mep=with_mep,
                                   shaft_kinds=inputs.shaft_kinds, document=title,
                                   measure_level=inputs.measured_level, built_fitout=inputs.earlier)
    drift = check_drift(inputs.furniture_ops, found.already_built)
    if drift["mismatches"]:
        raise Drift(inputs.furniture_run or "", drift["mismatches"])

    record_system = {p.key: "furniture" for p in found.layout.placed}
    if found.mep is not None:
        record_system.update(mep_plan.system_of(found.mep))
    other = {k for k, s in record_system.items() if s not in inputs.systems}
    pilot = _outside_pilot(inputs, found, record_system) if inputs.flat else set()
    dropped: dict[str, str] = {}
    trimmed: dict[str, list[str]] = {}
    built_plan = found.plan
    for drop, why in ((other, f"not in the systems built ({', '.join(inputs.systems)})"),
                      (pilot, f"outside the pilot (flat {inputs.flat}{' on ' + inputs.level if inputs.level else ''})")):
        if built_plan is not None and drop:
            pruned = to_plan.prune(built_plan, drop, why=why)
            built_plan = pruned.plan
            dropped.update(pruned.dropped)
            trimmed.update(pruned.trimmed)

    # a family question only about records not built now (left out, or built before) is not this plan's; a question
    # of the MEP layout only one system needs, only when that system is built
    gone = other | pilot | set(inputs.earlier)
    questions, not_now = [], []
    for q in found.questions:
        system = QUESTION_SYSTEM.get(q.topic)
        if (q.instances and set(q.instances) <= gone) or (system and system not in inputs.systems):
            not_now.append(q)
        else:
            questions.append(q)
    left = [x for x in found.left_out if x.key not in gone]
    ready = not questions
    report = _report(inputs, found, built_plan, questions, not_now, left, dropped, trimmed, drift, ready)
    return Planned(report=report, plan=built_plan, ready=ready, answers=inputs.answers)


def plan_fitout(revit: Any, store: RunStore, building_run: str, document: str | None, rules: str | FitoutRules,
                **options: Any) -> Planned:
    """``prepare`` (``options``: its keyword arguments) and ``plan`` in one call."""
    return plan(revit, prepare(store, building_run, rules, **options), document)


def check_drift(before: Mapping[str, FamilyOp], now: Mapping[str, FamilyOp]) -> dict[str, Any]:
    """The furniture run's family ops (``before``, as its plan placed them) against the ops the layout gives the same
    records now: level, family type, point within ``POINT_TOL_MM`` and turn within ``TURN_TOL_DEG``. A record it built
    that gets no op now (not laid out, or its family not settled) is listed, not compared: runs to it are left out."""
    mismatches = []
    for key in sorted(set(before) & set(now)):
        a, b = before[key], now[key]
        diffs = []
        if a.level != b.level:
            diffs.append(f"level {a.level} -> {b.level}")
        if (a.family, a.type) != (b.family, b.type):
            diffs.append(f"type {a.family}: {a.type} -> {b.family}: {b.type}")
        if math.dist(a.point, b.point) > POINT_TOL_MM:
            diffs.append(f"point {tuple(a.point)} -> {tuple(b.point)}")
        if abs((a.rotation_deg - b.rotation_deg + 180) % 360 - 180) > TURN_TOL_DEG:
            diffs.append(f"turn {a.rotation_deg:g} -> {b.rotation_deg:g}")
        if diffs:
            mismatches.append(f"{key}: {'; '.join(diffs)}")
    return {"compared": len(set(before) & set(now)), "mismatches": mismatches,
            "built_not_planned_now": sorted(set(before) - set(now))}


def _outside_pilot(inputs: Inputs, found: to_plan.FitoutPlan, record_system: Mapping[str, str]) -> set[str]:
    """Record keys outside a flat pilot: the flat's rooms' records and the flat's MEP records stay, with what they
    join (run ends, their ends in turn, circuit panels)."""
    rooms_in = {r.key for r in inputs.model.rooms
                if r.flat == inputs.flat and (not inputs.level or r.level == inputs.level)}

    def ours(rec: Any) -> bool:
        return rec.room_key in rooms_in or (rec.flat == inputs.flat and (not inputs.level or rec.level == inputs.level))

    keep = {p.key for p in found.layout.placed if p.room_key in rooms_in}
    joins: dict[str, list[str]] = {}
    if found.mep is not None:
        keep |= {d.key for d in found.mep.devices if ours(d)}
        keep |= {r.key for r in found.mep.runs if ours(r)}
        joins = {r.key: [k for k in (r.connect_start, r.connect_end) if k] for r in found.mep.runs}
        joins.update({c.key: [c.panel] for c in found.mep.circuits})
        keep |= {c.key for c in found.mep.circuits if c.panel in keep}
    todo = list(keep)
    while todo:
        for k in joins.get(todo.pop(), []):
            if k not in keep:
                keep.add(k)
                todo.append(k)
    return set(record_system) - keep


# ------------------------------------------------------------------------------------------------ report

def _report(inputs: Inputs, found: to_plan.FitoutPlan, built_plan: BuildPlan | None, questions, not_now, left,
            dropped: dict[str, str], trimmed: dict[str, list[str]], drift: dict[str, Any], ready: bool) -> dict:
    """What the plan holds and why the rest is not in it, item by item and system by system (JSON-able)."""
    layout = found.layout
    by_key = built_plan.by_key() if built_plan else {}
    placed = Counter(p.item for p in layout.placed)
    in_plan = Counter(p.item for p in layout.placed if p.key in by_key)
    before = Counter(p.item for p in layout.placed if p.key in inputs.earlier)
    unplaced: dict[str, Counter] = {}
    for u in layout.unplaced:
        unplaced.setdefault(u.item, Counter())[f"{u.status}: {u.reason}"] += 1
    sizes: dict[str, Counter] = {}
    for p in layout.placed:
        sizes.setdefault(p.item, Counter())[f"{p.width_mm:g} x {p.depth_mm:g}"] += 1
    items = {item: {"laid_out": placed[item], "in_plan": in_plan[item], "built_before": before[item],
                    "sizes": dict(sizes.get(item, {})), "not_placed": dict(unplaced.get(item, {}))}
             for item in sorted(set(placed) | set(unplaced))}
    ops = Counter(op.op for op in built_plan.ops) if built_plan else Counter()
    types = Counter(f"{op.family}: {op.type}" for op in built_plan.ops if op.op == "family") if built_plan else Counter()
    out: dict[str, Any] = {
        "ready": ready,
        "building_run": inputs.building_run, "furniture_run": inputs.furniture_run, "systems": inputs.systems,
        "rules": inputs.rules.id, "flat": inputs.flat, "level": inputs.level, "exclude_runs": inputs.exclude_runs,
        "items": items,
        "questions": [_question(q) for q in questions],
        "questions_not_needed_now": [_question(q) for q in not_now],
        "room_questions": [_question(q) for q in inputs.model.questions],
        "assumptions": [{"id": a.id, "text": a.text, "source": a.source} for a in found.assumptions],
        "left_out_of_plan": dict(Counter(f"{x.item}: {_why(x.reason)}" for x in left)),
        "dropped": dict(Counter(_why(why) for why in dropped.values())),
        "circuits_trimmed": len(trimmed),
        "ops": dict(ops),
        "family_types": dict(types),
        "skipped_rooms": [{"room": s.room_key, "name": s.name, "reason": s.reason} for s in layout.skipped],
    }
    if inputs.furniture_run:
        out["furniture_check"] = {k: v for k, v in drift.items() if k != "mismatches"}
    if found.mep is not None:
        out["notice"] = NOTICE
        out["mep"] = _mep_report(inputs, found, by_key)
    return out


# Reasons naming the record they depend on, summed up without it (thousands of runs give a few lines).
_NAMED = ((re.compile(r"^it joins .*, which is (not in the plan|left out)$"), r"it joins what is \1"),
          (re.compile(r"^its panel .* is (not in the plan|left out)$"), r"its panel is \1"))


def _why(reason: str) -> str:
    for pattern, said in _NAMED:
        if pattern.match(reason):
            return pattern.sub(said, reason)
    return reason


def _question(q) -> dict[str, Any]:
    return {"id": q.id, "topic": q.topic, "text": q.text, "instances": len(q.instances), "options": q.options[:12]}


def _mep_report(inputs: Inputs, found: to_plan.FitoutPlan, by_key: Mapping[str, Any]) -> dict[str, Any]:
    layout = found.mep
    system = mep_plan.system_of(layout)
    wanted = [s for s in inputs.systems if s in MEP_SYSTEMS]
    counts: dict[str, dict[str, Any]] = {}
    for s in wanted:
        devices = Counter(d.what for d in layout.devices if system[d.key] == s)
        runs = Counter(f"{r.kind}:{r.system}" for r in layout.runs if system[r.key] == s)
        keys = [k for k, v in system.items() if v == s]
        counts[s] = {"devices": dict(devices), "runs": dict(runs), "in_plan": sum(k in by_key for k in keys),
                     "laid_out": len(keys)}
    if "electrical" in counts:
        counts["electrical"]["circuits"] = dict(Counter(c.kind for c in layout.circuits))
    water = layout.water.model_dump() if layout.water else None
    return {
        "counts": counts,
        "unplaced": [u.model_dump() for u in layout.unplaced],
        "unrouted": [u.model_dump() for u in layout.unrouted],
        "problems": mep.problems(layout, inputs.model, found.layout),
        "provisions": [{"id": p.id, "label": p.label, "status": p.status, "note": p.note} for p in layout.provisions],
        "water": water,
        "height_m": layout.height_m, "fire_band_max_m": layout.fire_band_max_m,
    }


# ------------------------------------------------------------------------------------------------ build

def required_commands(built_plan: BuildPlan) -> list[str]:
    """The add-in commands building the plan needs (a command it does not use is not asked for)."""
    return list(dict.fromkeys(REQUIRED_COMMANDS + fitout_commands(built_plan.ops)))


def build(planned: Planned, store: RunStore, document: str | None, *, connect: Callable[[], Any] | None = None,
          types: TypeMap | None = None, partial: bool = False) -> dict[str, Any]:
    """Build the planned fit-out as its own run and return the run record; the run keeps the report (with the
    answers) as ``fitout_report``. ``partial``: build what is planned even with open questions (their items wait)."""
    if planned.plan is None:
        raise SessionError("nothing to build")
    if not planned.ready and not partial:
        raise SessionError("not built: there are open questions (answer them, or build partially what is planned)")
    needed = required_commands(planned.plan)
    service = BuildService(store, connect=connect or (lambda: RevitClient.connect(required_commands=needed)),
                           types=types if types is not None else TypeMap.load())
    record = service.build_now(planned.plan, document)
    store.get(record["run_id"]).save("fitout_report", {**planned.report, "answers": planned.answers})
    return record
