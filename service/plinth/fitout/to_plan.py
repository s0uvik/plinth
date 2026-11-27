"""Fit-out layout -> build plan (PRD Step 10, stage 8 "Review and build"; F5/F6): the furniture layout (and the MEP
layout when given) with the family types matched for it becomes one BuildPlan, built by the existing runner as its own
run on top of the building's run, so it is approved, built, read back and removed as a whole, apart from the building.

    found = to_plan.plan_fitout(revit, building_plan, building_run_created, furniture=layout, mep=mep_layout,
                                answers=answers)    # mep: the MepLayout (made into records here) or its records
    found.plan          # BuildPlan (None when nothing can be placed yet)
    found.questions     # families to choose (nothing fits), with the items they hold back
    found.assumptions   # template copies, size copies, size differences, placements decided here
    found.left_out      # records not in the plan, each with why

    # families first: each item's family chosen and measured, the layout made with their real footprints
    found = to_plan.plan_furniture(revit, building_plan, building_run_created, model, ruleset, answers=answers,
                                   measurements=families.load_measurements(path),   # measurements optional
                                   mep=True, shaft_kinds=shaft_kinds)                # and the MEP layout on it
    found.layout, found.items   # the layout it made; the family (and measured types) per rule item
    found.mep                   # the MEP layout made on that furniture layout (mep=True)

    # on an earlier fit-out run: its records are not placed again, runs and circuits join what it built
    found = to_plan.plan_furniture(..., mep=True, built_fitout=earlier_run_created)
    found.already_built         # the family op each of its records would get now (to check the layout is the same)
    to_plan.prune(found.plan, drop=keys)   # a plan without some records and whatever joins them

A measured family is inserted so its box lands on the layout's footprint (``measured_point``): the layout gives the
middle of the item's back edge and the turn for a family whose front faces -Y; the family's back middle (the middle
of its box's edge opposite its front) goes on that point after the turn, whatever its origin, and its front is turned
by the user's ``front`` when it is not -Y. It stands on the level (offset 0) unless its geometry reaches below its own
origin, and a sink or hob keeps its origin at the counter top; the measurement is written in the op's source and in
the item's assumption. A work-plane-based fixture on a counter goes on the level's plane at the counter top, never
on the wall behind it.

The plan's ops, in build order:
- ``types_from_file`` per installed template the chosen types come from (copied in as new types; types the project
  has stay as they are and are never removed with the run), ``family_create`` per box family Plinth creates because the
  user answered so (``{"create": "box"}``: loaded as a new family, removed with the run; placed like a measured family,
  its box known from its own geometry), then ``family_type_copy`` per type copied to a size;
- ``family`` per placed record (key = the layout's key, so it is stable for the same plan and rules), placed as its
  family's placement type allows: level-based at the record's insertion point (the middle of its back edge), turned
  by its rotation (the family's front drawn towards -Y), raised by its elevation; wall-hosted or face-based on the
  record's wall (a wall of the building run) on the room's side (towards the item's centre), at its elevation as the
  height; face-based without a wall on the level's plane, or facing down at its elevation (``face_down`` records:
  lights and fans with no ceiling);
- ``mep_run`` per run of the MEP layout (points' z above their level, made absolute here), joined to the devices and
  runs it names; ``circuit`` per circuit.
The building's levels and the walls used as hosts are the plan's ``existing_levels`` and ``existing`` (the building
run's ids): looked up, never created nor removed by the fit-out run.

Joins, best effort and never silent: an MEP family (the user's own WC, a socket, a box) often has no connector of a
run's domain (conduits almost never join receptacles), and a real type's routing preferences may lack an elbow or tee
at a size, so no fit-out run requires its joins (``require_connections`` false: a refused join or elbow leaves the run
standing and is an issue of the build; one refusal never stops a build of thousands of ops). Circuits are best effort
(``best_effort``): a device without an electrical connector is left off its circuit, a circuit with none left is not
made, a panel Revit refuses leaves it unassigned; each is an issue of the build. A box family Plinth creates gets the
connectors the plan joins to its instances (``connectors``): an electrical one at the rule set's supply voltage when
any is on a circuit or is a panel, a pipe connector per system classification and diameter of the pipe runs ending at
them.

On an earlier fit-out run (``built_fitout``: its keys -> element ids, e.g. the furniture built before the MEP): those
records get no op and no type op, but count as placed, so runs and circuits join them (``BuildPlan.existing``).
``prune`` is the one way records leave a plan (a system or flat left out, an earlier run's): whatever joins a record
that is gone goes too (a circuit only loses the device), and so do the types nothing uses any more.

MEP layout records (F4), read by attribute or key: ``devices`` shaped like the furniture records (key, room_key,
level, item, label, category, keywords, insertion or point, rotation_deg, elevation_mm, wall_key, center or toward,
face_down, source; width_mm/depth_mm when sized); ``runs`` (key, kind pipe/duct/conduit/cable_tray, level, system (a
system classification: Sanitary, DomesticColdWater, SupplyAir...; pipes and ducts), points [(x, y, z above the
level)], diameter_mm or width_mm/height_mm, start_key/end_key: a device or an earlier run, type_keywords, source);
``circuits`` (key, device_keys, panel_key, system_type, source).
"""

from __future__ import annotations

import re
from typing import Any, Iterable, Mapping

from pydantic import BaseModel, ConfigDict

from plinth.fitout import families, furniture as furniture_solver, mep as mep_solver, mep_plan
from plinth.fitout.families import BASE_TOL_MM, FRONT_TURN, Choice, ItemFamilies, Measurement, Need, Resolution, turn
from plinth.fitout.furniture import FurnitureLayout
from plinth.fitout.mepcore import MepLayout
from plinth.fitout.rooms import Assumption, Question, RoomModel
from plinth.plan import (MAX_BOX_CONNECTORS, TYPE_OPS, BuildPlan, CircuitOp, FamilyCreateOp, FamilyOp, FamilyTypeCopyOp,
                         LevelOp, MepRunOp, TypesFromFileOp, WallOp, references)

TYPES_KEY, TYPE_COPY_KEY, FAMILY_KEY = "FIT-TYPES-", "FIT-TYPE-", "FIT-FAMILY-"
LEVEL_BASED = (None, "OneLevelBased")
MAX_COPY_ITEMS = 100


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class LeftOut(_Model):
    key: str
    item: str
    reason: str


class FitoutPlan(_Model):
    plan: BuildPlan | None = None
    ready: bool = False  # no open questions: the plan holds every item of the layout that can be placed
    questions: list[Question] = []
    assumptions: list[Assumption] = []
    left_out: list[LeftOut] = []
    families: dict[str, Choice] = {}  # need id -> the family type it is placed with
    needs: list[Need] = []
    layout: FurnitureLayout | None = None  # plan_furniture: the layout made with the chosen families' sizes
    items: ItemFamilies | None = None  # plan_furniture: the family chosen (and measured) per rule item
    mep: MepLayout | None = None  # plan_furniture(mep=True): the MEP layout made on that furniture layout
    already_built: dict[str, FamilyOp] = {}  # built_fitout: the op each record an earlier run built would get now


class Pruned(_Model):
    plan: BuildPlan | None = None
    dropped: dict[str, str] = {}  # op key -> why it is not in the plan any more
    trimmed: dict[str, list[str]] = {}  # circuit key -> the devices left off it


def _get(record: Any, name: str, default: Any = None) -> Any:
    return record.get(name, default) if isinstance(record, dict) else getattr(record, name, default)


def _slug(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "-", text).strip("-").upper()


def _rotation(deg: float) -> float:
    """The same turn in (-180, 180]."""
    r = round(deg % 360.0, 6)
    return r - 360.0 if r > 180.0 else r


def _mep_part(mep: Any, name: str) -> list[Any]:
    return list(_get(mep, name) or []) if mep is not None else []


def mep_records(mep: Any, building: BuildPlan) -> Any:
    """The MEP records ``make`` reads. A ``MepLayout`` given as it is becomes its records here (``mep_plan.records``:
    points above their level, sizes, joins, classifications): read as records, its absolute z would be raised by the
    level again and its sizes and joins lost."""
    return mep_plan.records(mep, levels_of(building)) if isinstance(mep, MepLayout) else mep


def plan_fitout(revit: families.Revit, building: BuildPlan, built: dict[str, int], *, furniture: Any = None,
                mep: Any = None, answers: dict[str, Any] | None = None, templates: list[str] | None = None,
                year: int | str | None = None, built_fitout: Mapping[str, int] | None = None,
                voltage_v: float | None = None) -> FitoutPlan:
    """Match families for the layout (project, then installed templates) and make the fit-out's build plan. ``mep``:
    the MEP layout or its records (``mep_plan.records``); ``voltage_v``: the supply voltage of box families'
    electrical connectors (the rule set's)."""
    mep = mep_records(mep, building)
    devices = list(_get(furniture, "placed") or []) + _mep_part(mep, "devices")
    needs = families.needs_from(devices) + families.run_needs(_mep_part(mep, "runs"))
    resolution = families.resolve(needs, revit, answers, templates=templates, year=year)
    return make(resolution, needs, building, built, furniture=furniture, mep=mep, built_fitout=built_fitout,
                voltage_v=voltage_v)


def levels_of(building: BuildPlan) -> dict[str, float]:
    """The building's levels: name -> elevation (its own and those it was built on)."""
    levels = {op.name: op.elevation_mm for op in building.ops if isinstance(op, LevelOp)}
    levels.update(building.existing_levels)
    return levels


def plan_furniture(revit: families.Revit, building: BuildPlan, built: dict[str, int], model: RoomModel, rules: Any, *,
                   answers: dict[str, Any] | None = None, measurements: list[Measurement] | None = None,
                   mep: bool = False, shaft_kinds: Mapping[str, str] | None = None, document: str | None = None,
                   measure_level: str | None = None, cache: dict | None = None, templates: list[str] | None = None,
                   year: int | str | None = None, built_fitout: Mapping[str, int] | None = None) -> FitoutPlan:
    """Families first, then the layout, then the plan:

    1. a family type per rule item (``families.resolve_items``: answers ``fitout:family:<item>``, else the best match),
       each project type measured (``measurements`` given, else the add-in's measure_family_types on
       ``measure_level``), items the user skips or a chosen family includes left out;
    2. the layout with each measured item at its family's own footprint (its smaller types when it does not fit) and
       the rule's clearances, so what does not fit is reported as it really is; with ``mep``, the MEP layout on that
       furniture layout (``mep.layout``: its runs end at the fixtures where they really stand; ``shaft_kinds`` as it
       takes them), its questions and assumptions joining the plan's;
    3. the plan: measured items placed with the type the layout used, their insertion worked out from the measured box
       (``measured_point``); the rest (a counter's legs, template types, MEP) matched per size as ``plan_fitout``."""
    answers = answers or {}
    search = families._Search(revit, year, None, templates)
    items = families.resolve_items(model, rules, revit, answers, measurements=measurements, document=document,
                                   level=measure_level, cache=cache, search=search)
    layout = furniture_solver.solve(model, furniture_solver.with_sizes(rules, items.sizes(), items.no_sides),
                                    leave_out=items.left_out)
    mep_layout = mep_solver.layout(building, model, layout, rules, answers, shaft_kinds) if mep else None
    mep = mep_plan.records(mep_layout, levels_of(building)) if mep_layout is not None else None
    records = {p.key: p for p in layout.placed}
    devices = list(layout.placed) + _mep_part(mep, "devices")
    needs = families.needs_from(devices) + families.run_needs(_mep_part(mep, "runs"))
    pinned: dict[str, Choice] = {}
    asked: dict[str, list[str]] = {}
    rest: list[Need] = []
    for need in needs:
        fam = items.items.get(need.item) if not need.mep else None
        record = records.get(need.keys[0]) if need.keys else None
        if fam is not None and fam.options and record is not None:
            option = fam.options[min(record.size_index, len(fam.options) - 1)]
            pinned[need.id] = option.model_copy(update={"need": need.id})
        elif record is not None and need.item in items.asked:
            asked.setdefault(items.asked[need.item], []).extend(need.keys)
        else:
            rest.append(need)
    found = families.resolve(rest, revit, answers, search=search)
    questions = [q.model_copy(update={"instances": asked[q.id]}) for q in items.questions if q.id in asked]
    resolution = Resolution(choices={**found.choices, **pinned}, questions=questions + found.questions,
                            assumptions=[*items.assumptions, *found.assumptions], skipped=found.skipped,
                            searched=list(dict.fromkeys(items.searched + found.searched)))
    result = make(resolution, needs, building, built, furniture=layout, mep=mep, built_fitout=built_fitout,
                  voltage_v=supply_voltage(rules))
    update: dict[str, Any] = {"layout": layout, "items": items}
    if mep_layout is not None:
        update.update(mep=mep_layout, questions=[*result.questions, *mep_layout.questions],
                      ready=result.ready and not mep_layout.questions,
                      assumptions=[*result.assumptions, *mep_layout.assumptions, *mep_plan.assumptions(mep_layout)])
    return result.model_copy(update=update)


def supply_voltage(rules: Any) -> float | None:
    """The rule set's single-phase supply voltage (``electrical.supply_voltage_v``), None when it gives none."""
    value = getattr(getattr(getattr(rules, "electrical", None), "supply_voltage_v", None), "value", None)
    return float(value) if value is not None else None


def measured_point(m: Measurement, front: str, point: tuple[float, float], rotation_deg: float) -> tuple[tuple[float, float], float]:
    """Where to insert a measured family, and its turn, so that its box lands on the layout's footprint: the layout
    gives the middle of the item's back edge and the turn for a family whose front faces -Y; the family's own back
    middle (its box turned so its front faces -Y: the middle of the box's +Y edge) goes on that point."""
    x0, _, x1, y1 = m.item_box(front)
    back = turn(((x0 + x1) / 2, y1), rotation_deg)
    return (round(point[0] - back[0], 1), round(point[1] - back[1], 1)), rotation_deg + FRONT_TURN[front]


OWN_MOUNTING_MM = 300.0  # a fixture whose geometry starts this high above its origin carries its own mounting height


def measured_offset(m: Measurement, elevation: float, on_counter: bool) -> float:
    """The offset above the level: the layout's elevation (a counter top for a sink or hob, whose family origin is on
    the surface it sits in), raised for an item on the floor whose geometry reaches below its origin. The measured
    base offset (a family that took the point's z as an offset) is the add-in's way of placing, not the family's."""
    if not on_counter and m.base_mm < -BASE_TOL_MM:
        return elevation - m.base_mm
    if on_counter and m.base_mm > OWN_MOUNTING_MM:
        return 0.0  # drawn at its own counter height (a kitchen sink starting 684 mm up): on the level, not raised again
    return elevation


WALL_HOSTS = ("wall", "outside_wall")


def device_offset(record: Any, m: Measurement | None) -> tuple[float, str | None] | None:
    """The offset above the level of a level-based MEP device, and how it was found; None for anything else. The
    layout gives a wall device's centre height and a ceiling device's ceiling height, not where the family's origin
    goes: on a wall, the family's box is centred on that height (offset = centre - half its height - its box's
    bottom below its origin); on the ceiling, its top is at the ceiling (offset = ceiling - its box's top above its
    origin). The box is the measured one; else the rule's height with the origin at the base (as the box families
    Plinth creates have); else, nothing known, the origin goes at that height and the note says so."""
    kind = _get(record, "host_kind")
    height = _get(record, "height_mm")
    if kind in WALL_HOSTS:
        centre = float(_get(record, "center_mm") if _get(record, "center_mm") is not None else _get(record, "elevation_mm") or 0)
        if m is not None:
            return round(centre - m.height_mm / 2 - m.base_mm, 1), None
        if height:
            return round(centre - float(height) / 2, 1), (f"not measured: its origin taken as its base, the rule's "
                                                          f"{float(height):g} mm high box centred at {centre:g} mm")
        return centre, f"its height not known: its origin placed at the device's centre height ({centre:g} mm)"
    if kind == "ceiling" or (kind is None and _get(record, "face_down")):
        ceiling = float(_get(record, "elevation_mm") or 0)
        if m is not None:
            top = m.max[2] - m.base_offset_mm
            return round(ceiling - top, 1), (f"its family is level-based: hung with its top ({top:g} mm above its "
                                             f"origin, measured) at the ceiling's height {ceiling:g} mm, not facing down")
        if height:
            return round(ceiling - float(height), 1), (f"its family is level-based: hung with its top at the ceiling's "
                                                       f"height {ceiling:g} mm (the rule's {float(height):g} mm high, "
                                                       f"its origin taken as its base), not facing down")
        return ceiling, (f"its family is level-based: its origin placed at the ceiling's height {ceiling:g} mm (its "
                         f"height not known), not facing down")
    return None


def make(resolution: Resolution, needs: list[Need], building: BuildPlan, built: dict[str, int], *,
         furniture: Any = None, mep: Any = None, built_fitout: Mapping[str, int] | None = None,
         voltage_v: float | None = None) -> FitoutPlan:
    """The build plan for the layout with the families resolved (see the module docstring). ``mep``: the MEP layout or
    its records; ``voltage_v``: the supply voltage of box families' electrical connectors."""
    mep = mep_records(mep, building)
    built_fitout = dict(built_fitout or {})
    levels = levels_of(building)
    walls = {op.key for op in building.ops if isinstance(op, WallOp)}
    need_of: dict[tuple[str, str], Need] = {}
    for need in needs:
        for key in need.keys:
            need_of[(key, need.category)] = need
    by_key_need = {key: need for need in needs if not need.mep for key in need.keys}
    question_of = {key: q for q in resolution.questions for key in q.instances}

    ops: list[dict[str, Any]] = []
    left: list[LeftOut] = []
    notes: list[Assumption] = []
    used: dict[str, Choice] = {}
    existing: dict[str, int] = {}
    used_levels: set[str] = set()
    placed: set[str] = set(built_fitout)  # an earlier run's records: joined, not placed again
    chosen: dict[str, Choice] = {}  # placed record key -> its family type
    records: dict[str, Any] = {}

    def leave(key: str, item: str, reason: str) -> None:
        left.append(LeftOut(key=key, item=item, reason=reason))

    def choice_for(key: str, need: Need | None, item: str) -> Choice | None:
        if need is None:
            leave(key, item, "no family need for it")
            return None
        if need.id in resolution.skipped:
            leave(key, item, "left out by the user's answer")
            return None
        choice = resolution.choices.get(need.id)
        if choice is None:
            q = question_of.get(key)
            leave(key, item, f"waiting for a family type ({q.id})" if q else "no family type matched")
        return choice

    # ---------------------------------------------------------------- placed families
    devices = list(_get(furniture, "placed") or []) + _mep_part(mep, "devices")
    for record in devices:
        key, item = _get(record, "key"), _get(record, "item") or _get(record, "label") or "item"
        level = _get(record, "level")
        if level not in levels:
            leave(key, item, f"level {level!r} is not a level of the building")
            continue
        choice = choice_for(key, by_key_need.get(key), item)
        if choice is None:
            continue
        op, why = _family_op(record, choice, walls, built)
        if op is None:
            leave(key, item, why)
            continue
        if why:
            notes.append(Assumption(id=f"fitout:placement:{key}", text=f"{_get(record, 'label') or item}: {why}",
                                    value=choice.placement, source="family placement", instances=[key]))
        if op.get("host_key"):
            existing[op["host_key"]] = built[op["host_key"]]
        used_levels.add(level)
        used[choice.need] = choice
        placed.add(key)
        chosen[key], records[key] = choice, record
        ops.append(op)

    # ---------------------------------------------------------------- MEP runs and circuits
    for run in _mep_part(mep, "runs"):
        key, kind, level = _get(run, "key"), _get(run, "kind"), _get(run, "level")
        what = f"{kind} run"
        if level not in levels:
            leave(key, what, f"level {level!r} is not a level of the building")
            continue
        ends = [k for k in (_get(run, "start_key"), _get(run, "end_key")) if k]
        gone = [k for k in ends if k not in placed]
        if gone:
            leave(key, what, f"it joins {', '.join(gone)}, which is not in the plan")
            continue
        type_choice = choice_for(key, need_of.get((key, f"{kind}_type")), what)
        system_choice = None
        if kind in ("pipe", "duct"):
            system_choice = choice_for(key, need_of.get((key, f"{kind}_system_type")), what)
            if system_choice is None:
                continue
        if type_choice is None:
            continue
        z0 = levels[level]
        op = {"op": "mep_run", "key": key, "kind": kind, "level": level, "type": type_choice.type,
              "points": [(x, y, z + z0) for x, y, z in _get(run, "points")]}
        if system_choice is not None:
            op["system_type"] = system_choice.type
            used[system_choice.need] = system_choice
        for name in ("diameter_mm", "width_mm", "height_mm", "start_key", "end_key", "source"):
            if _get(run, name) is not None:
                op[name] = _get(run, name)
        # best effort, reported: a family may have no connector of the run's domain, a type no elbow at a size
        op["require_connections"] = False
        used[type_choice.need] = type_choice
        used_levels.add(level)
        placed.add(key)
        records[key] = run
        ops.append(op)

    for circuit in _mep_part(mep, "circuits"):
        key, panel = _get(circuit, "key"), _get(circuit, "panel_key")
        devices_in = [k for k in _get(circuit, "device_keys") or [] if k in placed]
        missing = [k for k in _get(circuit, "device_keys") or [] if k not in placed]
        if panel and panel not in placed:
            leave(key, "circuit", f"its panel {panel} is not in the plan")
            continue
        if not devices_in:
            leave(key, "circuit", "none of its devices is in the plan")
            continue
        if missing:
            notes.append(Assumption(id=f"fitout:circuit:{key}", text=f"circuit {key}: without {', '.join(missing)} "
                                    f"(not in the plan)", value=missing, source="fit-out plan", instances=[key]))
        op = {"op": "circuit", "key": key, "device_keys": devices_in,
              "system_type": _get(circuit, "system_type") or "PowerCircuit", "best_effort": True}
        if panel:
            op["panel_key"] = panel
        if _get(circuit, "source"):
            op["source"] = _get(circuit, "source")
        ops.append(op)

    connectors, capped = _box_connectors(ops, chosen, records, voltage_v)
    notes += capped
    type_ops = _type_ops(used.values(), connectors)
    all_ops = type_ops + ops
    op_keys = {op["key"] for op in ops}
    for op in ops:  # an earlier run's record a run or circuit joins: looked up, as the building's walls
        for ref in (op.get("start_key"), op.get("end_key"), op.get("panel_key"), *(op.get("device_keys") or [])):
            if ref in built_fitout and ref not in op_keys:
                existing[ref] = built_fitout[ref]
    plan, again = None, {}
    if ops:
        plan = BuildPlan.model_validate({
            "ops": all_ops, "existing_levels": {name: levels[name] for name in sorted(used_levels)},
            "existing": dict(sorted(existing.items()))})
        if built_fitout:
            again = {op.key: op for op in plan.ops if isinstance(op, FamilyOp) and op.key in built_fitout}
            plan = prune(plan, existing=built_fitout).plan
    # a question only about records an earlier run built is not this plan's
    questions = [q for q in resolution.questions if not q.instances or not set(q.instances) <= set(built_fitout)]
    return FitoutPlan(plan=plan, ready=not questions, questions=questions,
                      assumptions=[*resolution.assumptions, *notes], left_out=left, families=resolution.choices,
                      needs=needs, already_built=again)


def _box_connectors(ops: list[dict[str, Any]], chosen: dict[str, Choice], records: dict[str, Any],
                    voltage_v: float | None = None) -> tuple[dict[str, list[dict[str, Any]]], list[Assumption]]:
    """Box family (casefolded name) -> the connectors the plan joins to its instances: an electrical one (at
    ``voltage_v``, the supply's) when any of them is on a circuit (or is a panel), a pipe connector per system
    classification and diameter of the pipe runs ending at them; at most ``MAX_BOX_CONNECTORS`` (the rest listed
    back)."""
    box = {key: c.family.casefold() for key, c in chosen.items() if c.source == "create" and c.family}
    if not box:
        return {}, []
    powered: dict[str, bool] = {}
    pipes: dict[str, set[tuple[str, float]]] = {}
    for op in ops:
        if op["op"] == "circuit":
            for key in [*op["device_keys"], *([op["panel_key"]] if op.get("panel_key") else [])]:
                if key in box:
                    powered[box[key]] = True
        elif op["op"] == "mep_run" and op["kind"] == "pipe":
            classification = _get(records[op["key"]], "system")
            for key in (op.get("start_key"), op.get("end_key")):
                if key in box and classification and op.get("diameter_mm"):
                    pipes.setdefault(box[key], set()).add((classification, float(op["diameter_mm"])))
    for key, family in box.items():
        if _get(records[key], "panel"):
            powered[family] = True
    out: dict[str, list[dict[str, Any]]] = {}
    notes: list[Assumption] = []
    for family in sorted(set(powered) | set(pipes)):
        power = {"domain": "electrical", "system_type": "PowerCircuit",
                 **({"voltage_v": float(voltage_v)} if voltage_v else {})}
        wanted = ([power] if powered.get(family) else []) + [
            {"domain": "pipe", "classification": c, "diameter_mm": d} for c, d in sorted(pipes.get(family, ()))]
        out[family] = wanted[:MAX_BOX_CONNECTORS]
        if len(wanted) > MAX_BOX_CONNECTORS:
            notes.append(Assumption(id=f"fitout:box-connectors:{family}", text=f"box family {family!r}: the first "
                                    f"{MAX_BOX_CONNECTORS} of its {len(wanted)} connectors are made (the add-in's "
                                    f"limit); runs to the others join best effort", value=wanted[MAX_BOX_CONNECTORS:],
                                    source="create_box_family"))
    return out, notes


def prune(plan: BuildPlan, drop: Iterable[str] = (), existing: Mapping[str, int] | None = None,
          why: str = "left out") -> Pruned:
    """The plan without the ops ``drop`` names (``why``) and without the ops ``existing`` names (an earlier run built
    them: they stay joinable, as ``BuildPlan.existing``), and without whatever then joins an op that is gone: a run
    with an end at it, a family hosted on it, a circuit on its panel; a circuit only loses a device that is gone
    (``trimmed``), unless none is left. Then the types nothing uses any more go (a template item, a size copy, a box
    family), and the levels and existing elements nothing refers to. None when no element is left."""
    existing = dict(existing or {})
    drop = set(drop)
    dropped: dict[str, str] = {}
    trimmed: dict[str, list[str]] = {}
    kept: dict[str, Any] = {}
    for op in plan.ordered():  # a run joins only runs listed before it: one pass in build order settles it
        if op.op in TYPE_OPS:
            continue
        if op.key in existing:
            dropped[op.key] = "built by an earlier run"
            continue
        if op.key in drop:
            dropped[op.key] = why
            continue
        gone = [k for k in references(op) if k in dropped and k not in existing]
        if isinstance(op, CircuitOp):
            devices = [k for k in op.device_keys if k not in gone]
            if op.panel_key in gone or not devices:
                what = f"its panel {op.panel_key}" if op.panel_key in gone else "every device of it"
                dropped[op.key] = f"{what} is left out"
                continue
            if len(devices) < len(op.device_keys):
                trimmed[op.key] = [k for k in op.device_keys if k in gone]
                op = op.model_copy(update={"device_keys": devices})
        elif gone:
            dropped[op.key] = f"it joins {', '.join(gone)}, which is left out"
            continue
        kept[op.key] = op
    elements = [kept[op.key] for op in plan.ops if op.key in kept]
    if not elements:
        return Pruned(plan=None, dropped=dropped, trimmed=trimmed)
    placed = {(op.family, op.type) for op in elements if isinstance(op, FamilyOp)}
    families_used = {op.family.casefold() for op in elements if isinstance(op, FamilyOp) and op.family}
    copies = [op for op in plan.ops if isinstance(op, FamilyTypeCopyOp) and (op.family, op.name) in placed]
    wanted = {("family_type", f, t) for f, t in placed} | {("family_type", op.family, op.base_type) for op in copies}
    for op in elements:
        if isinstance(op, MepRunOp):
            wanted.add((f"{op.kind}_type", None, op.type))
            if op.system_type:
                wanted.add((f"{op.kind}_system_type", None, op.system_type))
    types: list[Any] = []
    for op in plan.ops:
        if isinstance(op, TypesFromFileOp):
            items = [i for i in op.items if (i.kind, i.family, i.name) in wanted]
            if items:
                types.append(op.model_copy(update={"items": items}))
        elif isinstance(op, FamilyCreateOp) and op.family.casefold() in families_used:
            types.append(op)
        elif op in copies:
            types.append(op)
    refs = {k for op in elements for k in references(op)}
    used_levels = {getattr(op, "level", None) for op in elements}
    joined = {k: v for k, v in {**plan.existing, **existing}.items() if k in refs}
    pruned = BuildPlan.model_validate({
        "plan_version": plan.plan_version, "spec_hash": plan.spec_hash,
        "ops": [op.model_dump(mode="json") for op in [*types, *elements]],
        "existing_levels": {k: v for k, v in plan.existing_levels.items() if k in used_levels},
        "existing": dict(sorted(joined.items()))})
    return Pruned(plan=pruned, dropped=dropped, trimmed=trimmed)


def _family_op(record: Any, choice: Choice, walls: set[str], built: dict[str, int]) -> tuple[dict | None, str | None]:
    """The family op for a record, placed as its family allows, or None and why it cannot be; with a note when the
    placement was decided here."""
    key = _get(record, "key")
    point = _get(record, "insertion") or _get(record, "point")
    toward = _get(record, "toward") or _get(record, "center")
    elevation = float(_get(record, "elevation_mm") or 0)
    wall = _get(record, "wall_key")
    wall_built = wall is not None and wall in walls and wall in built
    face_down = bool(_get(record, "face_down"))
    op: dict[str, Any] = {"op": "family", "key": key, "category": choice.category, "type": choice.type,
                          "level": _get(record, "level"), "point": tuple(point)}
    if choice.family:
        op["family"] = choice.family
    if elevation:
        op["offset_mm"] = elevation
    for name in ("room_key", "source"):
        if _get(record, name):
            op[name] = _get(record, name)
    note = None
    placement = choice.placement
    on_counter = bool(_get(record, "host"))  # a sink or hob: on the counter top, never on the wall behind it
    m = choice.measured
    if m is not None and (placement in LEVEL_BASED or placement == "WorkPlaneBased" and on_counter and not face_down):
        at, rotation = measured_point(m, choice.front, tuple(point), float(_get(record, "rotation_deg") or 0))
        op["point"] = at
        mep_offset = device_offset(record, m) if placement in LEVEL_BASED else None
        offset = mep_offset[0] if mep_offset else measured_offset(m, elevation, on_counter)
        note = mep_offset[1] if mep_offset else None
        op.pop("offset_mm", None)
        if offset:
            op["offset_mm"] = round(offset, 1)
        if _rotation(rotation):
            op["rotation_deg"] = _rotation(rotation)
        w, d, _ = m.size(choice.front)
        how = "the box family Plinth creates" if choice.source == "create" else "its measured box"
        op["source"] = (f"{op['source']}; " if op.get("source") else "") + \
            (f"placed by {how} ({w:g} x {d:g} mm, front {choice.front.upper()}; {m.describe()}): its back's "
             f"middle on the layout's point {tuple(round(v, 1) for v in point)}")
        return op, note
    if placement == "WorkPlaneBased" and on_counter and not face_down:  # not measured: on the level's plane
        if _rotation(float(_get(record, "rotation_deg") or 0)):
            op["rotation_deg"] = _rotation(float(_get(record, "rotation_deg")))
        return op, "its family is work-plane-based: placed on the level's plane at the counter top, not on the wall"
    if placement in LEVEL_BASED:
        if _rotation(float(_get(record, "rotation_deg") or 0)):
            op["rotation_deg"] = _rotation(float(_get(record, "rotation_deg")))
        notes = ["placement type of the project's family not known: placed as a level-based family"] if placement is None else []
        mep_offset = device_offset(record, None)
        if mep_offset:
            op.pop("offset_mm", None)
            if mep_offset[0]:
                op["offset_mm"] = mep_offset[0]
            notes += [mep_offset[1]] if mep_offset[1] else []
        return op, "; ".join(notes) or None
    if placement == "OneLevelBasedHosted":
        if not wall_built:
            return None, f"its family ({choice.family}) is wall-hosted and the item has no built wall behind it"
        op.update(host_key=wall, toward=tuple(toward))
        return op, None
    if placement == "WorkPlaneBased":
        if wall_built and not face_down:
            op.update(host_key=wall, toward=tuple(toward))
        elif face_down:
            op["face_down"] = True
        elif _rotation(float(_get(record, "rotation_deg") or 0)):
            op["rotation_deg"] = _rotation(float(_get(record, "rotation_deg")))
        return op, None
    return None, f"its family ({choice.family}) is placed {placement}, which the fit-out does not place"


def _type_ops(choices: Iterable[Choice], connectors: Mapping[str, list[dict[str, Any]]] | None = None
              ) -> list[dict[str, Any]]:
    """types_from_file per template the used types come from (sorted, so the plan is the same every time), a
    family_create per box family (with the ``connectors`` its instances are joined by), then a family_type_copy per
    size copy."""
    by_file: dict[str, dict[tuple, dict[str, Any]]] = {}
    copies: dict[tuple, dict[str, Any]] = {}
    boxes: dict[str, dict[str, Any]] = {}
    for c in choices:
        if c.source == "create" and c.box is not None and c.family:
            box = {"op": "family_create", "key": FAMILY_KEY + _slug(c.family), "kind": c.box.kind, "category": c.category,
                   "family": c.family, "type": c.type, "width_mm": c.box.width_mm, "depth_mm": c.box.depth_mm,
                   "height_mm": c.box.height_mm}
            if c.box.material:
                box["material"] = c.box.material
            if (connectors or {}).get(c.family.casefold()):  # the family's, whichever size asks: the same for each
                box["connectors"] = connectors[c.family.casefold()]
            if boxes.setdefault(c.family.casefold(), box) != box:
                raise ValueError(f"box family {c.family!r} is asked at two sizes; name each size's family apart")
            continue
        if c.source == "template" and c.path:
            name = c.base_type or c.type
            item: dict[str, Any] = {"kind": c.kind, "name": name}
            if c.kind == "family_type":
                item.update(family=c.family, category=c.category)
            by_file.setdefault(c.path, {})[(c.kind, c.family or "", name)] = item
        if c.base_type:
            copies[(c.family, c.type)] = {"op": "family_type_copy", "key": TYPE_COPY_KEY + _slug(f"{c.family}-{c.type}"),
                                          "category": c.category, "family": c.family, "base_type": c.base_type,
                                          "name": c.type, "values_mm": dict(c.values_mm)}
    ops = []
    for path in sorted(by_file):
        stem = path.replace("/", "\\").rsplit("\\", 1)[-1].rsplit(".", 1)[0]
        items = [by_file[path][k] for k in sorted(by_file[path])]
        for n, start in enumerate(range(0, len(items), MAX_COPY_ITEMS), 1):  # the add-in copies 100 items a call
            ops.append({"op": "types_from_file", "key": TYPES_KEY + _slug(stem) + (f"-{n}" if n > 1 else ""),
                        "path": path, "items": items[start:start + MAX_COPY_ITEMS]})
    return ops + [boxes[k] for k in sorted(boxes)] + [copies[k] for k in sorted(copies)]
