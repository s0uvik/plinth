"""The MEP layout (F4) as the records ``to_plan`` builds from (F5): one adapter, so the layout keeps describing the
systems in its own terms and the plan keeps speaking Revit's.

    records = mep_plan.records(layout, levels)       # {"devices": [...], "runs": [...], "circuits": [...]}
    found = to_plan.plan_fitout(revit, building, built, furniture=furniture_layout, mep=records)
    mep_plan.system_of(layout)                       # record key -> furniture / plumbing / electrical / hvac / fire

Why the shapes differ: the layout says where a device is fixed (``Host``: a wall's side, the ceiling, the floor,
outside a wall) and which way it faces, and gives a run's points at their absolute height with plinth's own system
names (cold_water, soil, power...). The plan places a family on a wall by a point on the side it faces (``toward``),
puts a light or fan with no ceiling facing down at its height (the building has no ceilings), takes a run's points
above its level and its system as a Revit system classification, to match the project's piping system types by.

- devices: ``item`` is the device's catalogue key, ``point`` its insertion (on the wall face for a wall device, its
  centre otherwise). On a wall (inside or out): ``wall_key`` and ``toward``, a point ``TOWARD_MM`` along the way it
  faces (into the room; an AC outdoor unit faces out of its wall, so its point is outside: the family goes on the
  wall's outer face). On the ceiling: ``face_down`` at the ceiling's height. On the floor: level-based at its
  elevation (an underground tank below the level). Sized devices give width, depth and height (a box family Plinth
  creates for one, when the user answers so, is made at that size). The source keeps the rule's source, the rating
  and the layout's note.
- runs: ``system`` is the Revit system classification (``CLASSIFICATION``; conduits have none), ``points`` z above the
  run's level, ``diameter_mm`` the nominal size, ``start_key``/``end_key`` the device, furniture fixture or run each
  end joins; the slope and note go in the source.
- circuits: power circuits of the board ``panel_key``, their kind, load and rating in the source.

Rainwater: Revit's metric templates have no storm piping system (classification), so rainwater pipes are matched to a
system of classification OtherPipe ("Other"), assumed and listed back (``assumptions``).
"""

from __future__ import annotations

import math
from typing import Any, Mapping

from plinth.fitout.mepcore import Circuit, Device, MepLayout, Run
from plinth.fitout.rooms import Assumption

TOWARD_MM = 200.0  # a wall device's side point: this far along the way it faces

# plinth's run systems -> Revit system classifications (MEPSystemClassification names; pipes only).
CLASSIFICATION = {"cold_water": "DomesticColdWater", "hot_water": "DomesticHotWater", "soil": "Sanitary",
                  "waste": "Sanitary", "vent": "Vent", "rainwater": "OtherPipe", "fire": "FireProtectWet"}

# The fit-out's systems, as a build can be asked for them (furniture is the furniture layout's records).
SYSTEMS = ("furniture", "plumbing", "electrical", "hvac", "fire")
DEVICE_SYSTEM = {"power": "electrical", "lighting": "electrical", "data": "electrical", "hvac": "hvac",
                 "plumbing": "plumbing", "fire": "fire"}
RUN_SYSTEM = {"cold_water": "plumbing", "hot_water": "plumbing", "soil": "plumbing", "waste": "plumbing",
              "vent": "plumbing", "rainwater": "plumbing", "power": "electrical", "lighting": "electrical",
              "fire": "fire"}


def _source(*parts: str | None) -> str:
    return "; ".join(p for p in parts if p)


def device_record(d: Device) -> dict[str, Any]:
    """A device as a placed-family record (see the module docstring)."""
    rec: dict[str, Any] = {"key": d.key, "room_key": d.room_key, "level": d.level, "item": d.what, "label": d.label,
                           "category": d.category, "keywords": list(d.keywords), "point": d.insertion,
                           "rotation_deg": d.rotation_deg, "elevation_mm": d.elevation_mm, "panel": d.panel,
                           "source": _source(f"{d.label}{' ' + d.rating if d.rating else ''}: {d.source}", d.note)}
    if d.host.kind in ("wall", "outside_wall"):
        a = math.radians(d.facing_deg)
        rec["wall_key"] = d.host.wall_key
        rec["toward"] = (round(d.insertion[0] + math.cos(a) * TOWARD_MM, 1),
                         round(d.insertion[1] + math.sin(a) * TOWARD_MM, 1))
    elif d.host.kind == "ceiling":
        rec["face_down"] = True
    if d.size is not None:
        rec.update(width_mm=d.size.width_mm, depth_mm=d.size.depth_mm, height_mm=d.size.height_mm)
    return rec


def run_record(r: Run, levels: Mapping[str, float]) -> dict[str, Any]:
    """A run as create_mep_run's record: z above its level, its system a classification (pipes)."""
    if r.kind != "conduit" and r.system not in CLASSIFICATION:
        raise ValueError(f"{r.key}: no system classification for a {r.kind} of {r.system}")
    z0 = levels[r.level]
    slope = f"slope {r.slope_pct:g}%" if r.slope_pct else None
    rec: dict[str, Any] = {"key": r.key, "kind": r.kind, "level": r.level,
                           "points": [(x, y, round(z - z0, 1)) for x, y, z in r.points], "diameter_mm": r.size_mm,
                           "source": _source(f"{r.system.replace('_', ' ')} {r.kind}", slope, r.source, r.note)}
    if r.kind != "conduit":
        rec["system"] = CLASSIFICATION[r.system]
    if r.connect_start:
        rec["start_key"] = r.connect_start
    if r.connect_end:
        rec["end_key"] = r.connect_end
    return rec


def circuit_record(c: Circuit) -> dict[str, Any]:
    return {"key": c.key, "device_keys": list(c.devices), "panel_key": c.panel, "system_type": "PowerCircuit",
            "source": f"{c.kind} circuit, {c.load_w:g} W, {c.rating_a} A: {c.source}"}


def records(layout: MepLayout, levels: Mapping[str, float]) -> dict[str, list[dict[str, Any]]]:
    """The layout's devices, runs and circuits as the records ``to_plan.make`` reads (``levels``: name -> elevation,
    the building's)."""
    missing = sorted({r.level for r in layout.runs} - set(levels))
    if missing:
        raise ValueError(f"runs on levels the building does not have: {', '.join(missing)}")
    return {"devices": [device_record(d) for d in layout.devices],
            "runs": [run_record(r, levels) for r in layout.runs],
            "circuits": [circuit_record(c) for c in layout.circuits]}


def assumptions(layout: MepLayout) -> list[Assumption]:
    """What the adapter takes on itself, listed back with the layout's own assumptions."""
    rain = [r.key for r in layout.runs if r.system == "rainwater"]
    if not rain:
        return []
    return [Assumption(id="fitout:rainwater_system", text="Rainwater pipes go on a piping system of classification "
                       "OtherPipe (Revit's metric templates have no storm system; their \"Other\" system); to use "
                       "another, answer fitout:family:pipe-system-otherpipe with {\"type\": <piping system type>}",
                       value="OtherPipe", source="Plinth placeholder: Revit metric template piping systems",
                       instances=rain)]


def system_of(layout: MepLayout) -> dict[str, str]:
    """Record key -> the fit-out system it belongs to (plumbing, electrical, hvac, fire); circuits are electrical."""
    out = {d.key: DEVICE_SYSTEM[d.system] for d in layout.devices}
    out.update({r.key: RUN_SYSTEM[r.system] for r in layout.runs})
    out.update({c.key: "electrical" for c in layout.circuits})
    return out
