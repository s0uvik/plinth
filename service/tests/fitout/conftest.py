import json

import pytest

from plinth.cad import reader, sheet, structure
from plinth.cad.recognise import recognise_plan
from plinth.fitout import rules
from plinth.imported import to_plan
from plinth.plan import BuildPlan
from tests.cad.conftest import SAMPLE, SAMPLE_DXF


@pytest.fixture(scope="session")
def sample_import():
    """The G+4 sample's building and its import plan with the test answers (as tests/cad/test_to_plan_sample.py)."""
    sheets = [sheet.detect(reader.read_dxf(p)) for p in sorted(SAMPLE_DXF.glob("*.dxf"))]
    building = structure.recognise_set(sheets, recognise_plan)
    answers = {k: v for k, v in json.loads((SAMPLE / "answers-test.json").read_text(encoding="utf-8")).items()
               if not k.startswith("_")}
    result = to_plan(building, answers)
    assert result.plan is not None
    return building, result


@pytest.fixture(scope="session")
def wb_rules():
    return rules.load("west-bengal")


def plan_of(*ops) -> BuildPlan:
    """A small plan: one level "L0" at 0 and "L1" at 3000, then the given ops (walls default to L0 -> L1)."""
    out = [{"op": "level", "key": "L0", "name": "L0", "elevation_mm": 0},
           {"op": "level", "key": "L1", "name": "L1", "elevation_mm": 3000}]
    for op in ops:
        if op["op"] == "wall":
            op = {"level": "L0", "top_level": "L1", **op}
        out.append(op)
    return BuildPlan.model_validate({"ops": out})


def box_walls(prefix: str, x0: float, y0: float, x1: float, y1: float, type_: str = "Brick 200") -> list[dict]:
    """Four walls on the centreline rectangle, anticlockwise from the bottom: S, E, N, W."""
    pts = [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]
    return [{"op": "wall", "key": f"{prefix}{n}", "type": type_, "start": pts[i], "end": pts[(i + 1) % 4]}
            for i, n in enumerate("SENW")]
