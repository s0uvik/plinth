"""Fit-out plan ops (F5): types from a template, size copies, placed families, MEP runs and circuits, built on an earlier
run's walls and levels, mapped to the add-in's commands, built by the runner, read back and removed: only what the run
made goes, types the project already had stay untouched."""

import pytest
from pydantic import ValidationError

from plinth.plan import BuildPlan
from plinth.revit import commands
from plinth.runner import Runner, plan_batches
from tests.fitout.fake_revit import FitoutRevit

TEMPLATE = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Residential-Default_Metric.rte"
WALL_ID = 9001


def _template():
    return {TEMPLATE: {
        "family_types": [
            {"id": 11, "category": "OST_Furniture", "family": "M_Bed-Standard", "type": "1525x2032mm",
             "placement": "OneLevelBased", "dims_mm": {"width": 1525, "depth": 2032}},
            {"id": 12, "category": "OST_PlumbingFixtures", "family": "M_Lavatory-Wall", "type": "560x430mm",
             "placement": "WorkPlaneBased", "dims_mm": {"width": 560, "depth": 430}},
            {"id": 13, "category": "OST_LightingFixtures", "family": "M_Downlight", "type": "100W",
             "placement": "WorkPlaneBased", "dims_mm": {}},
            {"id": 14, "category": "OST_ElectricalEquipment", "family": "M_Panel", "type": "DB 8 way",
             "placement": "OneLevelBased", "dims_mm": {}},
            {"id": 15, "category": "OST_Furniture", "family": "M_Chair", "type": "Standard",
             "placement": "OneLevelBased", "dims_mm": {}},
        ],
        "mep_types": {"pipe_type": [{"id": 21, "name": "PVC - DWV"}],
                      "pipe_system_type": [{"id": 22, "name": "Sanitary", "classification": "Sanitary"}],
                      "conduit_type": [{"id": 23, "name": "Rigid Nonmetallic Conduit (RNC Sch 40)"}]}}}


def _revit():
    # The project already has the chair type: it must be used as it is and survive the removal.
    return FitoutRevit(
        types=[{"category": "OST_Furniture", "family": "M_Chair", "type": "Standard"}],
        templates=_template(),
        elements={WALL_ID: {"category": "OST_Walls", "level": "L1"}, 9002: {"category": "OST_Levels", "level": None}})


def _plan(**over):
    ops = [
        {"op": "types_from_file", "key": "FIT-TYPES-RES", "path": TEMPLATE, "items": [
            {"kind": "family_type", "family": "M_Bed-Standard", "name": "1525x2032mm", "category": "OST_Furniture"},
            {"kind": "family_type", "family": "M_Lavatory-Wall", "name": "560x430mm", "category": "OST_PlumbingFixtures"},
            {"kind": "family_type", "family": "M_Downlight", "name": "100W", "category": "OST_LightingFixtures"},
            {"kind": "family_type", "family": "M_Panel", "name": "DB 8 way", "category": "OST_ElectricalEquipment"},
            {"kind": "family_type", "family": "M_Chair", "name": "Standard", "category": "OST_Furniture"},
            {"kind": "pipe_type", "name": "PVC - DWV"},
            {"kind": "pipe_system_type", "name": "Sanitary"},
            {"kind": "conduit_type", "name": "Rigid Nonmetallic Conduit (RNC Sch 40)"}]},
        {"op": "family_type_copy", "key": "FIT-TYPE-BED-1800", "category": "OST_Furniture", "family": "M_Bed-Standard",
         "base_type": "1525x2032mm", "name": "1800x2000mm (Plinth)", "values_mm": {"Width": 1800, "Depth": 2000}},
        {"op": "family", "key": "FIT-BED", "category": "OST_Furniture", "family": "M_Bed-Standard",
         "type": "1800x2000mm (Plinth)", "level": "L1", "point": (2000, 3000), "rotation_deg": 90, "room_key": "R1",
         "source": "furniture: bed"},
        {"op": "family", "key": "FIT-CHAIR", "category": "OST_Furniture", "family": "M_Chair", "type": "Standard",
         "level": "L1", "point": (1000, 1000)},
        {"op": "family", "key": "FIT-BASIN", "category": "OST_PlumbingFixtures", "family": "M_Lavatory-Wall",
         "type": "560x430mm", "level": "L1", "point": (4000, 100), "offset_mm": 800, "host_key": "W-S",
         "toward": (4000, 1000)},
        {"op": "family", "key": "FIT-LIGHT", "category": "OST_LightingFixtures", "family": "M_Downlight", "type": "100W",
         "level": "L1", "point": (2500, 2500), "offset_mm": 2700, "face_down": True},
        {"op": "family", "key": "FIT-DB", "category": "OST_ElectricalEquipment", "family": "M_Panel", "type": "DB 8 way",
         "level": "L1", "point": (300, 300), "offset_mm": 1500},
        {"op": "mep_run", "key": "FIT-SOIL", "kind": "pipe", "level": "L1", "type": "PVC - DWV", "system_type": "Sanitary",
         "points": [(4000, 300, 3200), (4000, 300, 3000), (6000, 300, 3000)], "diameter_mm": 100},
        {"op": "mep_run", "key": "FIT-WASTE", "kind": "pipe", "level": "L1", "type": "PVC - DWV", "system_type": "Sanitary",
         "points": [(4000, 200, 3500), (4500, 200, 3500)], "diameter_mm": 40, "start_key": "FIT-BASIN",
         "end_key": "FIT-SOIL"},
        {"op": "mep_run", "key": "FIT-CONDUIT", "kind": "conduit", "level": "L1",
         "type": "Rigid Nonmetallic Conduit (RNC Sch 40)", "points": [(300, 300, 1500), (300, 300, 2900), (2500, 2500, 2900)],
         "diameter_mm": 20, "start_key": "FIT-DB", "end_key": "FIT-LIGHT"},
        {"op": "circuit", "key": "FIT-C1", "device_keys": ["FIT-LIGHT"], "panel_key": "FIT-DB", "system_type": "PowerCircuit"},
    ]
    data = {"ops": ops, "existing_levels": {"L1": 3000.0}, "existing": {"W-S": WALL_ID}}
    data.update(over)
    return BuildPlan.model_validate(data)


def test_fitout_ops_check_their_references():
    plan = _plan()
    assert set(plan.model_dump(mode="json")) == {"plan_version", "spec_hash", "ops", "existing_levels", "existing"}
    with pytest.raises(ValidationError, match="not a level"):
        _plan(existing_levels={"L9": 0.0})
    with pytest.raises(ValidationError, match="host_key 'W-S' is not a wall or floor"):
        _plan(existing={})
    bad = _plan().model_dump(mode="json")
    soil, waste = [o for o in bad["ops"] if o["key"] in ("FIT-SOIL", "FIT-WASTE")]
    bad["ops"] = [o for o in bad["ops"] if o["key"] not in ("FIT-SOIL", "FIT-WASTE")] + [waste, soil]
    with pytest.raises(ValidationError, match="listed after it"):
        BuildPlan.model_validate(bad)
    with pytest.raises(ValidationError, match="not electrical equipment"):
        BuildPlan.model_validate({**_plan().model_dump(mode="json"), "ops": [
            *[o for o in _plan().model_dump(mode="json")["ops"] if o["op"] != "circuit"],
            {"op": "circuit", "key": "C", "device_keys": ["FIT-LIGHT"], "panel_key": "FIT-CHAIR"}]})
    with pytest.raises(ValidationError, match="system_type"):
        BuildPlan.model_validate({"ops": [{"op": "mep_run", "key": "P", "kind": "pipe", "level": "L", "type": "T",
                                           "points": [(0, 0, 0), (1, 0, 0)]}], "existing_levels": {"L": 0}})
    with pytest.raises(ValidationError, match="face_down places a family without a host"):
        BuildPlan.model_validate({"ops": [{"op": "family", "key": "F", "category": "OST_LightingFixtures", "type": "T",
                                           "level": "L", "point": (0, 0), "face_down": True, "host_key": "W"}],
                                  "existing_levels": {"L": 0}, "existing": {"W": 1}})
    # Plans without the fit-out context dump as before.
    plain = BuildPlan.model_validate({"ops": [{"op": "level", "key": "L", "name": "L", "elevation_mm": 0}]})
    assert set(plain.model_dump(mode="json")) == {"plan_version", "spec_hash", "ops"}


def test_fitout_ops_map_to_the_addin_commands():
    plan, by_key = _plan(), _plan().by_key()
    ids = {"W-S": WALL_ID, "FIT-BASIN": 1, "FIT-SOIL": 2, "FIT-LIGHT": 3, "FIT-DB": 4}
    bed = commands.to_command(by_key["FIT-BED"], by_key, ids)
    assert bed == {"command": "create_point_based_element", "params": {
        "level": "L1", "x": 2000, "y": 3000, "typeName": "1800x2000mm (Plinth)", "familyName": "M_Bed-Standard",
        "builtInCategory": "OST_Furniture", "rotation": 90}}
    basin = commands.to_command(by_key["FIT-BASIN"], by_key, ids)["params"]
    assert basin["host_id"] == WALL_ID and (basin["towardX"], basin["towardY"]) == (4000, 1000) and basin["offset"] == 800
    light = commands.to_command(by_key["FIT-LIGHT"], by_key, ids)
    assert light["command"] == "create_lighting_fixture"
    assert light["params"] == {"level": "L1", "x": 2500, "y": 2500, "type_name": "100W", "family_name": "M_Downlight",
                               "offset": 2700, "face_down": True}
    waste = commands.to_command(by_key["FIT-WASTE"], by_key, ids)
    assert waste == {"command": "create_mep_run", "params": {
        "kind": "pipe", "level": "L1", "points": [[4000, 200, 3500], [4500, 200, 3500]], "type_name": "PVC - DWV",
        "require_connections": True, "system_type_name": "Sanitary", "diameter": 40,
        "start_connect": {"element_id": 1}, "end_connect": {"element_id": 2}}}
    circuit = commands.to_command(by_key["FIT-C1"], by_key, ids)
    assert circuit == {"command": "create_electrical_circuit",
                       "params": {"element_ids": [3], "system_type": "PowerCircuit", "panel_id": 4}}
    copy_types = commands.to_command(by_key["FIT-TYPES-RES"], by_key, ids)
    assert copy_types["command"] == "copy_types_from_file" and copy_types["params"]["items"][5] == {
        "kind": "pipe_type", "name": "PVC - DWV"}
    # Types first (each its own call), then families, the soil stack, the waste teed into it (its own batch: the
    # stack's id comes back with the stack's batch), the conduit, the circuit.
    assert [[op.key for op in b] for b in plan_batches(plan)] == [
        ["FIT-TYPES-RES"], ["FIT-TYPE-BED-1800"], ["FIT-BED", "FIT-CHAIR", "FIT-BASIN", "FIT-LIGHT", "FIT-DB"],
        ["FIT-SOIL"], ["FIT-WASTE", "FIT-CONDUIT"], ["FIT-C1"]]


def test_fitout_builds_reads_back_and_removes_only_what_it_made():
    revit = _revit()
    before = dict(revit.elements)
    plan = _plan()
    runner = Runner(revit)
    result = runner.build(plan)
    assert result.status == "built", result.error
    # The copy recorded what it added (families and types, not the chair the project had); the size copy its type.
    copied = [k for k in result.created if k.startswith("FIT-TYPES-RES")]
    assert len(copied) == 4 * 2 + 3  # 4 new families with a type each, 3 MEP types
    chair_type = next(i for i, e in before.items() if e.get("type") == "Standard")
    assert chair_type not in result.created.values()
    copy_call = next(p for c, p in revit.calls if c == "duplicate_family_type")
    assert revit.elements[copy_call["source_type_id"]]["type"] == "1525x2032mm"  # the copied type's new id
    assert copy_call["type_parameter_overrides"] == {"Width": 1800, "Depth": 2000}
    # Hosts and joins went to the earlier run's wall and to this run's elements; the wall is not this run's.
    assert WALL_ID not in result.created.values()
    basin = revit.elements[result.created["FIT-BASIN"]]
    assert basin["host"] == WALL_ID
    # An MEP run's entries say what they are: segments key, key#2; fittings key#f1.
    assert {k for k in result.created if k.startswith("FIT-SOIL")} == {"FIT-SOIL", "FIT-SOIL#2", "FIT-SOIL#f1"}
    assert "FIT-LIGHT#2" in result.created  # the face-down plane

    readback = runner.verify(plan, result.created)
    assert readback["ok"], readback
    assert readback["elements"] == 5 + 1 + 3 + 1 + 3 + 1  # families + plane + soil + waste + conduit + circuit

    removal = runner.remove(result.created)
    assert removal["failedCount"] == 0
    assert revit.elements == before  # exactly what the run made is gone; the chair type and the wall stay


def test_types_already_there_are_used_and_not_recorded():
    revit = _revit()
    first = Runner(revit).build(_plan())
    again = Runner(revit).build(BuildPlan.model_validate({
        "ops": [_plan().model_dump(mode="json")["ops"][0]], "existing_levels": {"L1": 3000.0}}))
    assert again.status == "built" and again.created == {}  # all already there: nothing to remove later
    assert first.created


def test_a_type_missing_from_the_template_is_a_question_and_keeps_what_was_copied():
    revit = _revit()
    data = _plan().model_dump(mode="json")
    data["ops"][0]["items"].append({"kind": "family_type", "family": "M_Sofa", "name": "3 seat", "category": "OST_Furniture"})
    result = Runner(revit).build(BuildPlan.model_validate(data))
    assert result.status == "needs_input" and result.error_code == "TYPE_NOT_IN_FILE"
    assert result.failed_key == "FIT-TYPES-RES" and "M_Sofa: 3 seat" in result.error
    assert result.created  # the others were copied: recorded, so removing the run removes them


def test_a_size_copy_the_family_will_not_take_is_a_question():
    revit = _revit()
    data = _plan().model_dump(mode="json")
    data["ops"][1]["values_mm"] = {"Height": 600}
    result = Runner(revit).build(BuildPlan.model_validate(data))
    assert result.status == "needs_input" and result.error_code == "PARAMETER_NOT_FOUND"
    assert result.failed_key == "FIT-TYPE-BED-1800"


def test_a_size_copy_of_a_project_type_finds_its_id_in_the_project():
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "M_Table", "type": "1200x600",
                                "dims": {"width": 1200, "depth": 600}}])
    plan = BuildPlan.model_validate({"ops": [
        {"op": "family_type_copy", "key": "T", "category": "OST_Furniture", "family": "M_Table", "base_type": "1200x600",
         "name": "1000x500 (Plinth)", "values_mm": {"Width": 1000, "Depth": 500}}]})
    result = Runner(revit).build(plan)
    assert result.status == "built" and list(result.created) == ["T"]
    assert revit.elements[result.created["T"]]["dims"] == {"width": 1000, "depth": 500}
    # Built again: the name is taken (by that copy), so it is used as it is and not recorded a second time.
    assert Runner(revit).build(plan).created == {}
