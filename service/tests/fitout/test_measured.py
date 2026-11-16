"""Families first (Step 10, stage 2 before stage 3): the family chosen for each item is measured (the add-in's
measure_family_types, or measurements given), the layout uses its real footprint with the rule's clearances, and the
plan inserts each family so its measured box lands on the layout's footprint (its back's middle on the layout's point,
whatever its origin, its front turned as the user says), on the level even when the family took the point's z as an
offset. Items the user skips, or a chosen family includes, are not placed."""

import math

import pytest

from b2m.fitout import families, furniture, rooms, to_plan
from b2m.fitout.families import Measurement
from tests.fitout.conftest import box_walls, plan_of
from tests.fitout.fake_revit import FitoutRevit

# Boxes as the user measured them live on 2026-11-17 (level "Floor 1" at 2700; several families took the point's z as
# an offset, so their boxes start at +2700).
SAVED = {"types": [
    {"type_id": 373233, "family": "LR1454_Bella_Chocolate_sofa", "type": "LR1454_Bella_Chocolate_sofa",
     "placement": "OneLevelBased", "min": [-952, -455, 2700], "max": [1339, 575, 3616]},
    {"type_id": 366642, "family": "LR394_Conjunto_Cama_01", "type": "LR394_Conjunto_Cama_01",
     "placement": "OneLevelBased", "min": [-1750, -918, 2694], "max": [1750, 1680, 4126]},
    {"type_id": 362456, "family": "Wardrobe", "type": "1220 x 1830 x 610mm", "placement": "OneLevelBased",
     "min": [-610, -330, 0], "max": [610, 305, 1829]},
    {"type_id": 376771, "family": "LR1877_PV175B2", "type": "PV175B2", "placement": "WorkPlaneBased",
     "min": [-250, -260, -58], "max": [470, 250, 40]},
]}


def corners(box, rotation, at):
    x0, y0, x1, y1 = box
    return {tuple(round(v, 1) for v in (at[0] + p[0], at[1] + p[1]))
            for p in (families.turn(c, rotation) for c in ((x0, y0), (x1, y0), (x1, y1), (x0, y1)))}


@pytest.mark.parametrize("front", ["-y", "+y", "-x", "+x"])
@pytest.mark.parametrize("facing", [0.0, 90.0, 180.0, 270.0, 30.0])
def test_a_measured_family_lands_on_the_layouts_footprint(front, facing):
    m = Measurement(type="T", min=(-952, -455, 0), max=(1339, 575, 900))
    w, d, h = m.size(front)
    assert {w, d} == {2291, 1030} and h == 900
    # the layout: back edge's middle at P, front facing ``facing``; rotation for a family facing -Y = facing - 270
    p = (5000.0, 3000.0)
    inward = families.turn((1.0, 0.0), facing)
    along = (inward[1], -inward[0])  # back edge direction, the room on its left
    b0 = (p[0] - along[0] * w / 2, p[1] - along[1] * w / 2)
    b1 = (p[0] + along[0] * w / 2, p[1] + along[1] * w / 2)
    footprint = {tuple(round(v, 1) for v in q) for q in (b0, b1, (b1[0] + inward[0] * d, b1[1] + inward[1] * d),
                                                          (b0[0] + inward[0] * d, b0[1] + inward[1] * d))}
    at, rotation = to_plan.measured_point(m, front, p, (facing - 270) % 360)
    # the family's own box (family coordinates), turned by the op's rotation about the insertion point
    placed = corners((m.min[0], m.min[1], m.max[0], m.max[1]), rotation, at)
    assert all(min(math.dist(q, f) for f in footprint) < 0.3 for q in placed), (placed, footprint)
    # its front faces where the layout says
    front_dir = {"-y": (0, -1), "+y": (0, 1), "-x": (-1, 0), "+x": (1, 0)}[front]
    turned = families.turn(front_dir, rotation)
    assert turned[0] == pytest.approx(inward[0], abs=1e-9) and turned[1] == pytest.approx(inward[1], abs=1e-9)


def test_saved_measurements_make_the_level_offset_explicit():
    got = {m.family: m for m in families.load_measurements(SAVED, level_elevations=[-600, 0, 2700, 5400])}
    sofa, bed, wardrobe, hob = (got[n] for n in ("LR1454_Bella_Chocolate_sofa", "LR394_Conjunto_Cama_01", "Wardrobe",
                                                 "LR1877_PV175B2"))
    assert (sofa.base_offset_mm, sofa.base_mm, sofa.size()) == (2700, 0, (2291, 1030, 916))
    assert (bed.base_offset_mm, bed.base_mm, bed.size()) == (2700, -6, (3500, 2598, 1432))
    assert (wardrobe.base_offset_mm, wardrobe.size()) == (0, (1220, 635, 1829))
    assert (hob.base_offset_mm, hob.base_mm, hob.size()) == (0, -58, (720, 510, 98))
    assert "took the point's z as its offset" in sofa.describe()
    # a file names its level: that elevation only
    named = families.load_measurements({**SAVED, "level_elevation_mm": 5400})
    assert {m.base_offset_mm for m in named} == {0}
    # the offset above the level: 0 for these (the layout's elevation for a hob on the counter top)
    assert to_plan.measured_offset(sofa, 0, False) == 0 and to_plan.measured_offset(bed, 0, False) == 0
    assert to_plan.measured_offset(hob, 850, True) == 850
    deep = Measurement(type="T", min=(0, 0, -200), max=(1, 1, 300))
    assert to_plan.measured_offset(deep, 0, False) == 200  # geometry below its origin: raised onto the floor


# ------------------------------------------------------------------------------------------------ the flow

def building():
    """A bedroom (3600 x 4000 inside), a living room (4000 x 5200 inside) and a kitchen (2600 x 3000 inside, window
    on its east wall), each with a door in its south wall opening in."""
    def door(key, host, offset):
        return {"op": "door", "key": key, "host_key": host, "type": "Door 900x2100", "offset_mm": offset,
                "flip_facing": True}
    return plan_of(*box_walls("b", 0, 0, 3800, 4200), door("DB", "bS", 650),
                   {"op": "room", "key": "BED", "level": "L0", "point": (1900, 2100), "name": "Bedroom", "flat": "A"},
                   *box_walls("v", 5000, 0, 9200, 5400), door("DV", "vS", 650),
                   {"op": "room", "key": "LIV", "level": "L0", "point": (7100, 2700), "name": "Living", "flat": "A"},
                   *box_walls("k", 10000, 0, 12800, 3200), door("DK", "kS", 650),
                   {"op": "window", "key": "WK", "host_key": "kE", "type": "Window 1200x1200", "offset_mm": 1000,
                    "sill_mm": 1050},
                   {"op": "room", "key": "KIT", "level": "L0", "point": (11400, 1600), "name": "Kitchen", "flat": "A"})


def revit(**over):
    """The project: the user's families (boxes as measured), and the template types the matcher finds."""
    types = [
        {"category": "OST_Furniture", "family": "Bed Set", "type": "Queen set", "box": {
            "min": [-1300, -900, 3000], "max": [1300, 1200, 4000], "base_offset_mm": 3000}},
        {"category": "OST_Casework", "family": "Wardrobe", "type": "1520 x 1830 x 610mm",
         "box": {"min": [-762, -330, 0], "max": [762, 305, 1829]}},
        {"category": "OST_Casework", "family": "Wardrobe", "type": "1220 x 1830 x 610mm",
         "box": {"min": [-610, -330, 0], "max": [610, 305, 1829]}},
        {"category": "OST_Casework", "family": "Wardrobe", "type": "0910 x 1830 x 510mm",
         "box": {"min": [-457, -279, 0], "max": [457, 254, 1829]}},
        {"category": "OST_Furniture", "family": "Bella sofa", "type": "Bella sofa",
         "box": {"min": [-952, -455, 3000], "max": [1339, 575, 3916], "base_offset_mm": 3000}},
        # the TV unit is drawn as Furniture; the rule's category is Casework
        {"category": "OST_Furniture", "family": "Home TV set", "type": "Home TV set",
         "box": {"min": [0, -462, 0], "max": [2517, 0, 2395]}},
        {"category": "OST_Furniture", "family": "Dining six", "type": "Dining six",
         "box": {"min": [-631, -1458, -9], "max": [2358, 558, 1174]}},
        {"category": "OST_SpecialityEquipment", "family": "Hob", "type": "PV175B2", "placement": "WorkPlaneBased",
         "box": {"min": [-250, -260, -58], "max": [470, 250, 40]}},
        {"category": "OST_SpecialityEquipment", "family": "Fridge", "type": "550x600", "box": {
            "min": [-277, -602, 0], "max": [277, 0, 1525]}},
        {"category": "OST_Casework", "family": "Base Cabinet", "type": "600mm", "dims": {"width": 600, "depth": 600}},
        {"category": "OST_PlumbingFixtures", "family": "Sink Kitchen", "type": "750 x 450mm",
         "dims": {"width": 750, "depth": 450}},
        {"category": "OST_Furniture", "family": "M_Desk", "type": "1525 x 762mm",
         "box": {"min": [-782, -419, 0], "max": [782, 400, 762]}},
    ]
    return FitoutRevit(types=types, **over)


ANSWERS = {
    "fitout:family:bed": {"family": "Bed Set", "type": "Queen set", "includes": ["side_table"]},
    "fitout:family:wardrobe": {"family": "Wardrobe", "type": "1520 x 1830 x 610mm"},
    "fitout:family:tv_unit": {"family": "Home TV set", "type": "Home TV set"},
    "fitout:family:sofa": {"family": "Bella sofa", "type": "Bella sofa"},
    "fitout:family:dining_set": {"family": "Dining six", "type": "Dining six"},
    "fitout:family:hob": {"family": "Hob", "type": "PV175B2"},
    "fitout:family:fridge": {"family": "Fridge", "type": "550x600"},
    "fitout:family:study_table": {"family": "M_Desk", "type": "1525 x 762mm"},
}


@pytest.fixture(scope="module")
def planned(wb_rules):
    plan = building()
    model = rooms.build(plan, wb_rules)
    doc = revit()
    built = {op.key: 70000 + i for i, op in enumerate(plan.ops)}
    found = to_plan.plan_furniture(doc, plan, built, model, wb_rules, answers=ANSWERS, templates=[])
    return plan, model, doc, found


def test_the_layout_uses_each_chosen_familys_measured_footprint(planned, wb_rules):
    _, model, doc, found = planned
    layout = found.layout
    assert furniture.problems(layout, model, wb_rules) == []
    got = {p.item: p for p in layout.placed if p.item != "counter"}
    assert (got["bed"].width_mm, got["bed"].depth_mm, got["bed"].height_mm) == (2600, 2100, 1000)
    assert (got["sofa"].width_mm, got["sofa"].depth_mm) == (2291, 1030)
    assert (got["tv_unit"].width_mm, got["tv_unit"].depth_mm) == (2517, 462)
    assert (got["hob"].width_mm, got["hob"].depth_mm, got["hob"].elevation_mm) == (720, 510, 850)
    # the side table is in the bed set: not placed, listed per bedroom
    assert [(u.room_key, u.item, u.status) for u in layout.unplaced if u.item == "side_table"] == [
        ("BED", "side_table", "left_out")]
    assert "included in the bed family (Bed Set: Queen set)" in next(u.reason for u in layout.unplaced
                                                                     if u.item == "side_table")
    # measured once, in one call, for every chosen type and its family's other types
    measures = [p for c, p in doc.calls if c == "measure_family_types"]
    assert len(measures) == 1 and len(measures[0]["type_ids"]) == 11
    assert not doc.instances()  # measuring leaves nothing behind


def test_the_wardrobe_falls_back_to_the_familys_smaller_types(planned):
    _, _, _, found = planned
    options = found.items.items["wardrobe"].options
    assert [c.type for c in options] == ["1520 x 1830 x 610mm", "1220 x 1830 x 610mm", "0910 x 1830 x 510mm"]
    wardrobe = next(p for p in found.layout.placed if p.item == "wardrobe")
    op = found.plan.by_key()[wardrobe.key]
    assert op.type == options[wardrobe.size_index].type
    assert (wardrobe.width_mm, wardrobe.depth_mm) == (options[wardrobe.size_index].width_mm,
                                                      options[wardrobe.size_index].depth_mm)


def test_the_plan_places_each_measured_family_on_its_footprint(planned):
    _, _, _, found = planned
    by_key = found.plan.by_key()
    for p in found.layout.placed:
        if p.item == "counter" or p.key not in by_key:
            continue
        op = by_key[p.key]
        choice = next(c for c in found.families.values() if c.type == op.type and c.family == op.family)
        m = choice.measured
        if m is None:
            continue
        placed = corners((m.min[0], m.min[1], m.max[0], m.max[1]), op.rotation_deg, op.point)
        footprint = {tuple(q) for q in p.footprint}
        assert all(min(math.dist(q, f) for f in footprint) < 0.5 for q in placed), p.key
        assert op.host_key is None and op.offset_mm == (850 if p.item == "hob" else 0)
        assert "placed by its measured box" in op.source
    hob = next(by_key[p.key] for p in found.layout.placed if p.item == "hob")
    assert hob.category == "OST_SpecialityEquipment" and not hob.face_down  # work-plane-based: on the level's plane
    tv = next(by_key[p.key] for p in found.layout.placed if p.item == "tv_unit")
    assert tv.category == "OST_Furniture"  # found in another category than the rule's (Casework)
    measured = {a.id for a in found.assumptions if a.id.startswith("fitout:family-measured:")}
    assert {"fitout:family-measured:bed", "fitout:family-measured:sofa", "fitout:family-measured:hob"} <= measured
    bed_note = next(a for a in found.assumptions if a.id == "fitout:family-measured:bed")
    assert "2600 x 2100 mm" in bed_note.text and "took the point's z as its offset" in bed_note.text


def test_the_plan_builds_and_the_counter_and_sink_still_match_per_size(planned):
    from b2m.runner import Runner

    plan, _, doc, found = planned
    assert found.ready and found.questions == []
    copies = [op for op in found.plan.ops if op.op == "family_type_copy"]
    assert {op.family for op in copies} == {"Base Cabinet"}  # the counter's legs: size copies of the base cabinet
    result = Runner(doc).build(found.plan)
    assert result.status == "built", result.error


def test_given_measurements_need_no_add_in_and_a_missing_command_falls_back_to_the_rules(wb_rules):
    plan = building()
    model = rooms.build(plan, wb_rules)
    built = {op.key: 70000 + i for i, op in enumerate(plan.ops)}
    doc = revit()
    given = [Measurement(family="Bella sofa", type="Bella sofa", min=(-1000, -500, 0), max=(1000, 400, 800),
                         source="given")]
    answers = {"fitout:family:sofa": ANSWERS["fitout:family:sofa"], "fitout:family:side_table": "skip"}
    found = to_plan.plan_furniture(doc, plan, built, model, wb_rules, answers=answers, measurements=given, templates=[])
    sofa = next(p for p in found.layout.placed if p.item == "sofa")
    assert (sofa.width_mm, sofa.depth_mm) == (2000, 900)  # the given box, not the fake's
    asked = [p["type_ids"] for c, p in doc.calls if c == "measure_family_types"]
    assert all(found.items.items["sofa"].choice.type_id not in ids for ids in asked)
    assert [u.reason for u in found.layout.unplaced if u.item == "side_table"] == ["left out by the user's answer"]

    class OldAddin(FitoutRevit):
        def _measure_family_types(self, p):
            raise AttributeError("UNKNOWN_COMMAND: measure_family_types.")

    old = OldAddin(types=[{"category": "OST_Furniture", "family": "Bella sofa", "type": "Bella sofa"}])
    found = to_plan.plan_furniture(old, plan, built, model, wb_rules, answers=answers, templates=[])
    sofa = next(p for p in found.layout.placed if p.item == "sofa")
    assert (sofa.width_mm, sofa.depth_mm) == (2100, 850)  # the rule's size
    note = next(a for a in found.assumptions if a.id == "fitout:family-unmeasured:sofa")
    assert "measure_family_types failed" in note.text and "rule's sizes" in note.text


def test_the_measurement_cache_is_per_document_and_type():
    doc = revit()
    sofa_id = next(i for i, e in doc.project_types().items() if e["family"] == "Bella sofa")
    cache: dict = {}
    first = families.Measurer(doc, document="A", cache=cache).measure([(sofa_id, "Bella sofa", "Bella sofa")])
    again = families.Measurer(doc, document="A", cache=cache).measure([(sofa_id, "Bella sofa", "Bella sofa")])
    assert first == again and sum(c == "measure_family_types" for c, _ in doc.calls) == 1
    families.Measurer(doc, document="B", cache=cache).measure([(sofa_id, "Bella sofa", "Bella sofa")])
    assert sum(c == "measure_family_types" for c, _ in doc.calls) == 2


def test_a_bed_set_with_its_side_tables_keeps_no_side_clearance_and_one_too_big_is_reported(wb_rules):
    plan = building()
    model = rooms.build(plan, wb_rules)
    built = {op.key: 70000 + i for i, op in enumerate(plan.ops)}
    # the user's bed set (3500 x 2598, nightstands included) in a 3600 x 4000 bedroom: it fits only because the
    # set holds its side tables, so the bed's 600 mm side clearance is not kept beside the set
    user_set = [Measurement(family="Bed Set", type="Queen set", min=(-1750, -918, 0), max=(1750, 1680, 1432))]
    found = to_plan.plan_furniture(revit(), plan, built, model, wb_rules, answers=ANSWERS, measurements=user_set,
                                   templates=[])
    bed = next(p for p in found.layout.placed if p.item == "bed")
    assert (bed.width_mm, bed.depth_mm) == (3500, 2598)
    note = next(a for a in found.assumptions if a.id == "fitout:no-side-clearance:bed")
    assert "600 mm side clearance is not kept" in note.text and "side table" in note.text
    # without "includes" the side clearance stays, and the set fits nowhere: reported, not forced
    answers = {**ANSWERS, "fitout:family:bed": {"family": "Bed Set", "type": "Queen set"},
               "fitout:family:side_table": "skip"}
    found = to_plan.plan_furniture(revit(), plan, built, model, wb_rules, answers=answers, measurements=user_set,
                                   templates=[])
    assert "bed" not in {p.item for p in found.layout.in_room("BED")}
    why = next(u for u in found.layout.unplaced if u.item == "bed")
    assert why.status == "no_fit" and "3500 x 2598" in why.reason
    assert not any(a.id.startswith("fitout:no-side-clearance") for a in found.assumptions)
