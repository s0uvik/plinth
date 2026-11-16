"""Stair railings in the imported project's estimate come from the built model (b2m.imported.estimate): the railings
the run made are read back with their path lengths and measured; only railings nothing measured are asked.

A one-stair plan stands in for the import: the stair is built by create_stair, which (as Revit does) comes back with
two railings of its own (run keys S#2, S#3), so the plan's railing op does not run.
"""

from __future__ import annotations

from types import SimpleNamespace

from b2m.cad.building import RecognisedBuilding
from b2m.cad.project import ImportProject, SheetRecord
from b2m.estimate import load_rules
from b2m.imported import estimate
from b2m.imported.standards import ProjectStandards
from b2m.imported.to_plan import ImportPlan, Source
from b2m.plan import BuildPlan

SHEET = "1.GROUND FLOOR PLAN.dxf"
PLAN = ImportPlan(
    plan=BuildPlan(ops=[
        {"op": "level", "key": "L0", "name": "Ground Floor", "elevation_mm": 0},
        {"op": "level", "key": "L1", "name": "Floor 1", "elevation_mm": 3000},
        {"op": "stair", "key": "S", "base_level": "Ground Floor", "top_level": "Floor 1", "width_mm": 1200,
         "tread_mm": 250, "runs": [{"start": [0, 0], "end": [0, 2000], "risers": 9},
                                   {"start": [1500, 2000], "end": [1500, 0], "risers": 9}]},
        {"op": "railing", "key": "S.railing", "stair_key": "S"},
    ]),
    sources={"S": Source(sheet=SHEET, element="stair-1", storey="0"),
             "S.railing": Source(sheet=SHEET, element="stair-1", storey="0")},
    standards=ProjectStandards())


class FakeRevit:
    """read_back_elements answering from the given records."""

    def __init__(self, records: dict[int, dict]):
        self.records = records

    def call(self, command, params=None, **kw):
        assert command == "read_back_elements"
        return {"elements": [{"id": i, "exists": True, **self.records.get(i, {})} for i in params["element_ids"]],
                "next_start": None}


def run_with(created: dict[str, int]):
    stored = {"import_plan": PLAN.model_dump(mode="json"), "run": {"created": created}}
    return SimpleNamespace(id="run-1", load=stored.get)


def estimate_of(created: dict[str, int], records: dict[int, dict], answers: dict | None = None):
    project = ImportProject(id="p", created="2026-11-16", sheets=[
        SheetRecord(file=SHEET, source=SHEET, role="floor_plan", storeys=[0], units="mm", entities={}, layers={},
                    exploded=0, excluded={})])
    project.inputs.values.update(answers or {})
    run = run_with(created)
    measured = estimate.model_quantities_record(FakeRevit(records), run)
    return measured, estimate.quantities(project, RecognisedBuilding(), load_rules("office-default"), run=run,
                                         measured=measured)


def railing(length_mm: float | None) -> dict:
    return {"category": "OST_StairsRailing",
            "railing": {"host_id": 10, **({"path_length_mm": length_mm} if length_mm else {})}}


def test_stair_railings_revit_made_are_measured_not_asked():
    measured, (q, questions, source) = estimate_of(
        {"S": 10, "S#2": 11, "S#3": 12}, {11: railing(2799.9996), 12: railing(5120.4)})
    assert [e["op_key"] for e in measured["elements"]] == ["S#2", "S#3"]  # the stair itself is not a railing
    assert [(r.kind, r.length_mm, r.count, r.storey) for r in q.railings] == [("stair", 2800, 1, 0),
                                                                             ("stair", 5120, 1, 0)]
    assert not any(x["id"].startswith("estimate:railings:") for x in questions)
    assert source["source"] == "model"


def test_equal_railings_group_into_one_line():
    _, (q, _, _) = estimate_of({"S": 10, "S#2": 11, "S#3": 12}, {11: railing(2800.0004), 12: railing(2799.9998)})
    assert [(r.length_mm, r.count) for r in q.railings] == [(2800, 2)]


def test_a_railing_op_that_ran_is_measured_too():
    _, (q, questions, _) = estimate_of({"S": 10, "S.railing": 20, "S.railing#2": 21},
                                       {20: railing(3000), 21: railing(3000)})
    assert [(r.kind, r.length_mm, r.count) for r in q.railings] == [("stair", 3000, 2)]
    assert not questions


def test_railings_with_no_length_read_back_are_asked_and_the_answer_used():
    created, records = {"S": 10, "S#2": 11, "S#3": 12}, {11: railing(None), 12: railing(None)}  # an older add-in
    _, (q, questions, _) = estimate_of(created, records)
    (asked,) = [x for x in questions if x["id"] == "estimate:railings:stair"]
    assert "2 stair railings in the model with no length read back" in asked["text"]
    assert q.railings == []
    _, (q, questions, _) = estimate_of(created, records, {"estimate:railings:stair": {"length_mm": 2800, "count": 2}})
    assert [(r.length_mm, r.count) for r in q.railings] == [(2800, 2)]
    assert not any(x["id"] == "estimate:railings:stair" for x in questions)
