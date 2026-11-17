"""Regenerate the stored golden plans after an intended engine change. Review the diff before committing.

    .venv\\Scripts\\python scripts\\update_golden.py
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from plinth.engine import generate
from plinth.spec import BuildingSpec

GOLDEN = Path(__file__).resolve().parent.parent / "tests" / "golden"


def plan_json(spec_path: Path) -> str:
    """The plan as valid JSON with one op per line, so a review diff shows exactly which ops changed."""
    spec = BuildingSpec.model_validate(json.loads(spec_path.read_text(encoding="utf-8")))
    data = generate(spec).plan.model_dump(mode="json")
    ops = data.pop("ops")
    head = json.dumps(data, sort_keys=True)[:-1]
    body = ",\n".join(json.dumps(op, sort_keys=True, separators=(",", ":")) for op in ops)
    return head + ', "ops": [\n' + body + "\n]}\n"


def main() -> int:
    plans = GOLDEN / "plans"
    plans.mkdir(exist_ok=True)
    for spec_path in sorted((GOLDEN / "specs").glob("*.json")):
        target = plans / spec_path.name.replace(".json", ".plan.json")
        text = plan_json(spec_path)
        changed = not target.exists() or target.read_text(encoding="utf-8") != text
        target.write_text(text, encoding="utf-8")
        print(("updated " if changed else "same    ") + target.name)
    return 0


if __name__ == "__main__":
    sys.exit(main())
