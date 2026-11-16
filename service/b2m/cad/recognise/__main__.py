"""Print what plan recognition finds on floor and roof plans:

    python -m b2m.cad.recognise <drawing.dxf | drawing.dwg> [...] [--svg out_dir] [--json out_dir] [--oda exe]

Per sheet: counts per kind, wall thickness classes, marks found (texts vs marked openings), findings and the
unclaimed geometry. --svg writes an overlay (source linework + what was recognised) per sheet; --json the
PlanRecognition. Sheets that are not floor or roof plans are skipped with their role.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path

from b2m.cad import classify, overlay, reader, sheet as sheets
from b2m.cad.recognise.plan import recognise_plan_detailed


def describe(rec, report) -> str:
    kinds = Counter(w.kind for w in rec.walls)
    thick = Counter(w.thickness_mm for w in rec.walls)
    ops = Counter(o.kind for o in rec.openings)
    lines = [
        f"{rec.sheet}  ({rec.role}{', storeys ' + ', '.join(map(str, rec.storeys)) if rec.storeys else ''})",
        f"  walls {len(rec.walls)} ({_c(kinds)}) | thickness of walls {_c(thick, ' mm')}",
        "  thickness classes found: " + (", ".join(f"{c:g} mm ({s:.0%})" for c, s in report.thickness_classes) or "-"),
        f"  columns {len(rec.columns)} | openings {len(rec.openings)} ({_c(ops)}) | rooms {len(rec.rooms)} "
        f"({sum(1 for r in rec.rooms if r.boundary)} enclosed) | units {report.units}",
        f"  stairs {len(rec.stairs)} | shafts {len(rec.shafts)} ({_c(Counter(s.kind for s in rec.shafts))}) | "
        f"projections {len(rec.projections)} ({_c(Counter(p.kind for p in rec.projections))}) | grids {len(rec.grids)}"
        f" | section marks {len(rec.section_marks)} | falls {len(rec.falls)}",
        f"  outline {'closed, ' + str(len(rec.outline)) + ' corners' if rec.outline else '-'}",
        f"  mark texts {_c(Counter(report.marks.get('texts', {})))}",
        f"  marked openings {_c(Counter(report.marks.get('marked', {})))} | unmarked {_c(Counter(report.marks.get('unmarked', {})))}",
        f"  findings {len(rec.findings)} ({_c(Counter(f.topic for f in rec.findings))})",
    ]
    for f in rec.findings:
        lines.append(f"    {f.id} [{f.severity}] {f.text}  <{' '.join(f.evidence.handles[:6])}>")
    lines.append(f"  unclaimed {len(rec.unclaimed)}: {_c(Counter(report.unclaimed_by_note))}")
    return "\n".join(lines)


def _c(counter, suffix: str = "") -> str:
    return ", ".join(f"{k}{suffix} {v}" for k, v in sorted(counter.items(), key=lambda kv: str(kv[0]))) or "-"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m b2m.cad.recognise", description=__doc__.split("\n\n")[0])
    parser.add_argument("inputs", nargs="+", help="drawing files (DXF, or DWG with the ODA converter)")
    parser.add_argument("--svg", help="write an overlay SVG per sheet into this folder")
    parser.add_argument("--json", help="write the PlanRecognition JSON per sheet into this folder")
    parser.add_argument("--oda", help="ODAFileConverter.exe for DWG input")
    args = parser.parse_args(argv)
    for path in args.inputs:
        cad = reader.read(path, args.oda) if Path(path).suffix.lower() == ".dwg" else reader.read_dxf(path)
        sh = sheets.detect(cad)
        cls = classify.classify(sh.title, path)
        if cls.role not in ("floor_plan", "roof_plan"):
            print(f"{Path(path).name}: {cls.role}, skipped")
            continue
        rec, report = recognise_plan_detailed(sh, cls)
        print(describe(rec, report))
        stem = Path(path).stem
        if args.svg:
            Path(args.svg).mkdir(parents=True, exist_ok=True)
            (Path(args.svg) / f"{stem}.svg").write_text(overlay.render(sh.drawing_entities, rec), encoding="utf-8")
        if args.json:
            Path(args.json).mkdir(parents=True, exist_ok=True)
            (Path(args.json) / f"{stem}.json").write_text(json.dumps(rec.model_dump(mode="json"), indent=1),
                                                          encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
