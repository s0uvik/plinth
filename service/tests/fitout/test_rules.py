"""Fit-out rules (F2): rule sets per location profile, validated, inherited, every value sourced and unverified."""

import shutil
from pathlib import Path

import pytest

from b2m.cad.project import Location, LocationRequired
from b2m.fitout import rules

RULESETS = Path(rules.__file__).parent / "rulesets"


def test_every_rule_set_loads_and_every_value_has_a_source_and_is_unverified():
    loaded = rules.load_all()
    assert set(loaded) == {"india-nbc2016", "west-bengal"}
    for rs in loaded.values():
        rows = rs.listing()
        assert len(rows) > 500
        assert all(r.source for r in rows)
        assert not any(r.verified for r in rows)  # placeholders until checked against the official texts
        paths = {r.path for r in rows}
        assert {"furniture.layout.circulation_mm.value", "plumbing.water.lpcd", "fire.bands[0].max_height_m",
                "electrical.switchboard.height_mm", "hvac.split_ac.sizing[1].tr"} <= paths


def test_location_first_then_its_profile_picks_the_rule_set():
    with pytest.raises(LocationRequired):
        rules.for_location(None)
    wb = rules.for_location(Location(city="Kolkata", state="West Bengal"))
    assert wb.id == "west-bengal" and wb.extends == "india-nbc2016"
    assert rules.for_location(Location(city="Pune", state="Maharashtra")).id == "india-nbc2016"
    with pytest.raises(rules.RulesRequired):
        rules.for_location(Location(city="Dhaka"), country="Bangladesh")
    with pytest.raises(rules.RulesRequired):
        rules.load("nowhere")


def test_a_state_rule_set_inherits_the_national_one_and_adds_its_authorities():
    national, wb = rules.load("india-nbc2016"), rules.load("west-bengal")
    assert national.electrical.supply_authority is None and national.fire.authority is None
    assert "CESC" in wb.electrical.supply_authority.value and "West Bengal" in wb.fire.authority.value
    assert wb.furniture == national.furniture and wb.plumbing == national.plumbing
    assert wb.profiles == ["west-bengal"]


def test_inheritance_merges_mappings_and_null_removes(tmp_path):
    root = tmp_path / "rulesets"
    shutil.copytree(RULESETS / "india-nbc2016", root / "india-nbc2016")
    (root / "local").mkdir()
    (root / "local" / "profile.yaml").write_text("id: local\nname: Local\nextends: india-nbc2016\nprofiles: [x]\n")
    (root / "local" / "plumbing.yaml").write_text(
        "plumbing:\n  water: {lpcd: 150, source: 'local bye-law'}\n  classes: {balcony: null}\n")
    local = rules.load_all(root)["local"]
    assert local.plumbing.water.lpcd == 150 and local.plumbing.water.flushing_lpcd == 45
    assert local.plumbing.water.source == "local bye-law"
    assert "balcony" not in local.plumbing.classes and "toilet" in local.plumbing.classes
    (root / "local" / "furniture.yaml").write_text("furniture:\n  sets:\n    bedroom:\n      items: [{item: piano}]\n")
    with pytest.raises(ValueError, match="unknown item 'piano'"):
        rules.load_all(root)


@pytest.mark.parametrize("name, classes", [
    ("BED ROOM", ["bedroom"]), ("Bedroom 2", ["bedroom"]), ("MASTER BED ROOM", ["bedroom"]),
    ("LIVING / DINING / KITCHEN", ["living", "dining", "kitchen"]), ("Living/Dining", ["living", "dining"]),
    ("DRAWING CUM DINING", ["living", "dining"]), ("Kitchen", ["kitchen"]), ("TOILET", ["toilet"]),
    ("W.C.", ["toilet"]), ("Toilet 1", ["toilet"]), ("BALCONY", ["balcony"]), ("VERANDAH", ["balcony"]),
    ("LOBBY", ["lobby"]), ("Stair & Lobby", ["stair", "lobby"]), ("LIFT", ["lift"]),
    ("LIFT ROOM", ["lift_machine_room"]), ("MUMTY ROOM", ["mumty"]), ("OPEN TERRACE", ["terrace"]),
    ("PUJA", ["puja"]), ("DUCT", ["shaft"]), ("DRESS", ["dress"]), ("STORE", ["store"]), ("WASH", ["utility"]),
    ("FOO", []), ("", []),
])
def test_room_classes_from_names(name, classes):
    assert rules.load("india-nbc2016").classify(name) == classes


def test_furniture_sets_merge_for_combined_rooms():
    rs = rules.load("india-nbc2016")
    living_only = {e.item: e for e in rs.set_for(["living"])}
    assert living_only["dining_set"].unless_flat_has == ["dining"]
    merged = rs.set_for(["living", "dining", "kitchen"])
    assert [e.item for e in merged] == ["counter", "sofa", "tv_unit", "dining_set", "fridge"]  # by priority
    assert {e.item: e for e in merged}["dining_set"].unless_flat_has == []  # the room is the dining room
    bedroom = rs.set_for(["bedroom"])
    assert [(e.item, e.optional) for e in bedroom] == [("bed", False), ("wardrobe", False), ("side_table", False),
                                                       ("study_table", True)]


def test_mep_tables_answer_what_f4_asks():
    rs = rules.load("india-nbc2016")
    bedroom = rs.electrical.points["bedroom"]
    assert bedroom.lights.number(12.0) == 2 and bedroom.fans.number(12.0) == 1 and bedroom.sockets_16a.number(9) == 1
    assert rs.electrical.points["living"].lights.number(30.0) == 4  # one per 8 m², rounded up
    assert rs.electrical.switchboard.side == "latch" and rs.electrical.switchboard.height_mm == 1200
    assert rs.hvac.split_ac.tr_for(10.5) == 1.0 and rs.hvac.split_ac.tr_for(15) == 1.5
    assert rs.hvac.split_ac.tr_for(40) is None  # beyond the table: asked
    assert set(rs.hvac.exhaust) == {"toilet", "kitchen"}
    assert rs.plumbing.classes["toilet"].fixtures == ["wc", "basin", "shower", "floor_trap"]
    assert {f.furniture_item for f in rs.plumbing.fixtures.values() if f.furniture_item} <= set(rs.furniture.items)
    water = rs.plumbing.water
    assert water.lpcd * water.persons_per_flat == 675  # litres per flat per day
    assert rs.fire.band(13.5).provisions == ["extinguishers", "exit_signage"]
    assert "wet_riser" in rs.fire.band(20).provisions and rs.fire.band(100).max_height_m is None
