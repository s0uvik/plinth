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
    assert {"family": "M_Chair-Breuer", "type": "Chair-Breuer", "source": "template", "path": RES} in q.options
    assert q.options[-1] == "skip"

    answered = families.resolve([wardrobe], revit, {q.id: {"family": "M_Sofa", "type": "2134mm"}})
    choice = answered.choices[wardrobe.id]
    assert (choice.family, choice.answered, choice.base_type) == ("M_Sofa", True, "2134mm")  # copied to 1200 x 600
    assert families.resolve([wardrobe], revit, {q.id: "skip"}).skipped == [wardrobe.id]
    wrong = families.resolve([wardrobe], revit, {q.id: {"family": "M_None", "type": "X"}})
    assert wrong.questions and "is not in the project" in wrong.questions[0].text


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
