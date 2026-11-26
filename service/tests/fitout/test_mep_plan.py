"""The MEP layout as to_plan's records (F4 -> F5): devices placed by their host (a wall side towards the way they face,
facing down at the ceiling's height, on the floor), runs with their points above their level and their system as a
Revit classification, circuits of their board; every device category of the rules is one Revit knows."""

import pytest

from plinth.fitout import families, mep_plan
from plinth.fitout.mepcore import Circuit, Device, Host, MepLayout, Run
from plinth.fitout.rules import Size

LEVELS = {"L0": 0.0, "L1": 3000.0}


def device(**over) -> Device:
    data = dict(key="MEP-R1-socket_6a-1", room_key="R1", level="L1", flat="A", system="power", what="socket_6a",
                label="6 A socket", category="Electrical Fixtures", keywords=["receptacle", "socket"],
                insertion=(1000.0, 100.0), elevation_mm=300.0, facing_deg=90.0, rotation_deg=0.0,
                host=Host(kind="wall", wall_key="W-S", side="left", height_mm=300.0), load_w=100.0,
                source="IS 732 point rules")
    data.update(over)
    return Device(**data)


def run(**over) -> Run:
    data = dict(key="RUN-R1-waste-1", kind="pipe", system="waste", level="L1",
                points=[(1000.0, 500.0, 3450.0), (1000.0, 2000.0, 3420.0), (3000.0, 2000.0, 3420.0)], size_mm=40.0,
                slope_pct=2.0, connect_start="FIT-R1-basin-1", connect_end="RUN-S-waste_stack-1", source="NBC 2016",
                note="basin waste")
    data.update(over)
    return Run(**data)


def test_a_wall_device_faces_into_its_room_and_an_outdoor_unit_out_of_its_wall():
    socket = mep_plan.device_record(device())
    assert (socket["item"], socket["point"], socket["wall_key"], socket["toward"]) == (
        "socket_6a", (1000.0, 100.0), "W-S", (1000.0, 300.0))
    assert (socket["elevation_mm"], socket["rotation_deg"], socket["category"]) == (300.0, 0.0, "Electrical Fixtures")
    assert "face_down" not in socket and "width_mm" not in socket and "host" not in socket  # host: a counter fixture's
    # the outdoor unit stands outside the wall, facing out: its side point is further out still
    outdoor = mep_plan.device_record(device(
        what="ac_outdoor", system="hvac", label="Split AC outdoor unit", category="Mechanical Equipment",
        insertion=(1000.0, -560.0), facing_deg=270.0, rotation_deg=0.0, elevation_mm=1500.0, rating="1.5 TR",
        host=Host(kind="outside_wall", wall_key="W-S", side="right", height_mm=1800.0),
        size=Size(width_mm=850, depth_mm=320, height_mm=600), note="outdoor unit of MEP-R1-ac_indoor-1"))
    assert outdoor["toward"] == (1000.0, -760.0) and outdoor["wall_key"] == "W-S"
    assert (outdoor["width_mm"], outdoor["depth_mm"], outdoor["height_mm"]) == (850, 320, 600)
    assert outdoor["source"] == ("Split AC outdoor unit 1.5 TR: IS 732 point rules; outdoor unit of "
                                 "MEP-R1-ac_indoor-1")


def test_ceiling_devices_face_down_at_the_ceiling_and_floor_devices_stand_on_the_level():
    light = mep_plan.device_record(device(what="light", system="lighting", category="Lighting Fixtures",
                                          insertion=(2000.0, 2000.0), elevation_mm=2850.0, facing_deg=270.0,
                                          host=Host(kind="ceiling", height_mm=2850.0)))
    assert light["face_down"] is True and light["elevation_mm"] == 2850.0 and "wall_key" not in light
    tank = mep_plan.device_record(device(what="underground_tank", system="plumbing", category="Plumbing Equipment",
                                         room_key=None, insertion=(0.0, -3000.0), elevation_mm=-1800.0,
                                         host=Host(kind="floor"), load_w=None))
    assert "face_down" not in tank and "toward" not in tank and tank["elevation_mm"] == -1800.0
    assert tank["room_key"] is None


def test_runs_go_above_their_level_with_a_revit_classification():
    rec = mep_plan.run_record(run(), LEVELS)
    assert rec["points"] == [(1000.0, 500.0, 450.0), (1000.0, 2000.0, 420.0), (3000.0, 2000.0, 420.0)]
    assert (rec["system"], rec["diameter_mm"], rec["start_key"], rec["end_key"]) == (
        "Sanitary", 40.0, "FIT-R1-basin-1", "RUN-S-waste_stack-1")
    assert rec["source"] == "waste pipe; slope 2%; NBC 2016; basin waste"
    assert {s: mep_plan.CLASSIFICATION[s] for s in ("cold_water", "hot_water", "soil", "vent", "rainwater", "fire")} == {
        "cold_water": "DomesticColdWater", "hot_water": "DomesticHotWater", "soil": "Sanitary", "vent": "Vent",
        "rainwater": "OtherPipe", "fire": "FireProtectWet"}
    stack = mep_plan.run_record(run(level="L0", points=[(0.0, 0.0, -600.0), (0.0, 0.0, 15000.0)], connect_start=None,
                                    connect_end=None, slope_pct=None, note=None), LEVELS)
    assert stack["points"] == [(0.0, 0.0, -600.0), (0.0, 0.0, 15000.0)] and "start_key" not in stack
    conduit = mep_plan.run_record(run(kind="conduit", system="power", size_mm=20.0, slope_pct=None), LEVELS)
    assert "system" not in conduit and conduit["diameter_mm"] == 20.0
    with pytest.raises(ValueError, match="levels the building does not have: L9"):
        mep_plan.records(MepLayout(rule_set="x", runs=[run(level="L9")]), LEVELS)


def test_circuits_and_systems():
    c = Circuit(key="CKT-DB-1", panel="MEP-DB-1", kind="sockets", devices=["MEP-R1-socket_6a-1"], load_w=1200,
                rating_a=16, source="IS 732")
    rec = mep_plan.circuit_record(c)
    assert rec == {"key": "CKT-DB-1", "device_keys": ["MEP-R1-socket_6a-1"], "panel_key": "MEP-DB-1",
                   "system_type": "PowerCircuit", "source": "sockets circuit, 1200 W, 16 A: IS 732"}
    layout = MepLayout(rule_set="x", devices=[device(), device(key="MEP-T", system="fire", what="extinguisher")],
                       runs=[run(), run(key="RUN-C", kind="conduit", system="lighting")], circuits=[c])
    assert mep_plan.system_of(layout) == {"MEP-R1-socket_6a-1": "electrical", "MEP-T": "fire",
                                          "RUN-R1-waste-1": "plumbing", "RUN-C": "electrical", "CKT-DB-1": "electrical"}
    assert mep_plan.assumptions(layout) == []
    rain = MepLayout(rule_set="x", runs=[run(system="rainwater")])
    assert [a.id for a in mep_plan.assumptions(rain)] == ["fitout:rainwater_system"]


@pytest.mark.parametrize("rule_set", ["west-bengal", "india-nbc2016"])
def test_every_device_category_of_the_rules_is_a_revit_category(rule_set):
    from plinth.fitout import rules
    r = rules.load(rule_set)
    for cat in (r.electrical.devices, r.plumbing.devices, r.hvac.devices, r.fire.devices):
        for what, spec in cat.items():
            assert families.builtin_category(spec.category), (what, spec.category)
    assert families.builtin_category("Fire Protection") == "OST_FireProtection"
