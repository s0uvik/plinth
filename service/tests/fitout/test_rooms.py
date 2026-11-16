"""The room model (F2) from a build plan alone: class, polygon from wall faces, faces, doors, windows, flats.

On the G+4 sample every room of every storey gets its polygon, and its area matches the area drawn (the self-check's
oracle: the drawn boundary's area, within the self-check's area tolerance). Small synthetic plans pin the geometry
conventions: faces anticlockwise with the room on their left, door swing and hinge from the flips as to_plan writes
them, thickness from the type, questions for what the plan does not tell.
"""

from collections import Counter

import pytest
from shapely.geometry import Point, Polygon

from b2m.fitout import rooms
from b2m.imported.selfcheck import Tolerances, polygon_area_m2
from tests.fitout.conftest import box_walls, plan_of

TYPICAL = "2.PLAN 1st to 4th.dxf"


@pytest.fixture(scope="module")
def sample_model(sample_import, wb_rules):
    building, result = sample_import
    return building, result, rooms.build(result.plan, wb_rules)


def test_every_sample_room_gets_its_polygon_and_drawn_area(sample_model):
    building, result, model = sample_model
    # 29 rooms per typical storey: each flat's kitchen is a room of its own beside its living/dining (2026-11-17)
    assert len(model.rooms) == 121 and all(r.status == "ok" for r in model.rooms)
    assert Counter(r.level for r in model.rooms) == {"Ground Floor": 1, "Floor 1": 29, "Floor 2": 29, "Floor 3": 29,
                                                     "Floor 4": 29, "Roof": 4}
    recognised = {(p.sheet, r.id): (p, r) for p in building.plans for r in p.rooms}
    grouped = {(p.sheet, h) for p in building.plans for f in p.findings if f.topic == "rooms_share_space"
               for h in f.evidence.handles}
    tol = Tolerances()
    compared = 0
    for room in model.rooms:
        src = result.sources[room.key]
        plan_rec, rec = recognised[(src.sheet, src.element)]
        ring = Polygon(room.polygon)
        assert ring.is_valid and ring.exterior.is_ccw
        assert room.area_m2 == pytest.approx((ring.area - sum(Polygon(h).area for h in room.holes)) / 1e6, abs=1e-3)
        in_group = any((src.sheet, h) in grouped for h in rec.evidence.handles)
        if in_group or not rec.boundary:
            continue  # rooms answered "one room": no single drawn boundary (the self-check skips them too)
        drawn = rec.area_m2 if rec.area_m2 is not None else polygon_area_m2(rec.boundary)
        # the drawn boundary is the outer ring; Revit (and the model) also take out an enclosed duct (roof terraces)
        assert ring.area / 1e6 == pytest.approx(drawn, abs=tol.area(drawn)), room.key
        compared += 1
    assert compared == 85  # 21 rooms per typical storey, the ground toilet, the 4 roof rooms
    terraces = [r for r in model.rooms if r.name == "OPEN TERRACE"]
    assert [len(t.holes) for t in terraces] == [1, 1]  # a duct's walls stand in each; Revit read 235.13 m² live


def test_typical_storeys_are_alike_and_classed(sample_model):
    _, _, model = sample_model
    by_storey = {n: {r.key.rsplit("-", 1)[0]: r for r in model.on_level(f"Floor {n}")} for n in range(1, 5)}
    for n in (2, 3, 4):
        assert {k: (r.polygon, r.classes, r.flat, len(r.openings)) for k, r in by_storey[n].items()} == \
               {k: (r.polygon, r.classes, r.flat, len(r.openings)) for k, r in by_storey[1].items()}
    floor1 = by_storey[1].values()
    assert Counter(r.cls for r in floor1) == {"bedroom": 12, "toilet": 8, "living": 4, "kitchen": 4, "lobby": 1}
    living = [r for r in floor1 if r.cls == "living"]
    assert all(r.classes == ["living", "dining"] for r in living)
    assert sorted(r.flat for r in living) == ["U1", "U2", "U3", "U4"]
    kitchens = [r for r in floor1 if r.cls == "kitchen"]
    assert all(r.classes == ["kitchen"] for r in kitchens) and sorted(r.flat for r in kitchens) == ["U1", "U2", "U3", "U4"]
    lobby = next(r for r in floor1 if r.cls == "lobby")
    assert (lobby.flat, lobby.flat_source) == (None, "common")  # opens into all four flats
    assert {r.flat_source for r in floor1 if r.cls != "lobby"} == {"plan"}
    roof = {r.name: r for r in model.on_level("Roof")}
    assert {r.flat_source for r in roof.values()} == {"common"}
    ground = model.on_level("Ground Floor")[0]
    assert (ground.cls, ground.flat, ground.flat_source) == ("toilet", None, None)  # no flats on that level: not asked
    assert model.questions == [] and model.assumptions == []
    assert model.flats() and sorted(model.flats()) == ["U1", "U2", "U3", "U4"]


def test_sample_doors_and_windows_on_their_rooms(sample_model):
    _, result, model = sample_model
    plan_doors = {op.key for op in result.plan.ops if op.op == "door"}
    seen: dict[str, list] = {}
    for r in model.rooms:
        for o in r.openings:
            seen.setdefault(o.key, []).append((r.key, o))
            assert o.face is not None and r.faces[o.face].wall_key == o.wall_key
            if o.kind == "door" and o.swings_in:
                swing = Polygon(o.swing)
                assert swing.area == pytest.approx(3.1416 * o.width_mm ** 2 / 4, rel=0.02)
                assert r.shape().buffer(1).contains(swing)  # the leaf sweeps inside its room
    assert plan_doors <= set(seen)  # every door opens into or out of some room
    for key, views in seen.items():
        if views[0][1].kind == "door" and not views[0][1].sliding:
            assert sum(o.swings_in for _, o in views) <= 1  # a leaf swings into one room at most
            if len(views) == 2:
                (a, oa), (b, ob) = views
                assert oa.other_room == b and ob.other_room == a
                assert oa.hinge != ob.hinge and Point(oa.hinge).distance(Point(ob.hinge)) == pytest.approx(
                    next(f.thickness_mm for f in model.by_key()[a].faces if f.wall_key == oa.wall_key))
    bedrooms = [r for r in model.on_level("Floor 1") if r.cls == "bedroom"]
    assert all(r.doors and r.windows for r in bedrooms)
    assert all(any(f.outer for f in r.faces) for r in bedrooms)  # every bedroom has an outside wall
    w = next(o for r in bedrooms for o in r.windows if o.type == "W")
    assert (w.width_mm, w.sill_mm, w.head_mm) == (1500, 900, 2100)


def test_sample_kitchens_are_rooms_of_their_own(sample_model):
    """Each flat's kitchen (its own room since 2026-11-17) is closed off its living/dining by the separation line
    the build plan draws across the alcove's mouth: one separator face each side, neighbours of each other, and the
    two together fill the space the drawing leaves open (46.77 / 47.02 m2 recognised)."""
    building, _, model = sample_model
    floor1 = {r.key: r for r in model.on_level("Floor 1")}
    kitchens = [r for r in floor1.values() if r.cls == "kitchen"]
    assert len(kitchens) == 4
    for k in kitchens:
        ld = next(floor1[n] for n in k.neighbours if floor1[n].cls == "living")
        assert ld.flat == k.flat and k.key in ld.neighbours
        assert [f.kind for f in k.faces].count("separator") == 1 and [f.kind for f in ld.faces].count("separator") == 1
        assert k.area_m2 == pytest.approx(7.37, abs=0.01)  # NBC 2016's least kitchen is 5.0 m2
        assert k.windows  # light and air for its exhaust and fittings
        space = next(r.shared_space for p in building.plans for r in p.rooms if r.shared_space
                     and Polygon(r.shared_space).contains(Point(k.point)))
        assert k.area_m2 + ld.area_m2 == pytest.approx(Polygon(space).area / 1e6, abs=0.15)


# ------------------------------------------------------------------------------------------------ synthetic

def two_rooms(door: dict | None = None, **extra):
    """Rooms A [0,4000]x[0,3000] and B [4000,8000]x[0,3000] on 200 mm walls; the shared wall X runs up x=4000."""
    walls = [{"op": "wall", "key": "S", "type": "Brick 200", "start": (0, 0), "end": (8000, 0)},
             {"op": "wall", "key": "E", "type": "Brick 200", "start": (8000, 0), "end": (8000, 3000)},
             {"op": "wall", "key": "N", "type": "Brick 200", "start": (8000, 3000), "end": (0, 3000)},
             {"op": "wall", "key": "W", "type": "Brick 200", "start": (0, 3000), "end": (0, 0)},
             {"op": "wall", "key": "X", "type": "Brick 200", "start": (4000, 0), "end": (4000, 3000)}]
    ops = [*walls, {"op": "room", "key": "A", "level": "L0", "point": (2000, 1500), "name": extra.get("a", "Bedroom")},
           {"op": "room", "key": "B", "level": "L0", "point": (6000, 1500), "name": extra.get("b", "Toilet")}]
    if door:
        ops.append({"op": "door", "key": "D", "host_key": "X", "type": "Door 900x2100", "offset_mm": 1500, **door})
    ops += extra.get("more", [])
    return plan_of(*ops)


def test_polygon_from_wall_faces_and_faces_anticlockwise(wb_rules):
    model = rooms.build(two_rooms(), wb_rules)
    a, b = model.by_key()["A"], model.by_key()["B"]
    assert a.polygon == [(100, 100), (3900, 100), (3900, 2900), (100, 2900)]
    assert a.area_m2 == pytest.approx(10.64) and b.area_m2 == pytest.approx(10.64)
    assert [(f.wall_key, f.side, f.outer) for f in a.faces] == [
        ("S", "left", True), ("X", "left", False), ("N", "left", True), ("W", "left", True)]
    # the outer walls run anticlockwise round the block, so the rooms are on their left; X runs up: A is on its left
    assert all(f.kind == "wall" and f.thickness_mm == 200 for f in a.faces)
    assert [(f.wall_key, f.side) for f in b.faces][1:] == [("E", "left"), ("N", "left"), ("X", "right")]
    assert a.faces[0].inward == pytest.approx((0, 1))
    assert (a.height_mm, a.elevation_mm) == (3000, 0)


@pytest.mark.parametrize("flip_facing, flip_hand, into, hinge_y", [
    (False, False, "B", 1950),  # unflipped: opens to the wall's right (x > 4000), hinge at the end-side jamb
    (True, False, "A", 1950),
    (False, True, "B", 1050),
    (True, True, "A", 1050),
])
def test_door_swing_and_hinge_from_the_flips(wb_rules, flip_facing, flip_hand, into, hinge_y):
    model = rooms.build(two_rooms({"flip_facing": flip_facing, "flip_hand": flip_hand}), wb_rules)
    views = {r.key: r.doors[0] for r in model.rooms}
    assert {k: v.swings_in for k, v in views.items()} == {"A": into == "A", "B": into == "B"}
    door = views[into]
    x = 3900 if into == "A" else 4100
    assert door.hinge == (x, hinge_y) and door.latch == (x, 3000 - hinge_y)
    assert door.width_mm == 900 and door.height_mm == 2100 and door.other_room == ("B" if into == "A" else "A")
    swing = Polygon(door.swing)
    assert swing.bounds == pytest.approx((min(x, x + (900 if into == "B" else -900)), min(hinge_y, 3000 - hinge_y),
                                          max(x, x + (900 if into == "B" else -900)), max(hinge_y, 3000 - hinge_y)))
    assert model.by_key()["A"].neighbours == ["B"]


def test_sliding_door_and_window(wb_rules):
    plan = two_rooms(more=[
        {"op": "opening_type", "key": "T-sl", "name": "SL", "category": "door", "width_mm": 1200, "height_mm": 2100,
         "hint": "sliding"},
        {"op": "door", "key": "D2", "host_key": "X", "type": "SL", "offset_mm": 1500},
        {"op": "window", "key": "W1", "host_key": "S", "type": "Window 1200x1200", "offset_mm": 2000, "sill_mm": 900}])
    model = rooms.build(plan, wb_rules)
    a = model.by_key()["A"]
    d = a.doors[0]
    assert d.sliding and d.hinge is None and d.swing is None and d.width_mm == 1200
    w = a.windows[0]
    assert (w.width_mm, w.sill_mm, w.head_mm, w.center, w.face) == (1200, 900, 2100, (2000, 100), 0)
    assert a.faces[0].outer and not a.faces[1].outer


def test_thickness_from_the_type_op_or_name_else_asked(wb_rules):
    typed = plan_of({"op": "wall_type", "key": "T", "name": "Plaster wall", "thickness_mm": 300, "function": "interior"},
                    *box_walls("w", 0, 0, 3000, 3000, "Plaster wall"),
                    {"op": "room", "key": "R", "level": "L0", "point": (1500, 1500), "name": "Store"})
    assert rooms.build(typed, wb_rules).rooms[0].area_m2 == pytest.approx(2.7 * 2.7)
    unknown = plan_of(*box_walls("w", 0, 0, 3000, 3000, "Partition"),
                      {"op": "room", "key": "R", "level": "L0", "point": (1500, 1500), "name": "Store"})
    model = rooms.build(unknown, wb_rules)
    assert model.rooms[0].status == "waiting" and model.rooms[0].polygon is None
    assert [q.id for q in model.questions] == ["fitout:wall_thickness:Partition"]
    answered = rooms.build(unknown, wb_rules, {"fitout:wall_thickness:Partition": 100})
    assert answered.rooms[0].area_m2 == pytest.approx(2.9 * 2.9) and answered.questions == []
    assert rooms.type_thickness("B2M 250mm Exterior") == 250 and rooms.type_thickness("Brick 115") == 115
    assert rooms.type_thickness("Generic") is None and rooms.type_size("door-1025-sliding") == (1025, None)


def test_unknown_name_is_asked_once_and_answered(wb_rules):
    plan = two_rooms(a="Den", b="Den 2")
    model = rooms.build(plan, wb_rules)
    assert [(q.id, q.instances) for q in model.questions] == [("fitout:room_class:den", ["A", "B"])]
    assert model.by_key()["A"].classes == []
    model = rooms.build(plan, wb_rules, {"fitout:room_class:den": "study"})
    assert [r.classes for r in model.rooms] == [["study"], ["study"]] and model.questions == []


def test_unknown_door_width_is_assumed_from_the_rules_and_asked(wb_rules):
    plan = two_rooms({"type": "D7"})
    model = rooms.build(plan, wb_rules)
    assert model.by_key()["A"].doors[0].width_mm == 1000 and model.by_key()["A"].doors[0].width_assumed
    assert [a.id for a in model.assumptions] == ["fitout:door_width:D7"]
    assert [q.id for q in model.questions] == ["fitout:door_width:D7"]


def test_separator_splits_a_space_and_open_or_unenclosed_rooms(wb_rules):
    sep = {"op": "room_separator", "key": "SEP", "level": "L0", "points": [(2000, 0), (2000, 3000)]}
    plan = plan_of(*box_walls("w", 0, 0, 4000, 3000), sep,
                   {"op": "room", "key": "A", "level": "L0", "point": (1000, 1500), "name": "Living"},
                   {"op": "room", "key": "B", "level": "L0", "point": (3000, 1500), "name": "Dining"})
    a, b = rooms.build(plan, wb_rules).rooms
    assert a.area_m2 + b.area_m2 == pytest.approx(3.8 * 2.8, abs=1e-3)
    assert [f.kind for f in a.faces].count("separator") == 1 and a.neighbours == ["B"]
    # without the line they share one space: both get it, the first furnishes it
    plan = plan_of(*box_walls("w", 0, 0, 4000, 3000),
                   {"op": "room", "key": "A", "level": "L0", "point": (1000, 1500), "name": "Living"},
                   {"op": "room", "key": "B", "level": "L0", "point": (3000, 1500), "name": "Dining"})
    a, b = rooms.build(plan, wb_rules).rooms
    assert (a.status, a.shared_with, b.shared_with) == ("shared", ["B"], ["A"])
    # a wall missing: not enclosed, no polygon
    open_plan = plan_of(*box_walls("w", 0, 0, 4000, 3000)[:3],
                        {"op": "room", "key": "A", "level": "L0", "point": (1000, 1500), "name": "Living"})
    assert rooms.build(open_plan, wb_rules).rooms[0].status == "not_enclosed"


def test_flats_from_the_plan_by_connection_or_asked(wb_rules):
    # flat F1: A (bedroom, flat in the plan) - door - B (toilet, none); C (lobby) opens into flats F1 and F2
    walls = [*box_walls("o", 0, 0, 12000, 3000),
             {"op": "wall", "key": "X1", "type": "Brick 200", "start": (4000, 0), "end": (4000, 3000)},
             {"op": "wall", "key": "X2", "type": "Brick 200", "start": (6000, 0), "end": (6000, 3000)},
             {"op": "wall", "key": "X3", "type": "Brick 200", "start": (8000, 0), "end": (8000, 3000)}]
    doors = [{"op": "door", "key": f"D{i}", "host_key": f"X{i}", "type": "Door 900x2100", "offset_mm": 1500}
             for i in (1, 2, 3)]
    def room(key, x, name, flat=None):
        return {"op": "room", "key": key, "level": "L0", "point": (x, 1500), "name": name, **({"flat": flat} if flat else {})}
    plan = plan_of(*walls, *doors, room("A", 2000, "Bedroom", "F1"), room("B", 5000, "Toilet"),
                   room("C", 7000, "Lobby"), room("D", 10000, "Bedroom", "F2"))
    model = rooms.build(plan, wb_rules)
    got = {r.key: (r.flat, r.flat_source) for r in model.rooms}
    assert got == {"A": ("F1", "plan"), "B": ("F1", "connected"), "C": (None, "common"), "D": ("F2", "plan")}
    # a room reaching no flat on a level with flats is asked, and the answer is used
    plan = plan_of(*walls, *doors[:1], room("A", 2000, "Bedroom", "F1"), room("B", 5000, "Store"),
                   room("C", 7000, "Store"), room("D", 10000, "Bedroom", "F2"))
    model = rooms.build(plan, wb_rules)
    assert [q.id for q in model.questions] == ["fitout:flat:C"] and model.questions[0].options == ["F1", "F2", "common"]
    assert rooms.build(plan, wb_rules, {"fitout:flat:C": "F2"}).by_key()["C"].flat == "F2"


def test_a_column_standing_out_of_its_wall_is_an_obstacle(wb_rules):
    plan = plan_of(*box_walls("w", 0, 0, 4000, 3000),
                   {"op": "column", "key": "C1", "level": "L0", "top_level": "L1", "point": (0, 0),
                    "type": "RCC Column 400 x 400"},
                   {"op": "column", "key": "C2", "level": "L0", "top_level": "L1", "point": (4000, 1500),
                    "type": "RCC Column 200 x 200"},  # inside its wall: no obstacle
                   {"op": "room", "key": "A", "level": "L0", "point": (2000, 1500), "name": "Bedroom"})
    room = rooms.build(plan, wb_rules).rooms[0]
    assert room.obstacles == [[(100, 100), (200, 100), (200, 200), (100, 200)]]
