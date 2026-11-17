"""Print what Plinth reads from a drawing set, without saving anything:

    python -m plinth.cad.inspect <folder | drawing.dwg | drawing.dxf> [...] [--oda <ODAFileConverter.exe>] [--json]

Per sheet: role, storeys, title, scale, page, drawing entity counts by kind and layer; then the open questions.
"""

from __future__ import annotations

import argparse
import json
import sys

from plinth.cad import project as projects


def report(project: projects.ImportProject) -> str:
    lines = []
    for s in project.sheets:
        storeys = ", ".join(map(str, s.storeys)) or "-"
        scale = f"1:{s.scale:g}" if s.scale else "-"
        lines += [
            f"{s.file}",
            f"  role {s.role}{' ' + s.name if s.name else ''} | storeys {storeys} | decided by {s.decided_by or '-'}",
            f"  title {s.title or '-'} | scale {scale} | page {s.page or '-'} | units {s.units}",
            f"  entities {_counts(s.entities)} | exploded {s.exploded}",
            f"  layers {_counts(s.layers)}",
            f"  excluded {_counts(s.excluded)}",
        ]
    questions = project.open_questions()
    lines.append(f"Open questions: {len(questions)}")
    for q in questions:
        where = "; ".join(f"{e.sheet} {' '.join(e.handles)}".strip() for e in q.evidence)
        lines.append(f"  {q.id} [{q.topic}] {q.text}" + (f" ({where})" if where else ""))
    return "\n".join(lines)


def _counts(counts: dict[str, int]) -> str:
    return ", ".join(f"{k} {v}" for k, v in counts.items()) or "-"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="python -m plinth.cad.inspect", description=__doc__.split("\n\n")[0])
    parser.add_argument("inputs", nargs="+", help="a folder of drawings, or drawing files")
    parser.add_argument("--oda", help="ODAFileConverter.exe (otherwise PLINTH_ODA or the usual places)")
    parser.add_argument("--json", action="store_true", help="print the import project as JSON")
    args = parser.parse_args(argv)
    project = projects.analyse(args.inputs, args.oda)
    if args.json:
        print(json.dumps(project.model_dump(mode="json"), indent=2, ensure_ascii=False))
    else:
        print(report(project))
    return 0


if __name__ == "__main__":
    sys.exit(main())
