"""Step 4 gate material: for five test projects, the compliance report (PDF) and a typical floor plan (PNG)
for an architect to check by hand. The gate passes when the report misses no failure the architect finds.

    .venv\\Scripts\\python scripts\\step4_review_pack.py <out folder> [--specs 01,05,09,14,17]
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from b2m.compliance import evaluate, facts_before_build
from b2m.compliance.pdf import write_pdf
from b2m.engine import generate
from b2m.spec import BuildingSpec

SPECS = Path(__file__).resolve().parent.parent / "tests" / "golden" / "specs"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("out", type=Path)
    ap.add_argument("--specs", default="01,05,09,14,17", help="golden spec numbers: one per template, lift, ground flats")
    args = ap.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for number in args.specs.split(","):
        path = next(SPECS.glob(f"{number}-*.json"))
        spec = BuildingSpec.model_validate(json.loads(path.read_text(encoding="utf-8")))
        report = evaluate(facts_before_build(spec, generate(spec).report))
        write_pdf(report, args.out / f"{path.stem}-compliance.pdf", f"Compliance report: {path.stem}")
        subprocess.run([sys.executable, str(Path(__file__).with_name("render_floor.py")), str(path),
                        str(args.out / f"{path.stem}-floor1.png"), "--level", "1"], check=True, capture_output=True)
        (args.out / f"{path.stem}-spec.json").write_text(json.dumps(spec.model_dump(mode="json"), indent=2), encoding="utf-8")
        s = report["summary"]
        print(f"{path.stem}: {s['hard_fail']} hard, {s['soft_fail']} soft, {s['passed']} passed")
    print(f"\nReview pack in {args.out}. Rule values are provisional until the official texts are transcribed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
