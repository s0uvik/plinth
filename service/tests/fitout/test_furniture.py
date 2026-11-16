"""The furniture solver (F3): standard sets placed in each room by the rules, what does not fit listed with why.

On the G+4 sample: typical-floor bedrooms get bed, wardrobe and side table, kitchens a counter with sink and hob,
toilets WC, basin and shower; nothing overlaps, nothing stands in a door's swing, and the one item that does not fit
(the shower of the 3.3 m² ground-floor toilet) is listed with its reasons. Small synthetic rooms pin the conventions
(insertion at the back edge's middle, rotation for a family facing -Y) and the edge cases.
"""

import math
from collections import Counter

import pytest
from shapely.geometry import Point, Polygon

from b2m.fitout import furniture, rooms
from tests.fitout.conftest import box_walls, plan_of


@pytest.fixture(scope="module")
def sample_layout(sample_import, wb_rules):
    _, result = sample_import
    model = rooms.build(result.plan, wb_rules)
    return model, furniture.solve(model, wb_rules)


def test_sample_rooms_are_furnished_by_class(sample_layout):
    model, layout = sample_layout
    assert Counter(p.item for p in layout.placed) == {
        "bed": 48, "wardrobe": 48, "side_table": 48, "study_table": 48,  # 12 bedrooms per typical storey
        "wc": 33, "basin": 33, "shower": 32,  # 8 toilets per typical storey + the ground-floor toilet
        "counter": 32, "kitchen_sink": 16, "hob": 16, "fridge": 16,  # an L counter (2 legs) per flat
        "sofa": 16, "tv_unit": 16, "dining_set": 16}
    for room in model.rooms:
        items = Counter(p.item for p in layout.in_room(room.key))
        if room.cls == "bedroom":
            assert items["bed"] == items["wardrobe"] == items["side_table"] == 1, room.key
        if "kitchen" in room.classes:
            assert items["counter"] >= 1 and items["kitchen_sink"] == items["hob"] == 1, room.key
        if room.cls in ("lobby", "terrace", "mumty", "lift_machine_room"):
            assert not items  # nothing to furnish
    assert [(u.room_key, u.item, u.status) for u in layout.unplaced] == [
        ("1.GROUND FLOOR PLAN.dxf-room-1-0", "shower", "no_fit")]
    why = layout.unplaced[0].reason
    assert "900 x 900, 800 x 800" in why and "water closet" in why and "door" in why
    assert layout.skipped == []


def test_sample_layout_keeps_the_hard_rules(sample_layout, wb_rules):
    model, layout = sample_layout
    assert furniture.problems(layout, model, wb_rules) == []
    by_key = model.by_key()
    for p in layout.placed:
        room = by_key[p.room_key]
        for o in room.doors:  # nothing in a door's swing, nor its margin
            if o.swing:
                assert Polygon(p.footprint).intersection(Polygon(o.swing).buffer(99)).area < 1, (p.key, o.key)
    keys = [p.key for p in layout.placed]
    assert len(keys) == len(set(keys)) and all(k.startswith("FIT-") and "#" not in k for k in keys)


def test_sample_layout_is_deterministic_and_alike_on_typical_storeys(sample_layout, wb_rules):
    model, layout = sample_layout
    assert furniture.solve(model, wb_rules) == layout
    per_storey = {}
    for p in layout.placed:
        if p.level.startswith("Floor "):
            per_storey.setdefault(p.level, set()).add((p.key.rsplit("-", 3)[0].rsplit("-", 1)[0], p.item, tuple(p.footprint)))
    assert per_storey["Floor 2"] == per_storey["Floor 1"] == per_storey["Floor 4"]


def test_records_carry_what_f4_and_f5_need(sample_layout, wb_rules):
    model, layout = sample_layout
    sink = next(p for p in layout.placed if p.item == "kitchen_sink")
    counters = {p.key: p for p in layout.placed if p.item == "counter"}
    assert sink.host in counters and sink.elevation_mm == 850 and sink.category == "Plumbing Fixtures"
    assert Polygon(counters[sink.host].footprint).buffer(1).contains(Polygon(sink.footprint))
    bed = next(p for p in layout.placed if p.item == "bed")
    assert bed.category == "Furniture" and "bed" in bed.keywords and bed.source
    room = model.by_key()[bed.room_key]
    face = next(f for f in room.faces if f.wall_key == bed.wall_key and f.side == bed.wall_side)
    # insertion: the middle of the back edge, on the wall face; facing into the room; rotation = facing - 270
    assert Point(bed.insertion).distance(Polygon(room.polygon).exterior) < 0.2
    assert math.cos(math.radians(bed.facing_deg)) == pytest.approx(face.inward[0], abs=1e-6)
    assert math.sin(math.radians(bed.facing_deg)) == pytest.approx(face.inward[1], abs=1e-6)
    assert bed.rotation_deg == pytest.approx((bed.facing_deg - 270) % 360)
    fixtures = {f.furniture_item for f in wb_rules.plumbing.fixtures.values() if f.furniture_item}
    assert fixtures <= {p.item for p in layout.placed} | {"washing_machine"}  # F4 finds every fixture it plumbs


# ------------------------------------------------------------------------------------------------ synthetic

def room_plan(w: float, h: float, name: str, *extra, flat: str | None = None, door: dict | None = None):
    """One room w x h (centrelines, 200 mm walls) with a 900 door in its south wall 600 from the west end, opening in."""
    d = {"op": "door", "key": "D", "host_key": "wS", "type": "Door 900x2100", "offset_mm": 650, "flip_facing": True,
         **(door or {})}
    room = {"op": "room", "key": "R", "level": "L0", "point": (w / 2, h / 2), "name": name}
    if flat:
        room["flat"] = flat
    return plan_of(*box_walls("w", 0, 0, w, h), d, room, *extra)


def solve(plan, rules):
    model = rooms.build(plan, rules)
    layout = furniture.solve(model, rules)
    assert furniture.problems(layout, model, rules) == []
    return model, layout


def test_a_bedroom_gets_bed_side_table_wardrobe_and_study_table(wb_rules):
    model, layout = solve(room_plan(3800, 4200, "Bedroom"), wb_rules)
    got = {p.item: p for p in layout.placed}
    assert set(got) == {"bed", "wardrobe", "side_table", "study_table"} and layout.unplaced == []
    bed, table = got["bed"], got["side_table"]
    assert (bed.width_mm, bed.depth_mm, bed.size_index) == (1500, 2000, 0)
    assert Polygon(bed.footprint).distance(Polygon(table.footprint)) < 0.2  # beside the bed, backs flush
    assert bed.wall_key == table.wall_key and bed.facing_deg == table.facing_deg
    door = model.rooms[0].doors[0]
    assert door.swings_in and not any(Polygon(p.footprint).intersects(Polygon(door.swing).buffer(99))
                                      for p in layout.placed)


def test_what_does_not_fit_is_listed_never_forced(wb_rules):
    _, layout = solve(room_plan(1600, 1700, "Bedroom"), wb_rules)
    status = {u.item: (u.status, u.optional) for u in layout.unplaced}
    assert status["bed"] == ("no_fit", False) and status["side_table"] == ("anchor_missing", False)
    assert "bed" not in {p.item for p in layout.placed}
    assert all(u.reason for u in layout.unplaced)
    assert "1500 x 2000, 1200 x 2000, 900 x 2000" in next(u.reason for u in layout.unplaced if u.item == "bed")


def test_a_tall_item_keeps_clear_of_windows(wb_rules):
    # a store: the north wall is mostly window (sill 900), so the 2100 mm high shelves go on the east wall
    win = {"op": "window", "key": "W", "host_key": "wN", "type": "Window 1500x1200", "offset_mm": 1300, "sill_mm": 900}
    _, layout = solve(room_plan(2600, 2000, "Store", win), wb_rules)
    assert [(p.item, p.wall_key) for p in layout.placed] == [("shelf", "wE"), ("shelf", "wE")]
    # too small a store: nothing forced, the reasons name the window and the door
    win = {"op": "window", "key": "W", "host_key": "wN", "type": "Window 1000x1200", "offset_mm": 650, "sill_mm": 900}
    _, layout = solve(room_plan(1300, 1600, "Store", win, door={"offset_mm": 650}), wb_rules)
    assert layout.placed == [] and [u.item for u in layout.unplaced] == ["shelf", "shelf"]
    assert all("window" in u.reason and "door" in u.reason and u.optional for u in layout.unplaced)


def test_kitchen_counter_with_sink_and_hob(wb_rules):
    win = {"op": "window", "key": "W", "host_key": "wE", "type": "Window 1200x1200", "offset_mm": 1500,
           "sill_mm": 1050}
    _, layout = solve(room_plan(2800, 3200, "Kitchen", win), wb_rules)
    got = {p.item: p for p in layout.placed if p.item != "counter"}
    legs = [p for p in layout.placed if p.item == "counter"]
    assert len(legs) == 2 and all(p.depth_mm == 600 and p.height_mm == 850 for p in legs)  # an L at a corner
    assert [p.key for p in legs] == ["FIT-R-counter-1", "FIT-R-counter-2"]
    assert 1800 <= sum(p.width_mm for p in legs) <= 4200
    sink, hob = got["kitchen_sink"], got["hob"]
    assert sink.host in {p.key for p in legs} and hob.host in {p.key for p in legs}
    assert sink.wall_key == "wE"  # under the window
    assert Polygon(sink.footprint).distance(Polygon(hob.footprint)) >= 450 - 0.5
    assert {p.elevation_mm for p in (sink, hob)} == {850}


def test_living_room_sofa_faces_the_tv_and_dining_goes_to_the_dining_room(wb_rules):
    living = room_plan(4200, 5400, "Living", flat="A")
    model, layout = solve(living, wb_rules)
    got = {p.item: p for p in layout.placed}
    sofa, tv = got["sofa"], got["tv_unit"]
    assert (sofa.facing_deg - tv.facing_deg) % 360 == pytest.approx(180)
    clear = Polygon(sofa.footprint).distance(Polygon(tv.footprint))
    assert 1800 <= clear <= 4500
    assert "dining_set" in got
    # the same flat with a dining room: the living room's dining set is not needed (listed, not a failure)
    plan = plan_of(*box_walls("w", 0, 0, 4200, 5400), *box_walls("v", 4200, 0, 7400, 5400)[:3],
                   {"op": "door", "key": "D", "host_key": "wS", "type": "Door 900x2100", "offset_mm": 650,
                    "flip_facing": True},
                   {"op": "room", "key": "R", "level": "L0", "point": (2100, 2700), "name": "Living", "flat": "A"},
                   {"op": "room", "key": "Q", "level": "L0", "point": (5800, 2700), "name": "Dining", "flat": "A"})
    _, layout = solve(plan, wb_rules)
    living_items = {p.item for p in layout.in_room("R")}
    assert "dining_set" not in living_items and "dining_set" in {p.item for p in layout.in_room("Q")}
    assert [(u.room_key, u.item, u.status) for u in layout.unplaced] == [("R", "dining_set", "not_needed")]


def test_rooms_without_a_shape_or_a_class_are_skipped_with_why(wb_rules):
    plan = plan_of(*box_walls("w", 0, 0, 4000, 3000)[:3],
                   {"op": "room", "key": "A", "level": "L0", "point": (2000, 1500), "name": "Bedroom"},
                   *box_walls("v", 5000, 0, 9000, 3000),
                   {"op": "room", "key": "B", "level": "L0", "point": (7000, 1500), "name": "Den"},
                   *box_walls("u", 10000, 0, 14000, 3000),
                   {"op": "room", "key": "C", "level": "L0", "point": (11000, 1500), "name": "Living"},
                   {"op": "room", "key": "E", "level": "L0", "point": (13000, 1500), "name": "Dining"})
    model = rooms.build(plan, wb_rules)
    layout = furniture.solve(model, wb_rules)
    why = {s.room_key: s.reason for s in layout.skipped}
    assert set(why) == {"A", "B", "E"}
    assert "not enclosed" in why["A"] and "asked" in why["B"] and "furnished there" in why["E"]
    assert {p.item for p in layout.in_room("C")} >= {"sofa", "dining_set"}  # both rooms' sets, in the one space


def test_a_small_toilet_keeps_the_door_clear_and_says_what_is_left_out(wb_rules):
    # 1500 x 2400 inside, the door opening in from the middle of the short side
    _, layout = solve(room_plan(1700, 2600, "Toilet", door={"offset_mm": 850}), wb_rules)
    assert [p.item for p in layout.placed] == ["wc", "basin"]
    assert [(u.item, u.status) for u in layout.unplaced] == [("shower", "no_fit")]
    # the shower area may lie under the door's swing (open wet area): what keeps it out is the WC and basin
    assert "door's swing" not in layout.unplaced[0].reason and "water closet" in layout.unplaced[0].reason


def test_a_shower_area_may_lie_under_the_door_swing_but_not_in_its_approach(wb_rules):
    # 1800 x 2400 inside: with the leaf's swing kept clear there is no corner left for the shower area
    model, layout = solve(room_plan(2000, 2600, "Toilet", door={"offset_mm": 1000}), wb_rules)
    assert "shower" in [p.item for p in layout.placed]
    assert furniture.problems(layout, model, wb_rules) == []
