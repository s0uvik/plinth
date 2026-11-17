"""Golden tests (PRD: Testing): 20 specs covering every template and unit type. Each must produce exactly
its stored plan; any change to a plan must be reviewed and re-recorded with scripts/update_golden.py."""

import json
from pathlib import Path

import pytest

from plinth.engine import generate
from plinth.spec import BuildingSpec
from scripts.update_golden import plan_json
from tests.engine.test_engine import assert_plan_sound

GOLDEN = Path(__file__).parent / "golden"
SPECS = sorted((GOLDEN / "specs").glob("*.json"))


def test_there_are_twenty_golden_specs_covering_every_template_and_unit_type():
    assert len(SPECS) == 20
    seen = set()
    for path in SPECS:
        spec = BuildingSpec.model_validate(json.loads(path.read_text(encoding="utf-8")))
        template = generate(spec).report["template"]
        seen |= {(template, u.type) for u in spec.units}
    assert seen == {(t, u) for t in ("T2", "T3", "T4") for u in ("1BHK", "2BHK", "3BHK")}


@pytest.mark.parametrize("spec_path", SPECS, ids=[p.stem for p in SPECS])
def test_plan_matches_the_stored_golden_plan(spec_path):
    stored = (GOLDEN / "plans" / spec_path.name.replace(".json", ".plan.json")).read_text(encoding="utf-8")
    assert plan_json(spec_path) == stored, "plan changed: review it, then run scripts/update_golden.py"


@pytest.mark.parametrize("spec_path", SPECS, ids=[p.stem for p in SPECS])
def test_golden_plans_are_sound(spec_path):
    spec = BuildingSpec.model_validate(json.loads(spec_path.read_text(encoding="utf-8")))
    assert_plan_sound(generate(spec).plan, spec)
