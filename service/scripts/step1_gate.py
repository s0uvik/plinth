"""Step 1 gate: a hand-written build plan for a two-storey test house builds with one undo per batch,
and "remove generated model" deletes everything it created.

Run with Revit open on a SCRATCH project:
    .venv\\Scripts\\python scripts\\step1_gate.py --title "<scratch project title>" [--keep]
"""

from __future__ import annotations

import argparse
import json
import sys
import time

from plinth import logs
from plinth.builds import BuildService
from plinth.examples import two_storey_house
from plinth.revit import RevitClient
from plinth.revit.types import FamilyType, TypeMap
from plinth.runs import RunStore


def first_type(revit: RevitClient, category: str) -> FamilyType:
    """The first loaded type with its family, so a type name shared by several families is not ambiguous."""
    data = revit.call("get_available_family_types", {"builtInCategory": category})
    if not data["totalTypes"]:
        sys.exit(f"The project has no {category} types loaded.")
    first = data["categories"][0]["types"][0]
    return FamilyType(first["typeName"], first["familyName"])


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--title", required=True, help="the scratch project's title; nothing is built in any other document")
    parser.add_argument("--keep", action="store_true", help="leave the house in the model instead of removing it")
    args = parser.parse_args()
    logs.configure()

    with RevitClient.connect() as revit:
        door, window = first_type(revit, "OST_Doors"), first_type(revit, "OST_Windows")
    print(f"door type: {door.family}: {door.type}\nwindow type: {window.family}: {window.type}")

    plan = two_storey_house("Test door", "Test window", origin=(150000, 0), prefix=f"TH{time.strftime('%H%M%S')}")
    service = BuildService(RunStore(), types=TypeMap(doors={"Test door": door}, windows={"Test window": window}))
    started = time.monotonic()
    record = service.build_now(plan, args.title)
    print(f"\nrun {record['run_id']}: {record['status']} in {time.monotonic() - started:.1f} s")
    print(f"batches {record['batches']['succeeded']} of {record['batches']['total']}, {len(record.get('created') or {})} elements")
    if record["status"] not in ("built",):
        print(json.dumps({k: record.get(k) for k in ("error", "failed_key", "error_data", "failures", "readback")}, indent=2))
        if record.get("created") and not args.keep:
            print("removing what was built:", service.remove(record["run_id"])["status"])
        return 1
    print("read-back:", json.dumps({k: record["readback"][k] for k in ("ok", "elements", "warnings_total", "mismatches")}))

    if args.keep:
        print("\n--keep: the house stays. Each batch is one undo step named 'Plinth: Batch' in Revit's undo list.")
        return 0

    removed = service.remove(record["run_id"])
    print(f"\nremove: {removed['status']}, deleted {removed['removal']['deleted']}, also deleted {len(removed['removal']['alsoDeleted'])}")
    with RevitClient.connect() as revit:
        left = revit.call("analyze_model_statistics", {"elementIds": list(record["created"].values())})
    gone = len(left["missingIds"]) == len(record["created"])
    print("every created element is gone:", gone)
    print("\nPASS" if gone and removed["status"] == "removed" else "\nFAIL")
    return 0 if gone else 1


if __name__ == "__main__":
    sys.exit(main())
