"""Columns, beams, stairs, railings and offset floors: plan validation, command mapping, the runner's direct
calls for stairs and railings, read-back and removal, and the type map sections for them."""

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from b2m.plan import BuildPlan, translate
from b2m.revit import commands
from b2m.revit.client import RevitError
from b2m.revit.types import FamilyType, TypeMap
from b2m.runner import Runner, plan_batches

LEVELS = [{"op": "level", "key": "L0", "name": "L0", "elevation_mm": 0},
          {"op": "level", "key": "L1", "name": "L1", "elevation_mm": 3000}]
COLUMN = {"op": "column", "key": "C1", "level": "L0", "top_level": "L1", "point": (0, 0), "type": "RCC Column 230x450"}
BEAM = {"op": "beam", "key": "B1", "level": "L1", "start": (0, 0), "end": (4000, 0), "type": "RCC Beam 230x450"}
STAIR = {"op": "stair", "key": "S1", "base_level": "L0", "top_level": "L1", "type": "RCC Stair", "width_mm": 1200,
         "tread_mm": 250, "runs": [{"start": (600, 3000), "end": (600, 1250), "risers": 8},
                                   {"start": (1950, 1250), "end": (1950, 3000), "risers": 8}]}
RAILING = {"op": "railing", "key": "R1", "type": "Railing 900", "stair_key": "S1"}
WALL = {"op": "wall", "key": "W1", "level": "L0", "top_level": "L1", "start": (0, 0), "end": (4000, 0), "type": "Brick 230"}
SLAB = {"op": "floor", "key": "F1", "level": "L1", "type": "RCC 150", "points": [(0, 0), (4000, 0), (4000, 3000)]}
CHAJJA = {"op": "floor", "key": "H1", "level": "L0", "type": "Chajja 100", "offset_mm": 2550,
          "points": [(0, -115), (1000, -115), (1000, -715), (0, -715)]}
PLAN = BuildPlan.model_validate({"ops": LEVELS + [RAILING, STAIR, BEAM, SLAB, CHAJJA, WALL, COLUMN]})


def plan_with(*ops):
    return BuildPlan.model_validate({"ops": LEVELS + list(ops)})


def test_new_ops_are_validated():
    with pytest.raises(ValidationError, match="distinct start and end"):
        plan_with({**BEAM, "end": BEAM["start"]})
    with pytest.raises(ValidationError, match="not above"):
        plan_with({**COLUMN, "level": "L1", "top_level": "L0"})
    with pytest.raises(ValidationError, match="not a level"):
        plan_with({**STAIR, "top_level": "L9"})
    with pytest.raises(ValidationError, match="not a stair"):
        plan_with(BEAM, {**RAILING, "stair_key": "B1"})
    with pytest.raises(ValidationError, match="exactly one of stair_key or path"):
        plan_with(STAIR, {**RAILING, "path": [(0, 0), (1, 0)], "level": "L0"})
    with pytest.raises(ValidationError, match="needs a level"):
        plan_with({**RAILING, "stair_key": None, "path": [(0, 0), (1000, 0)]})
    with pytest.raises(ValidationError):
        plan_with({**STAIR, "runs": [{"start": (0, 0), "end": (0, 100), "risers": 1}]})  # a run needs 2 risers
    with pytest.raises(ValidationError, match="may not contain"):
        plan_with({**COLUMN, "key": "C#1"})


def test_build_order_puts_structure_between_walls_and_openings():
    phases = [b[0].op for b in plan_batches(PLAN)]
    assert phases == ["level", "column", "wall", "floor", "beam", "stair", "railing"]


def test_column_beam_stair_and_railing_commands():
    by_key = PLAN.by_key()
    types = TypeMap.from_dict({"columns": {"RCC Column 230x450": {"family": "M_Concrete-Rectangular-Column", "type": "230 x 450mm"}},
                               "beams": {"RCC Beam 230x450": {"family": "M_Concrete-Rectangular Beam", "type": "230 x 450mm"}},
                               "stairs": {"RCC Stair": "190mm max riser 250mm going"}, "railings": {"Railing 900": "900mm"}})
    column = commands.to_command(by_key["C1"], by_key, {}, types)
    assert column == {"command": "create_structural_column", "params": {
        "type_name": "230 x 450mm", "family_name": "M_Concrete-Rectangular-Column", "level": "L0", "x": 0, "y": 0,
        "top_level": "L1"}}
    turned = commands.to_command(plan_with({**COLUMN, "rotation_deg": 90}).by_key()["C1"], {}, {})
    assert turned["params"]["rotation_deg"] == 90 and "family_name" not in turned["params"]
    beam = commands.to_command(by_key["B1"], by_key, {}, types)
    assert beam == {"command": "create_structural_beam", "params": {
        "type_name": "230 x 450mm", "family_name": "M_Concrete-Rectangular Beam", "level": "L1",
        "start_x": 0, "start_y": 0, "end_x": 4000, "end_y": 0}}
    stair = commands.to_command(by_key["S1"], by_key, {}, types)
    assert stair == {"command": "create_stair", "params": {
        "base_level": "L0", "top_level": "L1", "type_name": "190mm max riser 250mm going", "width_mm": 1200,
        "tread_mm": 250, "runs": [{"start": [600, 3000], "end": [600, 1250], "risers": 8},
                                  {"start": [1950, 1250], "end": [1950, 3000], "risers": 8}]}}
    railing = commands.to_command(by_key["R1"], by_key, {"S1": 77}, types)
    assert railing == {"command": "create_railing", "params": {"type_name": "900mm", "stair_id": 77, "placement": "treads"}}
    path = commands.to_command(plan_with({**RAILING, "stair_key": None, "path": [(0, 0), (1000, 0)], "level": "L1"}).by_key()["R1"], {}, {})
    assert path["params"] == {"type_name": "Railing 900", "path": [[0, 0], [1000, 0]], "level": "L1"}


def test_floor_height_offset_is_sent_only_when_set():
    by_key = PLAN.by_key()
    chajja = commands.to_command(by_key["H1"], by_key, {})
    assert chajja["params"]["heightOffset"] == 2550 and chajja["params"]["typeName"] == "Chajja 100"
    slab = commands.to_command(by_key["F1"], by_key, {})
    assert "heightOffset" not in slab["params"]
    assert "offset_mm" not in by_key["F1"].model_dump() and by_key["H1"].model_dump()["offset_mm"] == 2550


def test_translate_moves_stair_runs_and_railing_paths():
    moved = translate(plan_with(STAIR, {**RAILING, "stair_key": None, "path": [(0, 0), (1000, 0)], "level": "L1"}), 10, 20)
    stair, railing = moved.by_key()["S1"], moved.by_key()["R1"]
    assert stair.runs[0].start == (610, 3020) and stair.runs[1].end == (1960, 3020)
    assert railing.path == [(10, 20), (1010, 20)]


class StructureRevit:
    """batch_execute answers like the add-in does for each command; stairs and railings are direct calls."""

    def __init__(self, stair_railings=(), fail=None):
        self.calls = []
        self.next_id = 100
        self.stair_railings = list(stair_railings)
        self.fail = fail
        self.stats = None

    def _id(self):
        self.next_id += 1
        return self.next_id

    def _result(self, command):
        if command == "create_structural_column":
            return {"column_id": self._id()}
        if command == "create_structural_beam":
            return {"element_id": self._id()}
        return {"elementId": self._id()}

    def call(self, command, params=None, *, timeout_seconds=None):
        self.calls.append((command, params))
        if command == self.fail:
            raise RevitError({"success": False, "error": "RUNS_NOT_CONNECTABLE: no landing fits"})
        if command == "batch_execute":
            assert not {c["command"] for c in params["commands"]} & {"create_stair", "create_railing"}
            return {"results": [{"index": i, "ok": True, "data": self._result(c["command"])}
                                for i, c in enumerate(params["commands"])], "rolledBack": False}
        if command == "create_stair":
            return {"stair_id": self._id(), "run_ids": [1, 2], "landing_ids": [3], "railing_ids": self.stair_railings}
        if command == "create_railing":
            return {"railing_ids": [self._id(), self._id()], "type_name": params.get("type_name")}
        if command == "analyze_model_statistics":
            return self.stats
        if command == "get_model_warnings_summary":
            return {"total_warnings": 0, "warnings": []}
        if command == "delete_element":
            return {"deleted": len(params["elementIds"]), "failedCount": 0}
        raise AssertionError(command)


def stats_for(created, counts):
    return {"categories": [{"builtInCategory": c, "level": None, "count": n} for c, n in counts.items()], "missingIds": []}


def test_runner_calls_stair_and_railing_directly_and_records_every_railing():
    revit = StructureRevit()
    runner = Runner(revit)
    result = runner.build(PLAN)
    assert result.status == "built" and result.batches_succeeded == result.batches_total == 7
    direct = [(c, p) for c, p in revit.calls if c in ("create_stair", "create_railing")]
    assert [c for c, _ in direct] == ["create_stair", "create_railing"]
    assert direct[1][1]["stair_id"] == result.created["S1"]
    assert {"C1", "B1", "S1", "R1", "R1#2"} <= result.created.keys()
    revit.stats = {"categories": [
        {"builtInCategory": "OST_Levels", "count": 2}, {"builtInCategory": "OST_StructuralColumns", "count": 1},
        {"builtInCategory": "OST_StructuralFraming", "count": 1}, {"builtInCategory": "OST_Stairs", "count": 1},
        {"builtInCategory": "OST_StairsRailing", "count": 2}, {"builtInCategory": "OST_Walls", "level": "L0", "count": 1},
        {"builtInCategory": "OST_Floors", "level": "L0", "count": 1}, {"builtInCategory": "OST_Floors", "level": "L1", "count": 1},
    ], "missingIds": []}
    check = runner.verify(PLAN, result.created)
    assert check["ok"], check
    runner.remove(result.created)
    assert sorted(revit.calls[-1][1]["elementIds"]) == sorted(result.created.values())


def test_a_stair_that_came_with_railings_keeps_them_and_skips_the_railing_op():
    revit = StructureRevit(stair_railings=[901, 902])
    runner = Runner(revit)
    result = runner.build(PLAN)
    assert result.status == "built"
    assert "create_railing" not in [c for c, _ in revit.calls]
    assert result.created["S1#2"] == 901 and result.created["S1#3"] == 902 and "R1" not in result.created
    revit.stats = {"categories": [
        {"builtInCategory": "OST_Levels", "count": 2}, {"builtInCategory": "OST_StructuralColumns", "count": 1},
        {"builtInCategory": "OST_StructuralFraming", "count": 1}, {"builtInCategory": "OST_Stairs", "count": 1},
        {"builtInCategory": "OST_StairsRailing", "count": 2}, {"builtInCategory": "OST_Walls", "level": "L0", "count": 1},
        {"builtInCategory": "OST_Floors", "level": "L0", "count": 1}, {"builtInCategory": "OST_Floors", "level": "L1", "count": 1},
    ], "missingIds": []}
    assert runner.verify(PLAN, result.created)["ok"]


def test_a_failed_direct_call_stops_the_run_and_names_the_op():
    revit = StructureRevit(fail="create_stair")
    result = Runner(revit).build(PLAN)
    assert result.status == "failed" and result.failed_key == "S1" and result.error_code == "RUNS_NOT_CONNECTABLE"
    assert "S1" not in result.created and "B1" in result.created


def test_type_map_sections_for_structure_stairs_and_railings():
    types = TypeMap.from_dict({"columns": {"RCC Column 230x450": {"family": "Col", "type": "230x450"}},
                               "beams": {"RCC Beam 230x450": "Beam 230x450"},
                               "stairs": {"RCC Stair": "Escalera"}, "railings": {"Railing 900": "Baranda"}})
    assert types.column("RCC Column 230x450", None) == FamilyType("230x450", "Col")
    assert types.column("Other", "Fam") == FamilyType("Other", "Fam")
    assert types.beam("RCC Beam 230x450", None) == FamilyType("Beam 230x450")
    assert types.stair("RCC Stair") == "Escalera" and types.stair("X") == "X"
    assert types.railing("Railing 900") == "Baranda"
    with pytest.raises(ValueError, match="must map to a type name"):
        TypeMap.from_dict({"stairs": {"RCC Stair": {"type": "x"}}})


def test_example_standards_file_maps_every_logical_type_the_engine_uses():
    from b2m.engine import catalog, structure
    example = Path(__file__).resolve().parents[2] / "installer" / "standards.example.json"
    types = TypeMap.from_dict(json.loads(example.read_text(encoding="utf-8")))
    assert set(catalog.WALL_THICKNESS) <= types.walls.keys()
    assert {*catalog.SLAB_THICKNESS, structure.CHAJJA} <= types.floors.keys()
    assert structure.COLUMN in types.columns and structure.BEAM in types.beams
    assert structure.STAIR in types.stairs and structure.RAILING in types.railings
    assert {d.name for d in catalog.DOORS.values()} <= types.doors.keys()
