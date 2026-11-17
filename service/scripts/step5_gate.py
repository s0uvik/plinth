"""Step 5 gate: one click produces the eight-sheet PDF in under 2 minutes.

Builds golden spec 01 (G+3) in a SCRATCH project, makes its drawing set, checks the PDF and the time,
then removes everything (pass --keep to look at it in Revit first).

    .venv\\Scripts\\python scripts\\step5_gate.py --title "<scratch project title>" [--types standards.json] [--keep]
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

from pypdf import PdfReader

from plinth import logs
from plinth.builds import BuildService
from plinth.drawings import build_drawing_set
from plinth.engine import generate
from plinth.plan import BuildPlan, translate
from plinth.revit.types import TypeMap
from plinth.runs import RunStore
from plinth.spec import BuildingSpec

SPEC = Path(__file__).resolve().parent.parent / "tests" / "golden" / "specs" / "01-t2-2bhk-stilt-g3.json"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--title", required=True)
    ap.add_argument("--types", type=Path)
    ap.add_argument("--keep", action="store_true")
    args = ap.parse_args()
    logs.configure()
    service = BuildService(RunStore(), types=TypeMap.load(args.types))
    spec = BuildingSpec.model_validate(json.loads(SPEC.read_text(encoding="utf-8")))
    plan = translate(generate(spec, name_prefix="D01 ").plan, 300000, 0)
    record = service.build_now(plan, args.title)
    print("build:", record["status"], record.get("error") or "")
    if record["status"] not in ("built", "built_with_issues"):
        return 1

    def make(revit, run):
        def remember(key, element_id):
            run.update(created={**(run.load("run").get("created") or {}), key: element_id})
        return build_drawing_set(revit, BuildPlan.model_validate(run.load("plan")), run.dir, None, prefix="D01 ", record=remember)

    started = time.monotonic()
    result = service.run_on(record["run_id"], make, "drawings")["drawings"]
    seconds = time.monotonic() - started
    if "error" in result:
        print("drawings failed:", result["error"])
        ok = False
    else:
        pages = len(PdfReader(result["set_pdf"]).pages)
        print(f"drawing set: {len(result['sheets'])} sheets, {pages} PDF pages, {seconds:.0f} s -> {result['set_pdf']}")
        ok = len(result["sheets"]) == 8 and pages >= 8 and seconds < 120
    if not args.keep:
        print("remove:", service.remove(record["run_id"])["status"])
    print("PASS" if ok else "FAIL")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
