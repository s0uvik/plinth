"""The drawing-import pipeline (PRD Step 9, stages 2-5): sheets -> recognised building -> questions.

    python -m plinth.cad.pipeline <folder | drawings...> [--project-id ID] [--oda EXE] [--root DIR]

``run_import(project)`` reads every source sheet of an import project, classifies it (T2; a role the user answered
for a sheet wins over its title), recognises floor and roof plans (T3), sections, foundation and beam layouts (T4),
registers every sheet in one building frame using T3's features (columns, grids, wall axes, outline, section marks),
settles what one sheet leaves open and another answers (``crosssheet``: lift landing doors, founding level, window
heights a section identifies, storeys covered vs storeys shown), and turns every finding and every missing input
into grouped import questions (``questions``). With a store it saves, in the import's folder:

- ``recognised_building.json``: the building as the drawings give it (after cross-sheet resolution);
- ``building.json``: the same with every answer recorded so far applied (``answers.apply``);
- ``project.json``: the project with its questions;
- ``project-standards.json``: the build-plan converter's (T6) catalogue of types for this building.

Whatever the build-plan converter (T6) still misses is asked too, under T6's own ids, unless an import question
already asks it (``plan_bridge``).
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import get_args

from plinth.cad import answers, assumptions, classify, crosssheet, plan_bridge, questions, reader, readiness, structure
from plinth.cad import sheet as sheets
from plinth.cad.annot import flatten, name_of
from plinth.cad.building import RecognisedBuilding
from plinth.cad.project import ImportProject, ImportStore

RECOGNISED_FILE = "recognised_building.json"  # the same name the import API (plinth.imports_api) uses
APPLIED_FILE = "building.json"


def load_sheets(project: ImportProject, oda: str | None = None) -> list[sheets.Sheet]:
    out = []
    for src in project.sources:
        dxf = Path(src.dxf)
        cad = reader.read_dxf(dxf, src.path) if dxf.is_file() else reader.read(src.path, oda)
        out.append(sheets.detect(cad))
    return out


def classifications(project: ImportProject) -> dict[str, classify.Classification]:
    """Each sheet's role and storeys as recorded in the project; a role the user gave (sheet_role) wins."""
    out = {s.file: classify.Classification(role=s.role, storeys=s.storeys, name=s.name, decided_by=s.decided_by)
           for s in project.sheets}
    roles = set(get_args(classify.Role)) - {"unknown"}
    for q in project.questions:
        if q.topic == "sheet_role" and not q.open and q.evidence and q.answer in roles:
            file = q.evidence[0].sheet
            if file in out:
                out[file] = out[file].model_copy(update={"role": q.answer})
    return out


def site_texts(sheet_list: list[sheets.Sheet], cls: dict[str, classify.Classification]):
    """(sheet, handle, text) of every text on the site plans: where a location may be suggested from."""
    for sh in sheet_list:
        src = name_of(sh)
        if cls.get(src) and cls[src].role == "site_plan":
            for e in flatten(sh.drawing_entities):
                if e.text:
                    yield src, e.handle, " ".join(e.text.split())


def run_import(project: ImportProject, store: ImportStore | None = None, oda: str | None = None,
               sheet_list: list[sheets.Sheet] | None = None) -> RecognisedBuilding:
    """Recognise, register and cross-check every sheet; update the project's questions (answers kept). Returns the
    recognised building (drawings only; ``answers.apply`` gives it with the answers)."""
    sheet_list = sheet_list if sheet_list is not None else load_sheets(project, oda)
    cls = classifications(project)
    building = structure.recognise_set(sheet_list, rules=None, classifications=cls)
    crosssheet.run(building)
    suggestions = assumptions.suggest_locations(site_texts(sheet_list, cls))
    questions.update(project, building, suggestions)
    if store is not None:
        persist(store, project, building)
    return building


def folder(store: ImportStore, project: ImportProject) -> Path:
    return store.root / project.id


def persist(store: ImportStore, project: ImportProject, building: RecognisedBuilding) -> Path:
    directory = folder(store, project)
    directory.mkdir(parents=True, exist_ok=True)
    _write(directory / RECOGNISED_FILE, building)
    _write(directory / APPLIED_FILE, answers.apply(project, building))
    store.save(project)
    plan_bridge.to_plan(project, building).standards.save(directory)  # project-standards.json (T6)
    return directory


def _write(path: Path, building: RecognisedBuilding) -> None:
    temp = path.with_name(f".{path.name}.tmp")
    temp.write_text(json.dumps(building.model_dump(mode="json"), indent=1, ensure_ascii=False), encoding="utf-8")
    temp.replace(path)


def load(store: ImportStore, project: ImportProject, applied: bool = True) -> RecognisedBuilding | None:
    path = folder(store, project) / RECOGNISED_FILE
    if not path.is_file():
        return None
    b = RecognisedBuilding.model_validate_json(path.read_text(encoding="utf-8"))
    return answers.apply(project, b) if applied else b


# ---------------------------------------------------------------- report

def report(project: ImportProject, building: RecognisedBuilding) -> str:
    lines = ["SHEETS"]
    for s in project.sheets:
        lines.append(f"  {s.file}: {s.role}" + (f" storeys {s.storeys}" if s.storeys else "")
                     + (f" {s.name}" if s.name else ""))
    lines.append("RECOGNITION")
    for p in building.plans:
        kinds = [o.kind for o in p.openings]
        lines.append(f"  {p.sheet} ({p.role}, storeys {p.storeys}): {len(p.walls)} walls, {len(p.columns)} columns, "
                     f"{kinds.count('door')} doors, {kinds.count('window')} windows, {len(p.rooms)} rooms, "
                     f"{len(p.stairs)} stairs, {len(p.shafts)} shafts, {len(p.projections)} projections, "
                     f"{len(p.grids)} grids, {len(p.section_marks)} section marks, {len(p.unclaimed)} unclaimed")
    for s in building.sections:
        lines.append(f"  {s.sheet} (section {s.name}): storeys {s.storey_heights_mm}, slab {s.slab_thickness_mm}, "
                     f"beam {s.beam_depth_mm}, parapet {s.parapet_height_mm}, plinth {s.plinth_height_mm}")
    for f in building.foundations:
        lines.append(f"  {f.sheet} (foundation): {len(f.footings)} footings, {len(f.plan_columns)} columns, "
                     f"{len(f.rebar)} rebar notes")
    for bl in building.beam_layouts:
        lines.append(f"  {bl.sheet} (beam layout): {len(bl.beams)} beams, {len(bl.column_points)} column points")
    lines.append("REGISTRATION")
    for r in building.registrations:
        lines.append(f"  {r.sheet}: {r.frame} rotate {r.rotation_deg:g} move {tuple(r.translation)} residual "
                     f"{r.residual_mm:g} mm ({r.method})")
    if building.resolved:
        lines.append("SETTLED ACROSS SHEETS")
        lines += [f"  {r.id}: {r.text}" for r in building.resolved]
    lines.append("QUESTIONS")
    for sev in questions.SEVERITIES:
        qs = [q for q in project.questions if q.severity == sev]
        if not qs:
            continue
        lines.append(f"  {sev.upper()} ({len(qs)})")
        for q in qs:
            state = "" if q.open else f"  -> {q.answer} ({q.answered_by})"
            n = f" [{len(q.instances)} place{'s' if len(q.instances) != 1 else ''}]" if q.instances else ""
            lines.append(f"    {q.id}{n}: {q.text}{state}")
            if q.options:
                lines.append(f"      options: {' | '.join(q.options)}")
    lines += readiness.lines(readiness.ready_to_build(project))
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m plinth.cad.pipeline", description=__doc__.split("\n\n")[0])
    parser.add_argument("inputs", nargs="*", help="a folder of drawings, or drawing files (a new import)")
    parser.add_argument("--project-id", help="re-run an existing import project")
    parser.add_argument("--oda", help="ODAFileConverter.exe (otherwise PLINTH_ODA or the usual places)")
    parser.add_argument("--root", help="imports folder (default: %%LOCALAPPDATA%%\\Plinth\\imports)")
    args = parser.parse_args(argv)
    store = ImportStore(Path(args.root) if args.root else None)
    if args.project_id:
        project = store.get(args.project_id)
        if project is None:
            parser.error(f"no import project {args.project_id} in {store.root}")
    elif args.inputs:
        project = store.create(args.inputs, args.oda)
    else:
        parser.error("give a folder or drawings, or --project-id")
    building = run_import(project, store, args.oda)
    print(f"IMPORT {project.id} ({folder(store, project)})")
    print(report(project, building))
    return 0


if __name__ == "__main__":
    sys.exit(main())
