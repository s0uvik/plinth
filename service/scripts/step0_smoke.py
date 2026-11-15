"""Step 0 gate: from Python, one batch creates two levels and four walls in Revit, as one undo step.

Run with Revit open on a SCRATCH project (it adds two levels and four walls):
    .venv\\Scripts\\python scripts\\step0_smoke.py [--expect-title "Project1"]

Checks, in order:
  1. health reports the add-in, Revit year and the open document;
  2. a write without a confirmed document is refused and nothing runs;
  3. a batch whose last command fails rolls back completely (its level name is free again afterwards);
  4. one batch creates two levels and four walls (a 6 x 4 m room between the levels);
then asks you to press Undo once in Revit and confirm everything from step 4 disappears.
"""

from __future__ import annotations

import argparse
import sys
import time

from b2m.revit import RevitClient, RevitError

REQUIRED = ["get_current_view_info", "create_level", "create_line_based_element", "batch_execute"]


def step(text: str) -> None:
    print(f"\n== {text}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--expect-title", help="refuse to run unless the active document has this title")
    args = parser.parse_args()

    stamp = time.strftime("%H%M%S")
    lower, upper = f"B2M Smoke {stamp} A", f"B2M Smoke {stamp} B"

    with RevitClient.connect(required_commands=REQUIRED) as revit:
        step("1. health")
        h = revit.health
        print(f"add-in {h['addin_version']}, Revit {h['revit_year']}, pid {h['process_id']}, transport {h['transport']}")
        print(f"document: {h.get('document')}")
        print(f"commands: {', '.join(c['name'] for c in h['commands'])}")

        step("2. write without a confirmed document is refused")
        try:
            revit.call("create_level", {"elevation": 50000, "name": lower})
            print("FAIL: the add-in accepted a write without a confirmed document")
            return 1
        except RevitError as e:
            assert e.code == "DOCUMENT_UNCONFIRMED", e.error
            print(f"refused as expected: {e.code}")

        title = revit.confirm_document(args.expect_title)
        print(f"confirmed document: {title}")

        step("3. failing batch rolls back everything")
        try:
            revit.call("batch_execute", {"commands": [
                {"command": "create_level", "params": {"elevation": 50000, "name": lower}},
                {"command": "create_line_based_element", "params": {
                    "elementType": "wall", "level": "No Such Level", "startX": 0, "startY": 0, "endX": 1000, "endY": 0}},
            ]})
            print("FAIL: the batch should have failed")
            return 1
        except RevitError as e:
            assert e.code == "BATCH_ROLLED_BACK", e.error
            print(f"rolled back as expected: {e.error}")
            print(f"valid levels offered: {e.data['results'][1]['data']['valid_levels'][:5]}")

        step("4. one batch: two levels + four walls")
        corners = [(0, 0), (6000, 0), (6000, 4000), (0, 4000)]
        walls = [
            {"command": "create_line_based_element", "params": {
                "elementType": "wall", "level": lower, "topLevel": upper,
                "startX": a[0], "startY": a[1], "endX": b[0], "endY": b[1]}}
            for a, b in zip(corners, corners[1:] + corners[:1])
        ]
        data = revit.call("batch_execute", {"commands": [
            {"command": "create_level", "params": {"elevation": 50000, "name": lower}},
            {"command": "create_level", "params": {"elevation": 53000, "name": upper}},
            *walls,
        ]})
        for r in data["results"]:
            d = r["data"]
            if r["command"] == "create_level":
                print(f"level {d['name']} id {d['elementId']} at {d['elevationMm']} mm")
            else:
                print(f"wall id {d['elementId']} {d['typeName']} {d['startMm']} -> {d['endMm']}, height {d['heightMm']} mm to {d['topLevel']}")
        assert len(data["results"]) == 6 and not data["rolledBack"]

    step("5. undo check (manual)")
    print(f"In Revit press Ctrl+Z ONCE. The undo entry is 'B2M: Batch'; levels '{lower}', '{upper}'")
    print("and the four walls should all disappear together. That is the one-undo gate.")
    print("\nPASS: steps 1-4")
    return 0


if __name__ == "__main__":
    sys.exit(main())
