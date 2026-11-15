"""Step 2 gate: all 20 golden specs build in Revit with zero warnings, and the same spec always gives
the same plan.

Each golden building is built on its own levels (prefixed "Gnn "), moved clear of the existing model,
read back (exact counts and warnings on the built elements), then removed before the next one.
Run with Revit open on a SCRATCH project:

    .venv\\Scripts\\python scripts\\step2_gate.py --title "<scratch project title>" [--types standards.json] [--only 01,14] [--keep]

A project without the B2M template types needs a standards file mapping the logical types
(Brick 230, Brick 115, RCC 150, the door and window names in b2m/engine/catalog.py) to its own.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

from b2m import logs
from b2m.builds import BuildService
from b2m.engine import generate
from b2m.plan import translate
from b2m.revit.types import TypeMap
from b2m.runs import RunStore
from b2m.spec import BuildingSpec

SPECS = Path(__file__).resolve().parent.parent / "tests" / "golden" / "specs"
OFFSET = (300000, 0)  # mm: well clear of a typical existing model


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--title", required=True, help="scratch project title; nothing is built in any other document")
    ap.add_argument("--types", type=Path, help="standards file mapping logical types to this project's types")
    ap.add_argument("--only", help="comma-separated spec numbers, e.g. 01,14")
    ap.add_argument("--keep", action="store_true", help="do not remove the buildings")
    args = ap.parse_args()
    logs.configure()

    service = BuildService(RunStore(), types=TypeMap.load(args.types))
    wanted = set(args.only.split(",")) if args.only else None
    rows, ok_all = [], True
    for path in sorted(SPECS.glob("*.json")):
        number = path.name[:2]
        if wanted and number not in wanted:
            continue
        spec = BuildingSpec.model_validate(json.loads(path.read_text(encoding="utf-8")))
        first = generate(spec, name_prefix=f"G{number} ")
        deterministic = first.plan.model_dump() == generate(spec, name_prefix=f"G{number} ").plan.model_dump()
        plan = translate(first.plan, *OFFSET)

        started = time.monotonic()
        record = service.build_now(plan, args.title)
        seconds = time.monotonic() - started
        readback = record.get("readback") or {}
        built = record["status"] == "built"
        removed = "kept"
        if record.get("created") and not args.keep:
            removed = service.remove(record["run_id"])["status"]
        ok = built and deterministic and readback.get("warnings_total") == 0 and (args.keep or removed == "removed")
        ok_all &= ok
        rows.append((path.stem, record["status"], readback.get("elements"), readback.get("warnings_total"),
                     f"{seconds:.0f}s", removed, "PASS" if ok else "FAIL"))
        print(" | ".join(str(c) for c in rows[-1]), flush=True)
        if not built:
            print("   ", record.get("error"), "at", record.get("failed_key"))
            for f in (record.get("failures") or [])[:3]:
                print("    failure:", f.get("severity"), f.get("description"))
        elif readback.get("warnings_total"):
            for w in readback["warnings"][:3]:
                print("    warning:", w["description"], w["count"])

    print(f"\n{sum(r[-1] == 'PASS' for r in rows)} of {len(rows)} passed")
    return 0 if ok_all else 1


if __name__ == "__main__":
    sys.exit(main())
