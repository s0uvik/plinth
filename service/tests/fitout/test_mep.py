"""The MEP layout (F4): plumbing and drainage, electrical, HVAC and fire laid out by rules on the rooms and furniture.

On the G+4 sample (stilt + 4 floors of 4 flats, two ducts, two roof terraces, 13.5 m): every WC drains to a soil stack
in a duct, every fixture is fed, every bedroom gets a split AC on an outside wall with its outdoor unit outside, every
electrical load is on a circuit of its flat's board or the common board, the fire band below 15 m gives extinguishers
and exit signs, and what the plan does not tell (meter room, underground tank, the large living rooms' AC, the stilt
floor's extinguishers) is asked; the audit finds nothing. A small synthetic flat (bedroom, kitchen, toilet beside a
duct, a stair, a roof terrace) pins the conventions: switchboard at the latch side at 1200 mm, the tees beside their
mains, the answers that place the underground tank and set a taller building's fire band.
"""

import math
from collections import Counter

import pytest
from shapely.geometry import Point, Polygon

from b2m.fitout import furniture, mep, rooms
from b2m.fitout.mepcore import TEE_GAP_MM
from tests.fitout.conftest import box_walls, plan_of


@pytest.fixture(scope="module")
def sample_mep(sample_import, wb_rules):
    _, result = sample_import
    model = rooms.build(result.plan, wb_rules)
    lay = furniture.solve(model, wb_rules)
    kinds = mep.shaft_kinds_from_sources(result.sources)
    return result, model, lay, mep.layout(result.plan, model, lay, wb_rules, shaft_kinds=kinds)


def drains_to_stack(layout, key: str, system: str) -> str | None:
    """The stack a fixture's drain reaches, following its runs (fixture -> branch -> stack)."""
    runs = {r.key: r for r in layout.runs}
    run = next((r for r in layout.runs if r.connect_start == key and r.system == system), None)
    seen = 0
    while run is not None and seen < 5:
        if run.key.split("-")[-2] == f"{system}_stack":
            return run.key
        run = runs.get(run.connect_end)
        seen += 1
    return None


def test_sample_counts_per_system(sample_mep):
    _, _, _, layout = sample_mep
    assert Counter(d.what for d in layout.devices) == {
        "socket_6a": 370, "light": 253, "socket_16a": 145, "fan": 112, "switchboard": 119, "tv_outlet": 64,
        "exhaust_fan": 49, "geyser": 49, "ac_indoor": 48, "ac_outdoor": 48, "floor_trap": 33, "exit_sign": 9,
        "distribution_board": 17, "data_outlet": 16, "extinguisher": 15, "roof_outlet": 12, "overhead_tank": 2}
    assert Counter((r.kind, r.system) for r in layout.runs) == {
        ("conduit", "power"): 639, ("conduit", "lighting"): 533, ("pipe", "cold_water"): 182, ("pipe", "waste"): 133,
        ("pipe", "hot_water"): 113, ("pipe", "soil"): 51, ("pipe", "rainwater"): 12, ("pipe", "vent"): 2}
    assert Counter(c.kind for c in layout.circuits) == {"dedicated": 113, "lighting": 52, "sockets": 49, "power": 32}
    stacks = sorted(r.key.split("-")[-2] for r in layout.runs if r.key.split("-")[-2].endswith("_stack"))
    assert stacks == ["soil_stack", "soil_stack", "vent_stack", "vent_stack", "waste_stack", "waste_stack"]
    assert layout.notice == mep.NOTICE and "indicative" in layout.notice.lower()


def test_sample_layout_keeps_the_rules(sample_mep):
    _, model, lay, layout = sample_mep
    assert mep.problems(layout, model, lay) == []
    keys = [d.key for d in layout.devices] + [r.key for r in layout.runs] + [c.key for c in layout.circuits]
    assert len(keys) == len(set(keys))
    assert all(k.startswith("MEP-") for k in keys[:len(layout.devices)])
    assert all(2 <= len(r.points) <= 200 for r in layout.runs)
    assert all(r.connect_start is None or r.connect_start.startswith("MEP-") for r in layout.runs
               if r.kind == "conduit") and all(r.connect_end is None or r.connect_end.startswith("MEP-")
                                               for r in layout.runs if r.kind == "conduit")


def test_every_wc_drains_to_a_soil_stack_and_every_fixture_is_fed(sample_mep):
    _, _, lay, layout = sample_mep
    wcs = [p for p in lay.placed if p.item == "wc"]
    assert len(wcs) == 33
    for wc in wcs:
        assert drains_to_stack(layout, wc.key, "soil"), wc.key
        assert any(r.connect_end == wc.key and r.system == "cold_water" for r in layout.runs), wc.key
    for p in lay.placed:
        if p.item in ("basin", "shower", "kitchen_sink"):
            assert drains_to_stack(layout, p.key, "waste"), p.key
            assert any(r.connect_end == p.key and r.system == "hot_water" for r in layout.runs), p.key
    # drains fall towards their stack; the branches tee in beside it, square to the duct's row of pipes
    for r in layout.runs:
        if r.system in ("soil", "waste") and not r.key.split("-")[-2].endswith("_stack"):
            zs = [p[2] for p in r.points[1:]]
            assert all(a >= b - 1e-6 for a, b in zip(zs, zs[1:])), r.key


def test_every_load_is_on_a_circuit_of_its_board(sample_mep):
    _, _, _, layout = sample_mep
    devices = {d.key: d for d in layout.devices}
    on = Counter(k for c in layout.circuits for k in c.devices)
    loads = [d for d in layout.devices if d.load_w is not None]
    assert loads and all(on[d.key] == 1 for d in loads)
    for c in layout.circuits:
        panel = devices[c.panel]
        assert panel.what == "distribution_board" and all(devices[k].flat == panel.flat for k in c.devices)
        if c.kind == "lighting":
            assert len(c.devices) <= 10 and c.load_w <= 800
        if c.kind == "power":
            assert len(c.devices) <= 2
    dbs = [d for d in layout.devices if d.what == "distribution_board"]
    assert sorted((d.level, d.flat) for d in dbs if d.flat) == sorted(
        (f"Floor {n}", f"U{u}") for n in range(1, 5) for u in range(1, 5))
    acs = [c for c in layout.circuits if any(devices[k].what == "ac_indoor" for k in c.devices)]
    assert len(acs) == 48 and all(c.kind == "dedicated" and len(c.devices) == 2 for c in acs)


def test_every_bedroom_has_an_ac_on_an_outside_wall(sample_mep):
    _, model, _, layout = sample_mep
    by_key = model.by_key()
    for room in model.rooms:
        if room.cls != "bedroom":
            continue
        units = [d for d in layout.in_room(room.key) if d.what == "ac_indoor"]
        assert len(units) == 1, room.key
        u = units[0]
        face = next(f for f in room.faces if (f.wall_key, f.side) == (u.host.wall_key, u.host.side)
                    and Point(u.insertion).distance(Polygon([f.start, f.end, f.end]).exterior) < 1)
        assert face.outer and u.host.height_mm == 2100 + 320 / 2 and u.rating in ("1.5 TR", "1 TR", "2 TR")
        out = next(d for d in layout.in_room(room.key) if d.what == "ac_outdoor")
        assert out.host.kind == "outside_wall" and out.host.wall_key == u.host.wall_key
        assert out.host.side != u.host.side and not room.shape().covers(Point(out.insertion))
        assert math.dist(out.insertion, u.insertion) < 1000
        sockets = [d for d in layout.in_room(room.key) if d.what == "socket_16a"]
        assert any(math.dist(s.insertion, u.insertion) < 1500 and s.elevation_mm == 2100 for s in sockets)
    assert sum(1 for d in layout.devices if d.what == "ac_indoor") == 48
    large = [u for u in layout.unplaced if u.what == "ac_indoor"]
    assert len(large) == 16 and all(by_key[u.room_key].classes == ["living", "dining"] for u in large)  # kitchen split off


def test_switchboards_doors_and_ceiling_points(sample_mep):
    _, model, _, layout = sample_mep
    by_key = model.by_key()
    for d in layout.devices:
        if d.what == "switchboard":
            assert d.host.kind == "wall" and d.elevation_mm == 1200
        if d.what in ("light", "fan"):
            room = by_key[d.room_key]
            assert d.host.kind == "ceiling" and d.elevation_mm == room.height_mm - 150
    # a bedroom: two lights (14 m² at one per 8 m²), a fan off the lights, 3 + 1 sockets, a TV point
    bed = next(r for r in model.on_level("Floor 1") if r.cls == "bedroom" and r.area_m2 == 14.0)
    got = Counter(d.what for d in layout.in_room(bed.key))
    assert (got["light"], got["fan"], got["socket_6a"], got["socket_16a"], got["tv_outlet"]) == (2, 1, 3, 1, 1)


def test_fire_band_at_13_5_m_gives_its_provisions(sample_mep, wb_rules):
    _, model, _, layout = sample_mep
    assert layout.height_m == 13.5 and layout.fire_band_max_m == 15
    assert [p.id for p in layout.provisions] == wb_rules.fire.band(13.5).provisions == ["extinguishers",
                                                                                          "exit_signage"]
    assert all(p.status == "placed" and p.devices for p in layout.provisions)
    ext = Counter(d.level for d in layout.devices if d.what == "extinguisher")
    assert ext == {"Floor 1": 3, "Floor 2": 3, "Floor 3": 3, "Floor 4": 3, "Roof": 3}
    assert [(u.what, u.level) for u in layout.unplaced if u.what == "extinguisher"] == [
        ("extinguisher", "Ground Floor")] * 2
    assert all(by_lobby for by_lobby in (model.by_key()[d.room_key].flat is None for d in layout.devices
                                         if d.system == "fire"))
    # exit signs face the stairs arriving at and leaving each lobby (and the stair up into the mumty), not the lifts
    signs = Counter(d.level for d in layout.devices if d.what == "exit_sign")
    assert signs == {"Floor 1": 2, "Floor 2": 2, "Floor 3": 2, "Floor 4": 2, "Roof": 1}


def test_sample_asks_what_the_plan_does_not_tell(sample_mep):
    _, _, _, layout = sample_mep
    assert [q.id for q in layout.questions] == [
        "fitout:ac:living dining:39.4", "fitout:ac:living dining:39.6",  # kitchens are their own rooms
        "fitout:extinguisher_room:Ground Floor", "fitout:meter_room", "fitout:underground_tank"]
    assert all(q.text for q in layout.questions)
    assumed = {a.id: a for a in layout.assumptions}
    assert assumed["fitout:persons_per_flat"].value == 5 and "16 flats" in assumed["fitout:persons_per_flat"].text
    assert "fitout:indicative" in assumed and assumed["fitout:building_height"].value == 13.5
    assert all(a.source for a in layout.assumptions)
    w = layout.water
    assert (w.flats, w.persons, w.demand_lpd, w.underground_l) == (16, 80, 10800, 10800)
    assert sorted(w.overhead_l.values()) == [2700, 2700]  # one tank per terrace, each for the 8 flats it feeds
    assert sorted((u.what, u.reason.split(" (")[0].split(";")[0]) for u in layout.unrouted) == [
        ("electrical riser: meter board end", "the meter room is asked"),
        ("rising main", "from the pump at the underground tank")]
    assert Counter(u.what for u in layout.unplaced) == {
        "ac_indoor": 16, "extinguisher": 2, "switchboard": 2, "shower": 1, "underground_tank": 1, "pump": 1,
        "meter_board": 1}


def test_sample_layout_is_deterministic(sample_mep, wb_rules):
    result, model, lay, layout = sample_mep
    again = mep.layout(result.plan, model, lay, wb_rules, shaft_kinds=mep.shaft_kinds_from_sources(result.sources))
    assert again == layout


# ------------------------------------------------------------------------------------------------ synthetic

def flat_plan(*extra):
    """One flat on L0 in an 8 x 6 m box (200 mm walls): bedroom (west half), kitchen (south-east, the entrance door in
    the south wall), toilet beside a duct (north-east), a small stair room; a roof terrace on L1 within a parapet."""
    walls = [*box_walls("o", 0, 0, 8000, 6000),
             {"op": "wall", "key": "x4", "type": "Brick 200", "start": (4000, 0), "end": (4000, 6000)},
             {"op": "wall", "key": "y36", "type": "Brick 200", "start": (4000, 3600), "end": (8000, 3600)},
             {"op": "wall", "key": "x66", "type": "Brick 200", "start": (6600, 3600), "end": (6600, 6000)},
             {"op": "wall", "key": "y48", "type": "Brick 200", "start": (6600, 4800), "end": (8000, 4800)}]
    parapet = [{**w, "key": "p" + w["key"], "level": "L1", "top_level": None, "height_mm": 900}
               for w in box_walls("", 0, 0, 8000, 6000)]
    ops = [{"op": "door", "key": "D1", "host_key": "x4", "type": "Door 900x2100", "offset_mm": 2000},
           {"op": "door", "key": "D2", "host_key": "y36", "type": "Door 750x2100", "offset_mm": 1000},
           {"op": "door", "key": "D3", "host_key": "oS", "type": "Door 1000x2100", "offset_mm": 6000},
           {"op": "window", "key": "W1", "host_key": "oW", "type": "Window 1500x1200", "offset_mm": 3000,
            "sill_mm": 900},
           {"op": "room", "key": "BED", "level": "L0", "point": (2000, 3000), "name": "Bedroom", "flat": "A"},
           {"op": "room", "key": "KIT", "level": "L0", "point": (6000, 1800), "name": "Kitchen", "flat": "A"},
           {"op": "room", "key": "WC", "level": "L0", "point": (5300, 4800), "name": "Toilet", "flat": "A"},
           {"op": "room", "key": "DUCT", "level": "L0", "point": (7300, 5400), "name": "Duct"},
           {"op": "room", "key": "ST", "level": "L0", "point": (7300, 4200), "name": "Stair"},
           {"op": "room", "key": "TER", "level": "L1", "point": (2000, 3000), "name": "Terrace"}]
    return plan_of(*walls, *parapet, *ops, *extra)


def fit(rules, answers=None, *extra):
    plan = flat_plan(*extra)
    model = rooms.build(plan, rules)
    lay = furniture.solve(model, rules)
    return model, lay, mep.layout(plan, model, lay, rules, answers)


def test_a_small_flat_is_fitted_out_and_keeps_the_rules(wb_rules):
    model, lay, layout = fit(wb_rules)
    assert mep.problems(layout, model, lay) == []
    by_key = model.by_key()
    wc = next(p for p in lay.placed if p.item == "wc")
    assert drains_to_stack(layout, wc.key, "soil") == "RUN-DUCT-soil_stack-1"
    stack = layout.run("RUN-DUCT-soil_stack-1")
    duct = Polygon(by_key["DUCT"].polygon)
    assert duct.contains(Point(stack.points[0][:2])) and stack.points[0][:2] == stack.points[1][:2]
    assert stack.points[0][2] == -600 and stack.points[1][2] == 3000 + 1500  # under the floor to above the roof
    # the toilet's exhaust fan on the wall to the duct or outside; its geyser above the shower, beside it a socket
    fan = next(d for d in layout.in_room("WC") if d.what == "exhaust_fan")
    assert fan.host.kind == "wall" and fan.elevation_mm == 2100 + 75
    geyser = next(d for d in layout.in_room("WC") if d.what == "geyser")
    assert geyser.elevation_mm == 1800 + 550 / 2 and geyser.capacity_l == 25
    assert any(d.what == "socket_16a" and d.elevation_mm == 1800 for d in layout.in_room("WC"))
    # the bedroom's switchboard at the latch side of its door, 1200 mm up
    door = next(o for o in by_key["BED"].doors if o.key == "D1")
    sb = next(d for d in layout.in_room("BED") if d.what == "switchboard")
    assert sb.elevation_mm == 1200 and math.dist(sb.insertion, door.latch) < math.dist(sb.insertion, door.hinge)
    assert math.dist(sb.insertion, door.latch) == pytest.approx(150 + 250 / 2, abs=60)
    # the flat's board by the entrance door; the common board in the stair room; every load on a circuit
    db = next(d for d in layout.devices if d.what == "distribution_board" and d.flat == "A")
    assert db.room_key == "KIT" and db.elevation_mm == 1800
    common = next(d for d in layout.devices if d.what == "distribution_board" and d.flat is None)
    assert common.room_key == "ST"
    on = {k for c in layout.circuits for k in c.devices}
    assert all(d.key in on for d in layout.devices if d.load_w is not None)
    # the bedroom's AC on an outside wall, above the window's head at most
    ac = next(d for d in layout.in_room("BED") if d.what == "ac_indoor")
    face = next(f for f in by_key["BED"].faces if (f.wall_key, f.side) == (ac.host.wall_key, ac.host.side))
    assert face.outer and ac.rating == "2 TR"  # 3.8 x 5.8 m inside: 22 m², up to 23 m² for 2 TR
    # tees: every run joining a main ends beside it
    runs = {r.key: r for r in layout.runs}
    for r in layout.runs:
        for end, p in ((r.connect_start, r.points[0]), (r.connect_end, r.points[-1])):
            if end in runs:
                main = runs[end]
                d = min(_dist_to_segment(p, a, b) for a, b in zip(main.points, main.points[1:]))
                assert d == pytest.approx(TEE_GAP_MM, abs=5), (r.key, end)


def _dist_to_segment(p, a, b):
    d = [b[k] - a[k] for k in range(3)]
    L2 = sum(x * x for x in d) or 1
    t = max(0.0, min(1.0, sum((p[k] - a[k]) * d[k] for k in range(3)) / L2))
    return math.dist(p, [a[k] + d[k] * t for k in range(3)])


def test_answers_place_the_underground_tank_and_raise_the_fire_band(wb_rules):
    model, lay, layout = fit(wb_rules, {"fitout:underground_tank": [12000, 3000], "fitout:building_height_m": 20})
    assert mep.problems(layout, model, lay) == []
    assert layout.height_m == 20 and layout.fire_band_max_m == 35
    band = wb_rules.fire.band(20)
    assert [p.id for p in layout.provisions] == band.provisions
    status = {p.id: p.status for p in layout.provisions}
    assert status["wet_riser"] == "placed" and status["terrace_tank"] == "sized"
    assert status["yard_hydrant"] == "not_laid_out"
    assert {"hose_reel", "call_point", "landing_valve"} <= {d.what for d in layout.devices}
    ugt = next(d for d in layout.devices if d.what == "underground_tank")
    assert ugt.capacity_l == math.ceil(5 * 135 / 100) * 100 + 75000 and ugt.insertion == (12000, 3000)
    oht = next(d for d in layout.devices if d.what == "overhead_tank")
    assert oht.capacity_l == math.ceil(5 * 135 * 0.5 / 100) * 100 + 25000
    rising = next(r for r in layout.runs if r.key.split("-")[-2] == "rising_main")
    pump = next(d for d in layout.devices if d.what == "pump")
    assert (rising.connect_start, rising.connect_end) == (pump.key, oht.key)
    assert "fitout:underground_tank" not in {q.id for q in layout.questions}


def test_a_shaft_of_unknown_kind_and_a_large_living_room_are_asked(wb_rules):
    shaft = {"op": "shaft", "key": "SH", "base_level": "L0", "top_level": "L0", "base_offset_mm": -200,
             "points": [(9000, 0), (10000, 0), (10000, 1000), (9000, 1000)]}
    model, lay, layout = fit(wb_rules, None, shaft)
    q = {q.id: q for q in layout.questions}
    assert q["fitout:shaft_kind:SH"].options == ["duct", "lift", "stair", "other"]
    model2, lay2, layout2 = fit(wb_rules, {"fitout:shaft_kind:SH": "lift"}, shaft)
    assert "fitout:shaft_kind:SH" not in {q.id for q in layout2.questions}
