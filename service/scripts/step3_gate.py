"""Step 3 gate: 18 of 20 test briefs give a valid spec first time; all 20 after one round of questions.

Calls Claude (about 25 requests on Claude Sonnet 5.5), so it needs ANTHROPIC_API_KEY or an `ant auth login`
profile, and it costs a little. A spec counts only if it matches the expected one (plot within 50 mm, upper
floors, flats of each type per floor, ground storey, lift), so a valid but wrong spec fails.

    .venv\\Scripts\\python scripts\\step3_gate.py [--only b01,b17]
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import yaml

from plinth.brief.agent import BriefSession, Questions, SpecReady, anthropic_client
from plinth.engine import LayoutError, generate

BRIEFS = Path(__file__).resolve().parent.parent / "tests" / "briefs" / "briefs.yaml"


def matches(spec, expect: dict) -> list[str]:
    problems = []
    width, depth = spec.site.plot_mm
    if abs(width - expect["plot"][0]) > 50 or abs(depth - expect["plot"][1]) > 50:
        problems.append(f"plot {width}x{depth}, expected {expect['plot'][0]}x{expect['plot'][1]}")
    if spec.building.upper_floors != expect["upper_floors"]:
        problems.append(f"upper floors {spec.building.upper_floors}, expected {expect['upper_floors']}")
    if spec.building.ground != expect["ground"]:
        problems.append(f"ground {spec.building.ground}, expected {expect['ground']}")
    if spec.core.lift != expect["lift"]:
        problems.append(f"lift {spec.core.lift}, expected {expect['lift']}")
    got = {u.type: u.per_floor for u in spec.units}
    if got != expect["units"]:
        problems.append(f"units {got}, expected {expect['units']}")
    return problems


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--only")
    args = ap.parse_args()
    briefs = yaml.safe_load(BRIEFS.read_text(encoding="utf-8"))
    if args.only:
        briefs = [b for b in briefs if b["id"] in args.only.split(",")]
    client = anthropic_client()

    first_time = after_round = 0
    for b in briefs:
        session = BriefSession.start(client, b["text"])
        outcome, asked, note = session.outcome, False, ""
        if isinstance(outcome, SpecReady) and not matches(outcome.spec, b["expect"]):
            first_time += 1
        if isinstance(outcome, Questions):
            asked = True
            answers = b.get("answers") or {"note": "Please use your best judgement within what the engine builds."}
            outcome = session.answer(answers)
        if isinstance(outcome, SpecReady):
            problems = matches(outcome.spec, b["expect"])
            if not problems:
                after_round += 1
                try:
                    note = f"layout {generate(outcome.spec).report['template']}"
                except LayoutError:
                    note = "valid spec, but no layout fits"
            else:
                note = "; ".join(problems)
        else:
            note = "; ".join(getattr(outcome, "errors", ["still asking after one round"]))
        should_ask = "answers" in b
        flag = "" if asked == should_ask else ("  <- asked unnecessarily" if asked else "  <- did not ask for a missing essential")
        print(f"{b['id']} {'asked' if asked else 'direct'}: {note}{flag}", flush=True)

    total = len(briefs)
    print(f"\nvalid first time: {first_time} of {total} (gate: 18 of 20)")
    print(f"valid after one round: {after_round} of {total} (gate: all)")
    passed = first_time >= total - 2 and after_round == total
    print("PASS" if passed else "FAIL")
    return 0 if passed else 1


if __name__ == "__main__":
    sys.exit(main())
