"""A box family Plinth creates for a fit-out item, only because the user answered so (2026-11-17: the user's bed set was
3.5 m wide and fitted no bedroom; they said yes to a plain bed): the answer ``fitout:family:<item>`` =
{"create": "box"} plans a family_create pre-step (the add-in's create_box_family), lays the item out at the box's size
with no measuring (its origin is the middle of its back, front -Y, base at the level), places it with that family, and
the run removes exactly what the creation added (nothing when a family of that name was already there)."""

import pytest

from plinth.fitout import families, rooms, to_plan
from plinth.plan import BuildPlan
from plinth.revit import commands
from plinth.runner import Runner, plan_batches
from tests.fitout.test_measured import ANSWERS, building, revit

BOX_ANSWERS = {**ANSWERS, "fitout:family:bed": {"create": "box"}, "fitout:family:side_table": "skip"}


def planned(wb_rules, answers=BOX_ANSWERS, doc=None):
    plan = building()
    model = rooms.build(plan, wb_rules)
    doc = doc or revit()
    built = {op.key: 70000 + i for i, op in enumerate(plan.ops)}
    for key, element_id in built.items():  # the building's run, as built
        doc.elements[element_id] = {"category": "OST_Walls", "level": None}
    return doc, to_plan.plan_furniture(doc, plan, built, model, wb_rules, answers=answers, templates=[])


def test_the_plan_creates_the_box_and_places_the_bed_with_it(wb_rules):
    doc, found = planned(wb_rules)
    assert found.ready, [q.id for q in found.questions]
    by_key = found.plan.by_key()
    create = by_key["FIT-FAMILY-PLINTH-BED"]
    assert create.model_dump(exclude_none=True) == {
        "key": "FIT-FAMILY-PLINTH-BED", "op": "family_create", "kind": "box", "category": "OST_Furniture",
        "family": "Plinth Bed", "type": "1500 x 2000 x 450mm (Plinth)", "width_mm": 1500, "depth_mm": 2000, "height_mm": 450}
    # laid out at the box's own size (the rule's first), never measured
    bed = next(p for p in found.layout.placed if p.item == "bed")
    assert (bed.width_mm, bed.depth_mm, bed.height_mm) == (1500, 2000, 450)
    assert all("Plinth Bed" not in str(p) for c, p in doc.calls if c == "measure_family_types")
    # placed with the new family: its back's middle (its origin) on the layout's point, front turned as the layout says
    op = by_key[bed.key]
    assert (op.family, op.type, op.category) == ("Plinth Bed", "1500 x 2000 x 450mm (Plinth)", "OST_Furniture")
    assert op.point == pytest.approx(tuple(bed.insertion), abs=0.1)
    assert op.rotation_deg == pytest.approx(to_plan._rotation(bed.rotation_deg))
    assert op.offset_mm == 0 and "the box family Plinth creates" in op.source
    note = next(a for a in found.assumptions if a.id == "fitout:family-create:bed")
    assert "creates a plain box family 'Plinth Bed'" in note.text and "removed with the fit-out run" in note.text
    assert [u.reason for u in found.layout.unplaced if u.item == "side_table"] == ["left out by the user's answer"]
    # a direct pre-step of its own, before anything is placed
    batches = plan_batches(found.plan)
    assert batches[0][0].op in ("types_from_file", "family_create") and [create] in batches
    assert batches.index([create]) < min(i for i, b in enumerate(batches) if b[0].op == "family")


def test_build_creates_it_and_removing_the_run_removes_only_what_it_added(wb_rules):
    doc, found = planned(wb_rules)
    before = {i: dict(e) for i, e in doc.elements.items()}
    runner = Runner(doc)
    result = runner.build(found.plan)
    assert result.status == "built", result.error
    call = next(p for c, p in doc.calls if c == "create_box_family")
    assert call == {"family_name": "Plinth Bed", "type_name": "1500 x 2000 x 450mm (Plinth)", "category": "OST_Furniture",
                    "width_mm": 1500, "depth_mm": 2000, "height_mm": 450}
    made = {k: v for k, v in result.created.items() if commands.op_key(k) == "FIT-FAMILY-PLINTH-BED"}
    assert [doc.elements[i].get("is_family") or doc.elements[i].get("is_type") or doc.elements[i].get("is_material")
            for i in made.values()] == [True, True, True]  # the family, its type, the material it brought
    beds = [e for e in doc.instances().values() if e.get("family") == "Plinth Bed"]
    assert len(beds) == sum(1 for p in found.layout.placed if p.item == "bed") >= 1
    assert runner.verify(found.plan, result.created)["ok"]
    runner.remove(result.created)
    assert doc.elements == before


def test_a_family_of_that_name_already_there_is_used_and_never_removed(wb_rules):
    doc = revit()
    old_type = doc._add_type("family_type", "Queen", "Plinth Bed", "OST_Furniture", placement="OneLevelBased")
    doc, found = planned(wb_rules, doc=doc)
    before = {i: dict(e) for i, e in doc.elements.items()}
    runner = Runner(doc)
    result = runner.build(found.plan)
    assert result.status == "built", result.error
    assert not any(commands.op_key(k) == "FIT-FAMILY-PLINTH-BED" for k in result.created)  # nothing of it recorded
    beds = [e for e in doc.instances().values() if e.get("family") == "Plinth Bed"]
    assert beds and all(e["type_id"] == old_type for e in beds)  # its own type, as it is
    runner.remove(result.created)
    assert doc.elements == before


def test_answer_options_and_refusals(wb_rules):
    need = families.Need(id="bed", item="bed", label="Bed", category="OST_Furniture", keywords=["bed"], width_mm=1500,
                         depth_mm=2000, height_mm=450, keys=["K1"])
    choice, note = families.box_choice(need, {"create": "box", "name": "Plain bed", "size_mm": [1200, 1950, 400.5],
                                              "material": "Oak"})
    assert (choice.family, choice.type, choice.source, choice.placement) == (
        "Plain bed", "1200 x 1950 x 400.5mm (Plinth)", "create", "OneLevelBased")
    assert choice.measured.min == (-600, -1950, 0) and choice.measured.max == (600, 0, 400.5)
    assert choice.measured.size() == (1200, 1950, 400.5) and choice.box.material == "Oak"
    assert note.value["size_mm"] == [1200, 1950, 400.5]
    for bad, why in (({"create": "mesh"}, "only family Plinth creates"), ({"create": "box", "name": "Bed: big"}, "family name"),
                     ({"create": "box", "size_mm": [1500, 0, 450]}, "size_mm"),
                     ({"create": "box", "family": "X"}, "do not apply")):
        with pytest.raises(ValueError, match=why):
            families.box_choice(need, bad)
    with pytest.raises(ValueError, match="give size_mm"):
        families.box_choice(need.model_copy(update={"height_mm": None}), {"create": "box"})


def test_an_item_at_two_sizes_gets_a_box_family_per_size():
    records = [{"key": f"B{i}", "item": "bed", "label": "Bed", "category": "Furniture", "keywords": ["bed"],
                "width_mm": w, "depth_mm": 2000, "height_mm": 450} for i, w in enumerate((1500, 1200))]
    needs = families.needs_from(records)
    found = families.resolve(needs, revit(), {"fitout:family:bed": {"create": "box"}}, templates=[])
    assert sorted(c.family for c in found.choices.values()) == ["Plinth Bed 1200x2000", "Plinth Bed 1500x2000"]
    ops = to_plan._type_ops(found.choices.values())
    assert sorted(op["key"] for op in ops) == ["FIT-FAMILY-PLINTH-BED-1200X2000", "FIT-FAMILY-PLINTH-BED-1500X2000"]
    # one size given: one family serves both
    found = families.resolve(needs, revit(), {"fitout:family:bed": {"create": "box", "size_mm": [1500, 2000, 450]}},
                             templates=[])
    assert [op["family"] for op in to_plan._type_ops(found.choices.values())] == ["Plinth Bed"]


def test_the_command_mapping():
    plan = BuildPlan.model_validate({"ops": [
        {"op": "level", "key": "L0", "name": "L0", "elevation_mm": 0},
        {"op": "family_create", "key": "FIT-FAMILY-PLINTH-BED", "category": "OST_Furniture", "family": "Plinth Bed",
         "type": "1500 x 2000 x 450mm (Plinth)", "width_mm": 1500, "depth_mm": 2000, "height_mm": 450, "material": "Oak"}]})
    op = plan.by_key()["FIT-FAMILY-PLINTH-BED"]
    assert commands.to_command(op, plan.by_key(), {}) == {"command": "create_box_family", "params": {
        "family_name": "Plinth Bed", "type_name": "1500 x 2000 x 450mm (Plinth)", "category": "OST_Furniture",
        "width_mm": 1500, "depth_mm": 2000, "height_mm": 450, "material_name": "Oak"}}
    assert commands.created_entries(op, {"action": "created", "created_ids": [5, 6, 7]}) == {
        "FIT-FAMILY-PLINTH-BED": 5, "FIT-FAMILY-PLINTH-BED#2": 6, "FIT-FAMILY-PLINTH-BED#3": 7}
    assert commands.created_entries(op, {"action": "already_there", "family_id": 5, "created_ids": []}) == {}
    assert "family_create" in commands.DIRECT_OPS
    with pytest.raises(ValueError, match="created only once"):
        BuildPlan.model_validate({"ops": [*plan.model_dump()["ops"], {**plan.model_dump()["ops"][1], "key": "X"}]})
