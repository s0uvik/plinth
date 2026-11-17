"""Compliance checks (PRD F5.1, F8.1): NBC 2016 and Kolkata rules from plinth/rules, run on the engine's geometry
before a build and on rooms read back from Revit after it. The report is advisory (PRD non-goal: submission).

    facts = facts_before_build(spec, engine_result.report)
    report = evaluate(facts)          # {"summary": {"hard_fail": ..}, "findings": [...], ...}
"""

from __future__ import annotations

import re
from dataclasses import asdict, dataclass, field
from datetime import datetime, timezone
from typing import Any, Iterable

from plinth.rules import rule_set
from plinth.spec import BuildingSpec

RULE_SETS = ("nbc2016", "kolkata")
EXT_HALF = 115
SLAB = {"RCC 150": 150}
ADVISORY = ("Advisory only: this report checks the model against the rule values configured in Plinth. It is not a "
            "submission check and does not replace an architect's review.")


@dataclass
class RoomFact:
    subject: str
    kind: str
    net_width_mm: float
    net_area_m2: float
    window_m2: float | None = None
    ventilated_area_m2: float | None = None


@dataclass
class Facts:
    phase: str  # "before_build" or "after_build"
    spec_hash: str
    plot_area_m2: float
    road_width_mm: int
    height_mm: int
    floor_to_floor_mm: int
    slab_mm: int
    setbacks_mm: dict[str, float]
    ground_coverage: float
    far: float
    stair: dict[str, float]
    lift: bool
    rooms: list[RoomFact] = field(default_factory=list)


@dataclass
class Finding:
    rule_id: str
    title: str
    clause: str
    rule_set: str
    severity: str  # "hard" or "soft"
    status: str  # "pass", "fail" or "not_checked"
    subject: str
    measured: str
    required: str
    verified: bool


def kind_of(room_name: str) -> str | None:
    """Room kind from the names the engine gives rooms."""
    name = room_name.lower()
    if name.startswith("living"):
        return "living"
    if name == "bedroom 1":
        return "bedroom_master"
    if name.startswith("bedroom"):
        return "bedroom"
    if name.startswith("kitchen"):
        return "kitchen"
    if name.startswith("toilet"):
        return "toilet"
    return None


def _site_facts(spec: BuildingSpec, report: dict[str, Any]) -> dict[str, Any]:
    plot_w, plot_d = spec.site.plot_mm
    p = report["plate_mm"]
    return dict(
        spec_hash=spec.hash(), plot_area_m2=plot_w * plot_d / 1e6, road_width_mm=spec.site.road_width_mm,
        height_mm=report["height_mm"], floor_to_floor_mm=spec.building.floor_to_floor_mm,
        slab_mm=SLAB[spec.standards.slab],
        setbacks_mm={"front": p["y0"] - EXT_HALF, "rear": plot_d - p["y1"] - EXT_HALF,
                     "side_left": p["x0"] - EXT_HALF, "side_right": plot_w - p["x1"] - EXT_HALF},
        ground_coverage=report["ground_coverage"], far=report["far"],
        stair={"width_mm": report["stair"]["width_mm"], "riser_mm": report["stair"]["riser_mm"],
               "tread_mm": report["stair"]["tread_mm"]},
        lift=spec.core.lift)


def facts_before_build(spec: BuildingSpec, report: dict[str, Any]) -> Facts:
    """Facts from the engine's layout: one set of rooms per flat, the same on every floor with flats."""
    rooms = [RoomFact(f"Flat {f['letter']} {r['name']}", r["kind"], r["net_width_mm"], r["net_area_m2"],
                      r.get("window_m2"), r.get("ventilated_area_m2"))
             for f in report["flats_per_floor"] for r in f["rooms"]]
    return Facts(phase="before_build", rooms=rooms, **_site_facts(spec, report))


def facts_after_build(spec: BuildingSpec, report: dict[str, Any], revit, room_ids: Iterable[int]) -> Facts:
    """Room facts read back from Revit for the rooms a run created; site facts from the layout report."""
    wanted = set(room_ids)
    listed = revit.call("list_rooms", {"limit": 20000})["rooms"]
    rooms = []
    for room in listed:
        if room["element_id"] not in wanted:
            continue
        kind = kind_of(room.get("name") or "")
        if kind is None:
            continue
        boundary = revit.call("get_room_boundaries", {"room_id": room["element_id"], "include_boundary_elements": False})
        points = [(s["start"]["x_mm"], s["start"]["y_mm"]) for loop in boundary["loops"][:1] for s in loop["segments"]]
        width = min(max(x for x, _ in points) - min(x for x, _ in points),
                    max(y for _, y in points) - min(y for _, y in points)) if points else 0
        rooms.append(RoomFact(f"{room.get('department') or ''} {room.get('name')} ({room.get('level', {}).get('name', '')})".strip(),
                              kind, width, room["area_m2"]))
    return Facts(phase="after_build", rooms=rooms, **_site_facts(spec, report))


def _band(bands: list[dict], key: str, value: float) -> float:
    for band in bands:
        if band[key] is None or value <= band[key]:
            return band["max"]
    return bands[-1]["max"]


def _m(mm: float) -> str:
    return f"{mm / 1000:.2f} m"


def evaluate(facts: Facts) -> dict[str, Any]:
    findings: list[Finding] = []
    sets = []
    for name in RULE_SETS:
        rs = rule_set(name)
        verified = bool(rs.get("verified"))
        sets.append({"name": rs["name"], "verified": verified, "source": rs.get("source", "")})

        def add(rule: dict, status: str, subject: str, measured: str, required: str) -> None:
            findings.append(Finding(rule["id"], rule["title"], rule.get("clause", ""), rs["name"], rule.get("severity", "hard"),
                                    status, subject, measured, required, verified))

        for kind, need in (rs.get("room_minimums") or {}).items():
            rule = {"id": f"{name.upper()}-ROOM-{kind.upper()}", "title": f"Minimum size: {kind.replace('_', ' ')}",
                    "clause": need.get("clause", ""), "severity": "hard"}
            for room in (r for r in facts.rooms if r.kind == kind):
                ok = room.net_width_mm >= need["width_mm"] and room.net_area_m2 >= need["area_m2"]
                add(rule, "pass" if ok else "fail", room.subject, f"{_m(room.net_width_mm)} wide, {room.net_area_m2:.2f} m²",
                    f"{_m(need['width_mm'])} wide, {need['area_m2']} m²")

        for rule in rs.get("rules") or []:
            t = rule["type"]
            if t == "clear_height":
                clear = facts.floor_to_floor_mm - facts.slab_mm
                ok = clear >= rule["min_mm"]
                add(rule, "pass" if ok else "fail", ", ".join(k.replace("_", " ") for k in rule["kinds"]),
                    _m(clear), f"at least {_m(rule['min_mm'])}")
            elif t == "window_ratio":
                for room in (r for r in facts.rooms if r.kind in rule["kinds"]):
                    if room.window_m2 is None or not room.ventilated_area_m2:
                        add(rule, "not_checked", room.subject, "windows are checked before the build", f"{rule['min_ratio']:.0%} of floor area")
                        continue
                    ratio = room.window_m2 / room.ventilated_area_m2
                    add(rule, "pass" if ratio >= rule["min_ratio"] else "fail", room.subject,
                        f"{room.window_m2:.2f} m² for {room.ventilated_area_m2:.2f} m² ({ratio:.0%})", f"{rule['min_ratio']:.0%} of floor area")
            elif t == "stair":
                value = facts.stair[rule["field"]]
                ok = ("min" not in rule or value >= rule["min"]) and ("max" not in rule or value <= rule["max"])
                limit = f"at least {rule['min']} mm" if "min" in rule else f"at most {rule['max']} mm"
                add(rule, "pass" if ok else "fail", "Stair", f"{value:g} mm", limit)
            elif t == "height_notice":
                above = facts.height_mm > rule["above_mm"]
                add(rule, "fail" if above else "pass", "Building", _m(facts.height_mm), f"at most {_m(rule['above_mm'])} for low-rise")
            elif t == "setbacks":
                row = next((r for r in rs["setbacks"] if facts.height_mm <= r["max_height"]), None)
                if row is None:
                    add(rule, "fail", "Building", _m(facts.height_mm), f"the table covers up to {_m(rs['setbacks'][-1]['max_height'])}")
                    continue
                for side in ("front", "rear", "side_left", "side_right"):
                    have = facts.setbacks_mm[side]
                    add(rule, "pass" if have >= row[side] else "fail", f"Open space, {side.replace('_', ' ')}",
                        _m(have), f"at least {_m(row[side])}")
            elif t == "ground_coverage":
                limit = _band(rule["bands"], "max_plot_m2", facts.plot_area_m2)
                add(rule, "pass" if facts.ground_coverage <= limit else "fail", "Building",
                    f"{facts.ground_coverage:.1%}", f"at most {limit:.0%} on a {facts.plot_area_m2:.0f} m² plot")
            elif t == "far":
                limit = _band(rule["bands"], "max_road_mm", facts.road_width_mm)
                add(rule, "pass" if facts.far <= limit else "fail", "Building", f"{facts.far:.2f}",
                    f"at most {limit:.2f} on a {_m(facts.road_width_mm)} road")
            elif t == "height":
                limit = _band(rule["bands"], "max_road_mm", facts.road_width_mm)
                add(rule, "pass" if facts.height_mm <= limit else "fail", "Building", _m(facts.height_mm),
                    f"at most {_m(limit)} on a {_m(facts.road_width_mm)} road")
            elif t == "lift":
                needs = facts.height_mm > rule["above_mm"]
                ok = facts.lift or not needs
                add(rule, "pass" if ok else "fail", "Building", "lift" if facts.lift else "no lift",
                    f"a lift above {_m(rule['above_mm'])}")

    order = {("fail", "hard"): 0, ("fail", "soft"): 1, ("not_checked", "hard"): 2, ("not_checked", "soft"): 2}
    findings.sort(key=lambda f: (order.get((f.status, f.severity), 3), f.rule_id, f.subject))
    summary = {
        "hard_fail": sum(f.status == "fail" and f.severity == "hard" for f in findings),
        "soft_fail": sum(f.status == "fail" and f.severity == "soft" for f in findings),
        "passed": sum(f.status == "pass" for f in findings),
        "not_checked": sum(f.status == "not_checked" for f in findings),
        "provisional": sum(not f.verified for f in findings),
    }
    return {
        "phase": facts.phase,
        "spec_hash": facts.spec_hash,
        "checked_utc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "advisory": ADVISORY,
        "rule_sets": sets,
        "summary": summary,
        "findings": [asdict(f) for f in findings],
    }


def hard_failures(report: dict[str, Any]) -> list[str]:
    return [f"{f['rule_id']}: {f['subject']}: {f['measured']} (required {f['required']})"
            for f in report["findings"] if f["status"] == "fail" and f["severity"] == "hard"]


def safe_name(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "-", text).strip("-")
