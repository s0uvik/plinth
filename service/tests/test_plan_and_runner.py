import pytest
from pydantic import ValidationError

from b2m.examples import two_storey_house
from b2m.plan import BuildPlan
from b2m.revit import commands
from b2m.revit.client import RevitError, RevitTimeout
from b2m.runner import Runner, plan_batches

HOUSE = two_storey_house("Door 900", "Window 1200")


class FakeRevit:
    """Records calls; batch_execute returns ids 1000, 1001...; can fail a given batch."""

    def __init__(self, fail_batch: int | None = None, fail_index: int = 0, timeout_batch: int | None = None):
        self.calls: list[tuple[str, dict]] = []
        self.next_id = 1000
        self.fail_batch, self.fail_index, self.timeout_batch = fail_batch, fail_index, timeout_batch
        self.batches = 0
        self.stats = None
        self.warnings = {"total_warnings": 0, "warnings": []}

    def call(self, command, params=None, *, timeout_seconds=None):
        self.calls.append((command, params))
        if command == "batch_execute":
            n = self.batches
            self.batches += 1
            if n == self.timeout_batch:
                raise RevitTimeout({"id": "x", "success": False, "pending": True, "error": "REVIT_OPERATION_TIMED_OUT: slow"})
            if n == self.fail_batch:
                raise RevitError({"success": False, "error": "BATCH_ROLLED_BACK: sub-command failed",
                                  "data": {"results": [{"index": self.fail_index, "ok": False, "error": "LEVEL_NOT_FOUND: x"}], "rolledBack": True},
                                  "failures": [{"severity": "Warning", "description": "overlap"}]})
            results = []
            for i, sub in enumerate(params["commands"]):
                assert sub["command"] not in ("create_stair", "create_railing"), "called directly, never in a batch"
                field = {"create_structural_column": "column_id", "create_structural_beam": "element_id"}.get(sub["command"], "elementId")
                results.append({"index": i, "ok": True, "data": {field: self.next_id}})
                self.next_id += 1
            return {"results": results, "rolledBack": False}
        if command == "create_stair":
            self.next_id += 1
            return {"stair_id": self.next_id - 1, "run_ids": [], "landing_ids": [], "railing_ids": []}
        if command == "create_railing":
            self.next_id += 1
            return {"railing_ids": [self.next_id - 1]}
        if command == "analyze_model_statistics":
            return self.stats
        if command == "get_model_warnings_summary":
            return self.warnings
        if command == "delete_element":
            return {"deleted": len(params["elementIds"]), "failedCount": 0}
        raise AssertionError(command)


def test_plan_rejects_bad_references():
    base = [{"op": "level", "key": "L0", "name": "L0", "elevation_mm": 0}]
    wall = {"op": "wall", "key": "W", "level": "L0", "start": (0, 0), "end": (1000, 0), "height_mm": 3000}
    with pytest.raises(ValidationError, match="duplicate key"):
        BuildPlan.model_validate({"ops": base + [{**wall, "key": "L0"}]})
    with pytest.raises(ValidationError, match="not a level"):
        BuildPlan.model_validate({"ops": base + [{**wall, "level": "L9"}]})
    with pytest.raises(ValidationError, match="not a wall"):
        BuildPlan.model_validate({"ops": base + [wall, {"op": "door", "key": "D", "host_key": "L0", "type": "D", "offset_mm": 1}]})
    with pytest.raises(ValidationError, match="exactly one of height_mm or top_level"):
        BuildPlan.model_validate({"ops": base + [{**wall, "height_mm": None}]})
    with pytest.raises(ValidationError):
        BuildPlan.model_validate({"ops": base + [{**wall, "colour": "red"}]})  # unknown fields are errors


def test_batches_follow_build_order_and_never_exceed_twenty():
    batches = plan_batches(HOUSE)
    phases = [b[0].op for b in batches]
    assert phases == ["level", "grid", "wall", "floor", "shaft", "door", "room"]
    assert all(len(b) <= 20 for b in batches)
    assert sum(len(b) for b in batches) == len(HOUSE.ops)
    assert {op.op for op in batches[5]} == {"door", "window"}

    many = BuildPlan.model_validate({"ops": [{"op": "level", "key": f"L{i}", "name": f"L{i}", "elevation_mm": i * 3000} for i in range(45)]})
    assert [len(b) for b in plan_batches(many)] == [20, 20, 5]


def test_same_plan_gives_same_commands():
    assert [b for b in plan_batches(HOUSE)] == [b for b in plan_batches(two_storey_house("Door 900", "Window 1200"))]


def test_doors_and_windows_get_host_ids_and_level_from_their_wall():
    by_key = HOUSE.by_key()
    door = commands.to_command(by_key["F1-DI"], by_key, {"F1-WI": 4242})
    assert door["command"] == "create_point_based_element"
    assert door["params"]["host_id"] == 4242
    assert door["params"]["level"] == "TH L1"
    assert door["params"]["builtInCategory"] == "OST_Doors"
    window = commands.to_command(by_key["F0-WN1"], by_key, {"F0-WN": 7})
    assert window["params"]["sillHeight"] == 900
    wall = commands.to_command(by_key["F0-WS"], by_key, {})
    assert wall["params"]["topLevel"] == "TH L1" and "height" not in wall["params"]
    room = commands.to_command(by_key["F0-R1"], by_key, {})
    assert room["params"]["department"] == "TH-A"


def test_build_swaps_keys_for_ids_and_reports_progress():
    revit = FakeRevit()
    progress = []
    result = Runner(revit, progress=lambda d, t: progress.append((d, t))).build(HOUSE)
    assert result.status == "built"
    assert result.batches_succeeded == result.batches_total == 7
    assert len(result.created) == len(HOUSE.ops)
    assert progress[0] == (0, 7) and progress[-1] == (7, 7)
    door_batch = [p for c, p in revit.calls if c == "batch_execute"][5]
    wall_ids = {result.created[k] for k in result.created if "-W" in k and k.count("-") == 1}
    assert all(cmd["params"]["host_id"] in wall_ids for cmd in door_batch["commands"])


def test_build_stops_at_first_failed_batch_and_names_the_op():
    revit = FakeRevit(fail_batch=2, fail_index=3)
    result = Runner(revit).build(HOUSE)
    assert result.status == "failed"
    assert result.batches_succeeded == 2
    assert result.error_code == "BATCH_ROLLED_BACK"
    assert result.failed_key == plan_batches(HOUSE)[2][3].key
    assert len(result.created) == 3 + 5  # levels and grids only
    assert result.failures[0]["description"] == "overlap"
    assert sum(1 for c, _ in revit.calls if c == "batch_execute") == 3


def test_timeout_marks_the_run_unknown():
    result = Runner(FakeRevit(timeout_batch=1)).build(HOUSE)
    assert result.status == "unknown"
    assert result.batches_succeeded == 1


def _stats_for(created, plan=None):
    rows = {}
    by_key = (plan or HOUSE).by_key()
    for key in created:
        op = by_key[commands.op_key(key)]
        level = commands.expected_level(op, by_key) if key == op.key else None
        k = (commands.category_of(key, op), level)
        rows[k] = rows.get(k, 0) + 1
    return {"categories": [{"builtInCategory": c, "level": lv, "count": n} for (c, lv), n in rows.items()], "missingIds": []}


def test_verify_passes_when_revit_matches_the_plan():
    revit = FakeRevit()
    runner = Runner(revit)
    result = runner.build(HOUSE)
    revit.stats = _stats_for(result.created)
    check = runner.verify(HOUSE, result.created)
    assert check["ok"], check
    assert check["elements"] == len(HOUSE.ops)


def test_verify_reports_missing_mismatched_and_warnings():
    revit = FakeRevit()
    runner = Runner(revit)
    result = runner.build(HOUSE)
    stats = _stats_for(result.created)
    for row in stats["categories"]:
        if row["builtInCategory"] == "OST_Doors" and row["level"] == "TH L0":
            row["count"] -= 1
    stats["missingIds"] = [123]
    revit.stats = stats
    revit.warnings = {"total_warnings": 1, "warnings": [{"description": "Walls overlap", "count": 1}]}
    check = runner.verify(HOUSE, result.created)
    assert not check["ok"]
    assert check["mismatches"] == [{"category": "OST_Doors", "level": "TH L0", "expected": 2, "actual": 1}]
    assert check["missing_ids"] == [123]
    assert check["warnings_total"] == 1


def test_remove_deletes_newest_first_in_one_call():
    revit = FakeRevit()
    Runner(revit).remove({"a": 1, "b": 2, "c": 3})
    assert revit.calls == [("delete_element", {"elementIds": [3, 2, 1]})]


def test_remove_counts_elements_the_user_already_undid_as_gone_not_failed():
    revit = FakeRevit()
    revit.call = lambda command, params, **kw: (
        {"deleted": 1, "failedCount": 2, "total": 3, "failedIds": [3, 2], "errors": ["3: not found", "2: locked"]}
        if command == "delete_element" else {"missingIds": [3]})
    result = Runner(revit).remove({"a": 1, "b": 2, "c": 3})
    assert result["alreadyGoneIds"] == [3]
    assert result["failedIds"] == [2] and result["failedCount"] == 1 and result["errors"] == ["2: locked"]


def test_type_map_converts_logical_names_and_passes_unknown_ones_through():
    from b2m.revit.types import TypeMap
    types = TypeMap.from_dict({"walls": {"Brick 230": "Muro de 25cm"},
                               "doors": {"Door 900": {"family": "Puerta", "type": "0.90 x 2.10"}}})
    by_key = HOUSE.by_key()
    door = commands.to_command(by_key["F1-DI"], by_key, {"F1-WI": 1}, types)
    assert door["params"]["typeName"] == "0.90 x 2.10" and door["params"]["familyName"] == "Puerta"
    window = commands.to_command(by_key["F0-WN1"], by_key, {"F0-WN": 7}, types)
    assert window["params"]["typeName"] == "Window 1200" and "familyName" not in window["params"]
    assert types.wall("Brick 230") == "Muro de 25cm" and types.wall("Brick 115") == "Brick 115"
    with pytest.raises(ValueError):
        TypeMap.from_dict({"roofs": {}})


def test_type_map_loads_the_users_standards_file(tmp_path, monkeypatch):
    from b2m.revit.types import TypeMap
    monkeypatch.setenv("B2M_HOME", str(tmp_path))
    assert TypeMap.load() == TypeMap()
    (tmp_path / "standards.json").write_text('{"floors": {"RCC 150": "Losa 15"}}', encoding="utf-8")
    assert TypeMap.load().floor("RCC 150") == "Losa 15"


def test_translate_moves_every_coordinate_and_nothing_else():
    from b2m.plan import translate
    moved = translate(HOUSE, 1000, -500)
    a, b = HOUSE.by_key(), moved.by_key()
    assert b["F0-WS"].start == (a["F0-WS"].start[0] + 1000, a["F0-WS"].start[1] - 500)
    assert b["STAIR"].points[0] == (a["STAIR"].points[0][0] + 1000, a["STAIR"].points[0][1] - 500)
    assert b["F0-R1"].point == (a["F0-R1"].point[0] + 1000, a["F0-R1"].point[1] - 500)
    assert b["F0-DI"] == a["F0-DI"] and b["L1"] == a["L1"]
