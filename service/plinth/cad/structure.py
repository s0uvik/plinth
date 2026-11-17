"""Sections, foundation, beam layouts and registration for a whole drawing set (PRD Step 9, T4).

    python -m plinth.cad.sections <folder | drawing.dwg | drawing.dxf> [...] [--oda <exe>] [--json]

Reads and classifies every sheet, recognises sections, foundation and beam-layout sheets, places every sheet in one
building frame (the typical floor plan's coordinates), ties sections to the plans through their cut lines, and
prints what was found with the findings. Plans are recognised by T3 (``plinth.cad.recognise.recognise_plan``) and
registration uses its features (columns, grids, wall axes, outline, section marks). Only a plan T3 cannot recognise
falls back to a provisional reading with just what registration needs, and a finding says so.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Callable

from plinth.cad import classify, reader, register, sheet as sheets
from plinth.cad.annot import name_of
from plinth.cad.beams import recognise_beam_layout
from plinth.cad.building import Evidence, Finding, PlanRecognition, RecognisedBuilding, SectionRecognition
from plinth.cad.foundation import recognise_foundation
from plinth.cad.sections import recognise_section_full
from plinth.rules import rule_set

PlanRecogniser = Callable[[sheets.Sheet, classify.Classification], PlanRecognition]


def load(inputs: list[str | Path], oda: str | None = None) -> list[sheets.Sheet]:
    files: list[Path] = []
    for item in map(Path, inputs):
        if item.is_dir():
            files += sorted(p for p in item.iterdir() if p.suffix.lower() in (".dwg", ".dxf"))
        else:
            files.append(item)
    return [sheets.detect(reader.read(f, oda)) for f in files]


def recognise_set(sheet_list: list[sheets.Sheet], plan_recogniser: PlanRecogniser | None = None,
                  rules: str | None = "nbc2016",
                  classifications: dict[str, classify.Classification] | None = None) -> RecognisedBuilding:
    """Every sheet recognised by its role and placed in one building frame. ``classifications`` (by sheet file
    name) overrides what the titles say, e.g. roles the user answered; ``rules`` adds clear-height findings."""
    if plan_recogniser is None:
        from plinth.cad.recognise import recognise_plan as plan_recogniser
    building = RecognisedBuilding()
    plans: list[tuple[sheets.Sheet, classify.Classification, PlanRecognition]] = []
    sections: list[tuple[SectionRecognition, float | None]] = []
    features: dict[str, tuple[register.Features, register.Features | None]] = {}
    marks_by_sheet: dict[str, list] = {}
    for sh in sheet_list:
        src = name_of(sh)
        c = (classifications or {}).get(src) or classify.classify(sh.title, src)
        if c.role in ("floor_plan", "roof_plan"):
            try:
                plan = plan_recogniser(sh, c)
            except Exception as exc:  # noqa: BLE001 - one unreadable plan must not stop the set
                plan, _ = register.provisional_plan(sh, c.role, c.storeys)
                plan.findings.append(Finding(
                    id="plan-not-recognised", topic="plan_recognition", severity="blocking",
                    text=f"Plan recognition failed on this sheet ({type(exc).__name__}: {exc}); only its columns, "
                         "centre lines and section marks were read, to place it. Its walls, openings and rooms "
                         "are not recognised.", evidence=Evidence(sheet=src)))
            plans.append((sh, c, plan))
            building.plans.append(plan)
            marks_by_sheet[src] = plan.section_marks
            features[src] = (register.plan_features(plan), None)
        elif c.role == "section":
            rec, datum_y = recognise_section_full(sh, c.name)
            sections.append((rec, datum_y))
            building.sections.append(rec)
        elif c.role == "foundation":
            rec = recognise_foundation(sh)
            building.foundations.append(rec)
            features[src] = (register.foundation_features(rec), None)
        elif c.role == "beam_layout":
            rec = recognise_beam_layout(sh, c.storeys[0] if len(c.storeys) == 1 else None)
            building.beam_layouts.append(rec)
            features[src] = (register.beam_layout_features(rec), register.beam_layout_content(rec))

    frame = _frame([(c, p) for _, c, p in plans])
    regs = {}
    if frame is None:
        building.findings.append(Finding(id="frame-none", topic="registration", severity="blocking",
                                         text="No floor plan to set the building frame by.",
                                         evidence=Evidence(sheet="")))
    else:
        regs[frame] = register.identity(frame)
        ref = features[frame][0]
        pending = [s for s in features if s != frame]
        for src in pending:
            reg, found = register.register(ref, features[src][0], features[src][1])
            if reg is None:  # try through a sheet already placed
                for other, oreg in list(regs.items()):
                    if other == frame:
                        continue
                    reg2, found2 = register.register(features[other][0], features[src][0], features[src][1])
                    if reg2 is not None:
                        reg = _compose(oreg, reg2, other)
                        found = found2
                        break
            building.findings += found
            if reg is not None:
                regs[src] = reg
    vregs, vfound = register.register_sections(sections)
    building.findings += vfound
    building.registrations = list(regs.values()) + vregs
    building.findings += register.place_cut_lines([s for s, _ in sections], marks_by_sheet, regs, frame)
    if rules:
        for s, _ in sections:
            s.findings += clear_height_findings(s, rules)
    return building


def _frame(plans: list[tuple[classify.Classification, PlanRecognition]]) -> str | None:
    floors = [(c, p) for c, p in plans if c.role == "floor_plan"]
    if not floors:
        return None
    typical = max(floors, key=lambda cp: (len(cp[0].storeys), -min(cp[0].storeys or [99])))
    if len(typical[0].storeys) <= 1:
        typical = min(floors, key=lambda cp: min(cp[0].storeys or [99]))
    return typical[1].sheet


def _compose(outer, inner, via: str):
    """inner maps sheet -> via's coordinates; outer maps via -> frame."""
    t_out, t_in = register.transform_of(outer), register.transform_of(inner)
    origin = t_out.apply(t_in.apply((0.0, 0.0)))
    reg = inner.model_copy(deep=True)
    reg.rotation_deg = round(outer.rotation_deg + inner.rotation_deg, 6)
    reg.translation = (round(origin[0], 3), round(origin[1], 3))
    reg.method = f"{inner.method}, then {outer.method} (through {via})"
    reg.residual_mm = round(outer.residual_mm + inner.residual_mm, 3)
    return reg


def clear_height_findings(section: SectionRecognition, rules: str = "nbc2016") -> list[Finding]:
    """Storey heights against the rule set's clear-height rules (floor to floor less the slab): a finding, never a
    change to what the drawing says."""
    out = []
    t = section.slab_thickness_mm
    if not section.storey_heights_mm or t is None:
        return out
    for rule in rule_set(rules).get("rules", []):
        if rule.get("type") != "clear_height":
            continue
        low = [(i, h) for i, h in enumerate(section.storey_heights_mm) if h - t < rule["min_mm"]]
        if not low:
            continue
        heights = sorted({h for _, h in low})
        out.append(Finding(
            id=f"{rules}-{rule['id']}", topic="rule", severity="check",
            text=f"{rule['title']} ({rules}, {rule['clause']}): at least {rule['min_mm']} mm for "
                 f"{', '.join(rule['kinds'])}. The section's storeys are {', '.join(f'{h:g}' for h in heights)} mm "
                 f"floor to floor with a {t:g} mm slab, so at most {max(heights) - t:g} mm clear (less under beams). "
                 "Which rule set applies depends on the project's location.",
            evidence=Evidence(sheet=section.sheet, note=f"storeys {', '.join(str(i) for i, _ in low)}"),
            options=["the drawing stands (a relaxation or another rule set applies)",
                     "storey height to be raised", "rooms of these kinds are not on these storeys"]))
    return out


# ---------------------------------------------------------------- report

def report(b: RecognisedBuilding) -> str:
    lines = []
    for s in b.sections:
        lines.append(f"SECTION {s.name} ({s.sheet}) datum {s.datum}")
        lines.append(f"  storeys {s.storey_heights_mm} | slab {s.slab_thickness_mm} | beam {s.beam_depth_mm} | "
                     f"parapet {s.parapet_height_mm} | plinth {s.plinth_height_mm}")
        lines.append(f"  floor build-up {s.foundation_layers} | doors {s.door_heights_mm} | window sills "
                     f"{s.window_sills_mm} heads {s.window_heads_mm}")
        for lv in s.levels:
            lines.append(f"  level {lv.elevation_mm:>9g}  {lv.kind:<11} {lv.name}"
                         + (f" (storey {lv.storey})" if lv.storey is not None else ""))
        if s.cut_path:
            lines.append(f"  cut {'line' if s.cut_line else 'path'} {s.cut_path} looking {s.view_direction} "
                         f"(in {s.cut_sheet})")
        lines += _findings(s.findings)
        for e in s.unexplained:
            lines.append(f"  unexplained {' '.join(e.handles)}: {e.note}")
    for f in b.foundations:
        lines.append(f"FOUNDATION ({f.sheet})")
        for fo in f.footings:
            lines.append(f"  footing {fo.id} {fo.kind} {fo.width_mm:g} x {fo.length_mm:g} at {fo.center}, depth "
                         f"{fo.depth_mm}, founding {fo.founding_depth_mm} below GL")
        lines.append(f"  {len(f.plan_columns)} columns on the footing plan")
        for c in f.column_sections:
            lines.append(f"  column section {c.id} {c.width_mm:g} x {c.depth_mm:g} ({c.evidence.note})")
        for r in f.rebar:
            parts = [f"dia {r.bar_dia_mm:g}" if r.bar_dia_mm else "", f"@{r.spacing_mm:g}" if r.spacing_mm else "",
                     f"{r.count} bars" if r.count else "", f"{r.legs} legs" if r.legs else "",
                     f"cover {r.cover_mm:g}" if r.cover_mm else ""]
            lines.append(f"  rebar {r.member}: {r.role or '-'} {' '.join(p for p in parts if p)}  \"{r.text}\"")
        lines += _findings(f.findings)
    for bl in b.beam_layouts:
        widths = sorted({bm.width_mm for bm in bl.beams})
        lines.append(f"BEAM LAYOUT ({bl.sheet}) storey {bl.storey}: {len(bl.beams)} beams, widths {widths}, "
                     f"{len(bl.grids)} grids, {len(bl.column_points)} column points")
        lines += _findings(bl.findings)
    lines.append("REGISTRATION")
    for r in b.registrations:
        lines.append(f"  {r.sheet}: {r.frame} rotate {r.rotation_deg:g} move {r.translation} residual "
                     f"{r.residual_mm:g} mm ({r.method})")
    lines += _findings(b.findings)
    return "\n".join(lines)


def _findings(findings: list[Finding]) -> list[str]:
    return [f"  [{f.severity}] {f.topic}: {f.text}" + (f" Options: {' | '.join(f.options)}" if f.options else "")
            for f in findings]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m plinth.cad.sections", description=__doc__.split("\n\n")[0])
    parser.add_argument("inputs", nargs="+", help="a folder of drawings, or drawing files")
    parser.add_argument("--oda", help="ODAFileConverter.exe (otherwise PLINTH_ODA or the usual places)")
    parser.add_argument("--json", action="store_true", help="print the recognised building as JSON")
    args = parser.parse_args(argv)
    building = recognise_set(load(args.inputs, args.oda))
    if args.json:
        print(json.dumps(building.model_dump(mode="json"), indent=2, ensure_ascii=False))
    else:
        print(report(building))
    return 0


if __name__ == "__main__":
    sys.exit(main())
