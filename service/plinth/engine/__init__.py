"""Layout engine (PRD F4.1-F4.3): building spec to build plan.

    result = generate(spec)          # raises LayoutError with every reason if nothing fits
    result.plan                      # BuildPlan for the runner
    result.report                    # template, envelope, setbacks, areas per flat and room, notes

Deterministic: integer millimetres, fixed iteration orders, no randomness, so the same spec always gives
the same plan (F4.2). Layout variants are tried in a fixed order until one validates (F4.3).
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from plinth.engine import catalog, structure
from plinth.engine.emit import Extras, emit, level_names
from plinth.engine.floors import BuiltFloor, build_floor, ventilated_area_mm2
from plinth.engine.geometry import net_area_mm2, net_rect
from plinth.engine.site import Envelope, LayoutError, envelope
from plinth.engine.stair import design_stair
from plinth.engine.templates import PlateLayout, plan_plate
from plinth.engine.units import SEQUENCES
from plinth.engine.validate import validate_floor
from plinth.plan import BuildPlan
from plinth.spec import BuildingSpec

ENGINE_VERSION = "0.2.0"  # 0.2: RCC frame, stairs and railings, chajjas, roof parapet
VARIANTS = max(len(v) for v in SEQUENCES.values())

__all__ = ["ENGINE_VERSION", "EngineResult", "LayoutError", "generate"]


@dataclass(frozen=True)
class EngineResult:
    plan: BuildPlan
    report: dict[str, Any]


def generate(spec: BuildingSpec, name_prefix: str = "") -> EngineResult:
    env = envelope(spec)
    reasons: list[str] = []
    for variant in range(VARIANTS):
        try:
            plate = plan_plate(spec, env, variant)
        except LayoutError as e:
            reasons.append(f"variant {variant + 1}: {e}")
            continue
        floors = [build_floor(plate, level) for level in range(spec.building.upper_floors + 1)]
        slab = catalog.SLAB_THICKNESS[spec.standards.slab]
        problems = [p for f in floors for p in validate_floor(plate, f, spec.building.floor_to_floor_mm, slab)]
        rcc = spec.standards.structure == "rcc_frame"
        head = structure.opening_heads(floors)
        if rcc and head > spec.building.floor_to_floor_mm - structure.BEAM_DEPTH:
            problems.append(f"openings reach {head} mm above the floor; the {structure.BEAM_DEPTH} mm beams start at "
                            f"{spec.building.floor_to_floor_mm - structure.BEAM_DEPTH} mm")
        if problems:
            reasons.append(f"variant {variant + 1}: " + "; ".join(problems[:5]) + (" ..." if len(problems) > 5 else ""))
            continue
        # The stair zone does not depend on the variant: a stair that does not fit is reported straight away.
        stair = design_stair(plate.core.stair, spec.building.floor_to_floor_mm, spec.core.stair_width_mm)
        rcc_frame = structure.frame(plate, floors[1], floors) if rcc else None
        extras = Extras(rcc_frame, stair, spec.standards.chajja_mm, spec.standards.roof_parapet_mm)
        plan = emit(plate, floors, spec.building.floor_to_floor_mm,
                    (spec.standards.external_wall, spec.standards.internal_wall), spec.standards.slab,
                    name_prefix, spec.hash(), extras)
        return EngineResult(plan, _report(spec, env, plate, floors, reasons, name_prefix, extras))
    raise LayoutError("No layout fits this spec. " + " | ".join(reasons))


def _report(spec: BuildingSpec, env: Envelope, plate: PlateLayout, floors: list[BuiltFloor], tried: list[str],
            prefix: str, extras: Extras) -> dict[str, Any]:
    p = plate.plate
    outer_w, outer_d = p.width + 230, p.height + 230
    footprint = outer_w * outer_d / 1e6
    plot_w, plot_d = spec.site.plot_mm
    plot_area = plot_w * plot_d / 1e6
    typical = floors[1]
    notes = list(plate.notes)
    flats = []
    glazing: dict[str, float] = {}
    for w in typical.windows:
        glazing[w.spaces[0]] = glazing.get(w.spaces[0], 0) + w.type.width * w.type.height / 1e6
    for flat in typical.layout.flats:
        rooms = []
        for s in flat.spaces:
            r = net_rect(s, s.anchor, typical.edges, typical.thickness)
            rooms.append({"name": s.name, "kind": s.kind, "net_width_mm": min(r.width, r.height),
                          "net_length_mm": max(r.width, r.height),
                          "net_area_m2": round(net_area_mm2(s, typical.edges, typical.thickness) / 1e6, 2),
                          "window_m2": round(glazing.get(s.id, 0.0), 2),
                          "ventilated_area_m2": round(ventilated_area_mm2(s, typical) / 1e6, 2)})
        flats.append({"letter": flat.letter, "type": flat.type, "target_carpet_m2": plate.targets[flat.type],
                      "carpet_m2": round(sum(r["net_area_m2"] for r in rooms), 2),
                      "width_mm": flat.w, "depth_mm": flat.d, "rooms": rooms})
    for f in flats:
        if f["carpet_m2"] > f["target_carpet_m2"] * 1.15:
            notes.append(f"Flat {f['letter']} ({f['type']}) has {f['carpet_m2']:.0f} m² carpet against a "
                         f"{f['target_carpet_m2']:.0f} m² target: room minimums and the shared plate depth set its size.")
    floors_with_flats = spec.building.upper_floors + (1 if spec.building.ground == "flats" else 0)
    if not env.setbacks.verified:
        notes.append("Setbacks come from a provisional table that has not been checked against the official rules.")
    notes.append("Lift landing doors are not modelled. The stair runs from the ground floor to the top floor; "
                 "roof access (a stair cover) is not modelled.")
    if extras.frame is not None:
        notes += extras.frame.notes
    sc = plate.core.stair_core
    stair = extras.stair
    return {
        "engine_version": ENGINE_VERSION,
        "spec_hash": spec.hash(),
        "template": plate.template,
        "variant": plate.variant + 1,
        "variants_rejected": tried,
        "levels": level_names(spec.building.upper_floors, prefix),
        "height_mm": env.height,
        "envelope_mm": {"x0": env.x0, "y0": env.y0, "x1": env.x1, "y1": env.y1},
        "setbacks_mm": {"front": env.setbacks.front, "rear": env.setbacks.rear, "side_left": env.setbacks.side_left,
                        "side_right": env.setbacks.side_right, "verified": env.setbacks.verified, "source": env.setbacks.source},
        "plate_mm": {"x0": p.x0, "y0": p.y0, "x1": p.x1, "y1": p.y1},
        "footprint_m2": round(footprint, 2),
        "plot_area_m2": round(plot_area, 2),
        "ground_coverage": round(footprint / plot_area, 4),
        "far": round(footprint * floors_with_flats / plot_area, 3),
        "stair": {"width_mm": sc.width, "risers": sc.risers, "riser_mm": round(sc.riser, 1), "tread_mm": stair.tread,
                  "inner_width_mm": sc.inner_width, "inner_depth_mm": sc.inner_depth,
                  "risers_per_flight": sc.risers_per_flight, "flight_going_mm": sc.flight_length,
                  "mid_landing_depth_mm": stair.landing_depth, "storeys": spec.building.upper_floors,
                  "railing": structure.RAILING},
        "structure": _structure(extras),
        "lift": spec.core.lift,
        "flats_per_floor": flats,
        "notes": notes,
    }


def _structure(extras: Extras) -> dict[str, Any]:
    f = extras.frame
    out: dict[str, Any] = {"system": "load_bearing" if f is None else "rcc_frame",
                           "chajja_mm": extras.chajja_mm, "roof_parapet_mm": extras.parapet_mm}
    if f is not None:
        out.update(column_type=structure.COLUMN, beam_type=structure.BEAM, columns_per_storey=len(f.columns),
                   beams_per_floor=len(f.beams), longest_beam_mm=max(abs(b[0] - a[0]) + abs(b[1] - a[1]) for a, b in f.beams))
    return out
