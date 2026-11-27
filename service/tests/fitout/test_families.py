"""Family matching (Step 10, stage 2): project first, then the installed templates; keywords and size decide; a size
copy only when the family has the size parameters; otherwise the closest, with the difference listed; nothing that
fits is asked."""

from plinth.fitout import families
from plinth.fitout.families import Need
from tests.fitout.fake_revit import FitoutRevit

RES = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Residential-Default_Metric.rte"
SYS = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Systems-Default_Metric.rte"


def _templates():
    return {
        SYS: {"family_types": [
            {"id": 31, "category": "OST_PlumbingFixtures", "family": "M_Sink Kitchen-Double", "type": "Standard",
             "placement": "OneLevelBased", "dims_mm": {}},
            {"id": 32, "category": "OST_LightingFixtures", "family": "M_Downlight - Disk", "type": "100W - 230V",
             "placement": "WorkPlaneBased", "dims_mm": {}}],
              "mep_types": {"pipe_type": [{"id": 41, "name": "PVC - DWV"}, {"id": 42, "name": "Copper"}],
                            "pipe_system_type": [{"id": 43, "name": "Sanitary", "classification": "Sanitary"},
                                                 {"id": 44, "name": "Domestic Cold Water",
                                                  "classification": "DomesticColdWater"}]}},
        RES: {"family_types": [
            {"id": 11, "category": "OST_Furniture", "family": "M_Bed-Standard", "type": "1525x2032mm",
             "placement": "OneLevelBased", "dims_mm": {"width": 1525, "length": 2032}},
            {"id": 12, "category": "OST_Furniture", "family": "M_Bed-Box", "type": "Queen 1800x2000",
             "placement": "OneLevelBased", "dims_mm": {}},
            {"id": 13, "category": "OST_Furniture", "family": "M_Sofa", "type": "2134mm", "placement": "OneLevelBased",
             "dims_mm": {"width": 2134, "depth": 914}},
            {"id": 14, "category": "OST_Furniture", "family": "M_Chair-Breuer", "type": "Chair-Breuer",
             "placement": "OneLevelBased", "dims_mm": {}}]},
    }


def need(item, category, keywords, w=None, d=None, keys=("K1",)):
    return Need(id=families.need_id(item, w, d), item=item, label=item.replace("_", " "), category=category,
                keywords=keywords, width_mm=w, depth_mm=d, keys=list(keys))


BED = ["bed", "double bed", "queen", "mattress"]


def test_keywords_match_whole_words_in_any_order():
    kitchen = ["kitchen sink", "sink", "bowl"]
    assert families.keyword_score(kitchen, "M_Sink Kitchen-Double", "Standard") > families.keyword_score(
        kitchen, "M_Sink Vanity-Round", "500mm")
    assert families.keyword_score(["wc", "water closet"], "M_Water Closet-Flush Tank") == 2
    assert families.keyword_score(["sofa"], "M_Sofa-Pensi") > 0 and families.keyword_score(["bed"], "M_Bedside") == 0
    assert families.keyword_score(["side table", "night stand"], "M_Table-Night Stands") == 2  # plural


def test_categories_and_needs_group_by_item_and_size():
    assert families.builtin_category("Plumbing Fixtures") == "OST_PlumbingFixtures"
    assert families.builtin_category("Specialty Equipment") == "OST_SpecialityEquipment"
    records = [{"key": "A", "item": "bed", "label": "Double bed", "category": "Furniture", "keywords": BED,
                "width_mm": 1800, "depth_mm": 2000},
               {"key": "B", "item": "bed", "label": "Double bed", "category": "Furniture", "keywords": BED,
                "width_mm": 1800, "depth_mm": 2000},
               {"key": "C", "item": "bed", "label": "Double bed", "category": "Furniture", "keywords": BED,
                "width_mm": 1500, "depth_mm": 2000}]
    needs = families.needs_from(records)
    assert [(n.id, n.keys) for n in needs] == [("bed-1500x2000", ["C"]), ("bed-1800x2000", ["A", "B"])]


def test_a_project_type_that_fits_is_used_and_templates_are_not_opened():
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "Bed-Double", "type": "1800 x 2000",
                                "dims": {"width": 1800, "depth": 2000}}], templates=_templates())
    found = families.resolve([need("bed", "OST_Furniture", BED, 1800, 2000)], revit)
    choice = found.choices["bed-1800x2000"]
    assert (choice.source, choice.family, choice.type, choice.base_type) == ("project", "Bed-Double", "1800 x 2000", None)
    assert not any(c in ("list_template_files", "list_types_in_file") for c, _ in revit.calls)
    assert found.questions == [] and found.assumptions == [] and found.searched == ["project"]


def test_a_template_type_that_fits_beats_a_project_type_that_does_not():
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "Bed-Single", "type": "900 x 1900",
                                "dims": {"width": 900, "depth": 1900}, "writable": []}], templates=_templates())
    found = families.resolve([need("bed", "OST_Furniture", BED, 1800, 2000)], revit)
    choice = found.choices["bed-1800x2000"]
    # M_Bed-Box "Queen 1800x2000": size read from its name; "queen" scores as well as "bed".
    assert (choice.source, choice.path, choice.family, choice.type) == ("template", RES, "M_Bed-Box", "Queen 1800x2000")
    assert [a.id for a in found.assumptions] == ["fitout:family-source:bed-1800x2000"]
    assert found.searched == ["project", RES, SYS]  # Residential before Systems


def test_a_size_copy_is_planned_only_when_the_family_has_the_size_parameters():
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "Sofa-3", "type": "2000",
                                "dims": {"width": 2000, "depth": 850}}], templates={})
    found = families.resolve([need("sofa", "OST_Furniture", ["sofa", "couch"], 1800, 850)], revit, templates=[])
    choice = found.choices["sofa-1800x850"]
    assert (choice.family, choice.base_type, choice.type, choice.values_mm) == (
        "Sofa-3", "2000", "1800x850mm (Plinth)", {"Width": 1800.0})
    assert [a.id for a in found.assumptions] == ["fitout:family-copy:sofa-1800x850"]

    # The same type with read-only size parameters: used as it is, the difference listed.
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "Sofa-3", "type": "2000",
                                "dims": {"width": 2000, "depth": 850}, "writable": []}], templates={})
    found = families.resolve([need("sofa", "OST_Furniture", ["sofa", "couch"], 1800, 850)], revit, templates=[])
    choice = found.choices["sofa-1800x850"]
    assert (choice.type, choice.base_type, choice.differences) == ("2000", None, ["width 2000 instead of 1800"])
    assert found.assumptions[0].id == "fitout:family-size:sofa-1800x850"


def test_a_template_type_with_its_size_parameters_is_copied_to_the_size():
    revit = FitoutRevit(templates=_templates())
    found = families.resolve([need("bed", "OST_Furniture", ["bed"], 1600, 2000)], revit)
    choice = found.choices["bed-1600x2000"]
    # Only M_Bed-Standard has size parameters (Width, Length); M_Bed-Box's size is only in its name.
    assert (choice.source, choice.family, choice.base_type, choice.values_mm) == (
        "template", "M_Bed-Standard", "1525x2032mm", {"Width": 1600.0})


def test_nothing_with_the_keywords_is_asked_and_the_answer_is_used():
    revit = FitoutRevit(templates=_templates())
    wardrobe = need("wardrobe", "OST_Furniture", ["wardrobe", "almirah"], 1200, 600, keys=("W1", "W2"))
    found = families.resolve([wardrobe], revit)
    assert found.choices == {}
    (q,) = found.questions
    assert q.id == "fitout:family:wardrobe-1200x600" and q.topic == "families" and q.instances == ["W1", "W2"]
    # nothing shares a word with a wardrobe: a chair is not offered; a box Plinth creates and "skip" always are
    assert q.options == [{"create": "box"}, "skip"]
    assert '{"family": "<family>", "type": "<type>"}' in q.text and '{"create": "box"}' in q.text

    answered = families.resolve([wardrobe], revit, {q.id: {"family": "M_Sofa", "type": "2134mm"}})
    choice = answered.choices[wardrobe.id]
    assert (choice.family, choice.answered, choice.base_type) == ("M_Sofa", True, "2134mm")  # copied to 1200 x 600
    assert families.resolve([wardrobe], revit, {q.id: "skip"}).skipped == [wardrobe.id]
    wrong = families.resolve([wardrobe], revit, {q.id: {"family": "M_None", "type": "X"}})
    assert wrong.questions and "is not in the project" in wrong.questions[0].text


def device(rules, group, name, keys=("D1",)):
    spec = getattr(rules, group).devices[name]
    return Need(id=name, item=name, label=spec.label, category=families.builtin_category(spec.category),
                keywords=spec.keywords, width_mm=spec.size.width_mm, depth_mm=spec.size.depth_mm,
                height_mm=spec.size.height_mm, keys=list(keys), device=True)


def test_a_weak_or_wrong_device_match_is_asked_not_picked(wb_rules):
    # 2026-11-27, on the user's model: exhaust fans and split AC indoor units became a ceiling-ducted chilled-water fan
    # coil unit, 6 A sockets a US GFCI duplex, roof outlets a floor drain, the switchboard a dimmer switch copied to
    # 250 mm wide; a BIM engineer sees each at once
    sys_ = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Systems-DefaultGBRENU.rte"
    rows = [("OST_MechanicalEquipment", "Fan Coil Unit - Ceiling Ducted - CHW", "282 LPS", {}),
            ("OST_MechanicalEquipment", "Air Handling Unit - Split System - Vertical", "03", {}),
            ("OST_MechanicalEquipment", "Ventilation Unit", "Small", {}),
            ("OST_ElectricalFixtures", "M_Duplex Receptacle", "GFCI", {}),
            ("OST_PlumbingFixtures", "M_Floor Drain - Round", "125 mm Strainer", {}),
            ("OST_LightingDevices", "Lighting Switches", "Dimmer Switch", {"width": 70, "length": 114})]
    templates = {sys_: {"family_types": [
        {"id": i, "category": c, "family": f, "type": t, "placement": "OneLevelBased", "dims_mm": d}
        for i, (c, f, t, d) in enumerate(rows, 1)]}}
    revit = FitoutRevit(templates=templates)
    needs = [device(wb_rules, "hvac", "exhaust_fan"), device(wb_rules, "hvac", "ac_indoor"),
             device(wb_rules, "hvac", "ac_outdoor"), device(wb_rules, "electrical", "socket_6a"),
             device(wb_rules, "plumbing", "roof_outlet"), device(wb_rules, "electrical", "switchboard")]
    found = families.resolve(needs, revit)
    assert found.choices == {}
    asked = {q.id.split(":")[-1]: q for q in found.questions}
    assert set(asked) == {n.id for n in needs}
    options = {k: [o["family"] for o in q.options if isinstance(o, dict) and "family" in o] for k, q in asked.items()}
    # offered only what shares a word and is not what the item is not: the ventilation unit for the exhaust fan, not
    # the fan coil; the switch (at its own size, offered) for the switchboard
    assert options["exhaust_fan"] == ["Ventilation Unit"]
    assert options["ac_indoor"] == ["Ventilation Unit"]  # shares "unit"; not the fan coil, nor the air handling unit
    assert options["socket_6a"] == [] and options["roof_outlet"] == []
    assert options["switchboard"] == ["Lighting Switches"]
    assert asked["switchboard"].options[-2:] == [{"create": "box", "size_mm": [250.0, 50.0, 150.0]}, "skip"]
    assert "well enough to be picked" in asked["switchboard"].text  # a match, but far from its size
    # a strong match at a plausible size is picked as before
    templates[sys_]["family_types"].append({"id": 9, "category": "OST_MechanicalEquipment", "family": "Exhaust Fan",
                                            "type": "Wall 300mm", "placement": "OneLevelBased", "dims_mm": {}})
    picked = families.resolve([device(wb_rules, "hvac", "exhaust_fan")], FitoutRevit(templates=templates))
    assert picked.choices["exhaust_fan"].family == "Exhaust Fan"


def test_mep_curve_types_come_with_fittings_and_never_by_an_ambiguous_name():
    plu = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Plumbing-Default_Metric.rte"
    ele = "C:\\ProgramData\\Autodesk\\RVT 2026\\Templates\\English\\Electrical-Default_Metric.rte"
    templates = {plu: {"family_types": [], "mep_types": {"pipe_type": [{"id": 61, "name": "PVC - DWV"}]}},
                 ele: {"family_types": [], "mep_types": {"conduit_type": [
                     {"id": 71, "name": "Rigid Nonmetallic Conduit (RNC Sch 40)"},
                     {"id": 72, "name": "Rigid Nonmetallic Conduit (RNC Sch 40)"}]}}}
    # the user's project (from DefaultMetric.rte): a pipe type "Default", two conduit types "Conduit", no fittings
    project = [{"kind": "pipe_type", "name": "Default"}, {"kind": "conduit_type", "name": "Conduit"},
               {"kind": "conduit_type", "name": "Conduit"}]
    needs = families.run_needs([{"key": "P", "kind": "pipe", "system": "Sanitary"}, {"key": "C", "kind": "conduit"}])
    needs = [n for n in needs if n.category in ("pipe_type", "conduit_type")]
    found = families.resolve(needs, FitoutRevit(mep=project, templates=templates))
    pipe, conduit = (found.choices[next(n.id for n in needs if n.category == k)] for k in ("pipe_type", "conduit_type"))
    assert (pipe.source, pipe.type, pipe.source_id) == ("template", "PVC - DWV", None)
    assert (conduit.source, conduit.type, conduit.source_id) == ("template", "Rigid Nonmetallic Conduit (RNC Sch 40)", 71)
    assert {a.id for a in found.assumptions} >= {f"fitout:mep-fittings:{n.id}" for n in needs}
    from plinth.fitout import to_plan
    items = [i for op in to_plan._type_ops([pipe, conduit]) for i in op["items"]]
    assert {"kind": "conduit_type", "name": "Rigid Nonmetallic Conduit (RNC Sch 40)", "source_id": 71} in items
    # copied by its id: the fake (as the add-in) takes the one asked for
    revit = FitoutRevit(templates=templates)
    data = revit.call("copy_types_from_file", {"path": ele, "items": [
        {"kind": "conduit_type", "name": "Rigid Nonmetallic Conduit (RNC Sch 40)", "source_id": 72}]})
    assert data["items"][0]["status"] == "copied" and data["items"][0]["source_id"] == 72
    # a project with pipe fittings keeps its own pipe type
    fitted = FitoutRevit(mep=project, templates=templates, types=[
        {"category": "OST_PipeFitting", "family": "M_Elbow - PVC", "type": "Standard"}])
    pipe_need = next(n for n in needs if n.category == "pipe_type")
    assert families.resolve([pipe_need], fitted).choices[pipe_need.id].source == "project"


def test_an_earlier_runs_box_copy_and_hidden_types_are_planned_as_it_made_them():
    bed = need("bed", "OST_Furniture", BED, 1500, 2000).model_copy(update={"height_mm": 450})
    old = families.EarlierBox(run="R1", key="FIT-FAMILY-B2M-BED", element_id=9, family="B2M Bed",
                              type="1500 x 2000 x 450mm (B2M)", category="OST_Furniture", width_mm=1500,
                              depth_mm=2000, height_mm=450)
    choice, note = families.box_choice(bed, {"create": "box"}, earlier=[old])
    assert (choice.family, choice.type) == ("B2M Bed", "1500 x 2000 x 450mm (B2M)") and "run R1" in note.text
    other_size = bed.model_copy(update={"width_mm": 1200})
    assert families.box_choice(other_size, {"create": "box"}, earlier=[old])[0].family == "Plinth bed"
    assert families.box_choice(bed, {"create": "box", "type": "Plain"}, earlier=[old])[0].type == "Plain"
    # a size copy keeps the name the earlier run gave it
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "Sofa-3", "type": "2000",
                                "dims": {"width": 2000, "depth": 850}}], templates={})
    sofa = need("sofa", "OST_Furniture", ["sofa"], 1800, 850)
    copies = [{"family": "Sofa-3", "base_type": "2000", "values_mm": {"Width": 1800.0}, "name": "1800x850mm (B2M)"}]
    found = families.resolve([sofa], revit, templates=[], as_built=families.AsBuilt(copies=copies))
    assert found.choices[sofa.id].type == "1800x850mm (B2M)"
    # a type an earlier run (with no saved layout) brought in is not seen in the project: planned as it was then
    revit = FitoutRevit(types=[{"category": "OST_Furniture", "family": "M_Bed-Box", "type": "Queen 1800x2000",
                                "box": {"min": [-900, -2000, 0], "max": [900, 0, 500]}}], templates=_templates())
    hidden = families.AsBuilt(hidden=[("M_Bed-Box", "Queen 1800x2000")])
    again = families.resolve([need("bed", "OST_Furniture", BED, 1800, 2000)], revit, as_built=hidden)
    assert again.choices["bed-1800x2000"].source == "template"


def test_run_types_match_by_kind_keywords_and_system_classification():
    revit = FitoutRevit(templates=_templates())
    runs = [{"key": "SOIL", "kind": "pipe", "system": "Sanitary"}, {"key": "CW", "kind": "pipe", "system": "DomesticColdWater"}]
    needs = families.run_needs(runs)
    found = families.resolve(needs, revit)
    by_item = {n.category + ":" + (n.classification or ""): found.choices[n.id] for n in needs}
    assert by_item["pipe_type:"].type == "PVC - DWV" and by_item["pipe_type:"].kind == "pipe_type"
    assert by_item["pipe_system_type:Sanitary"].type == "Sanitary"
    assert by_item["pipe_system_type:DomesticColdWater"].type == "Domestic Cold Water"
    assert by_item["pipe_system_type:Sanitary"].category == "OST_PipingSystem"
