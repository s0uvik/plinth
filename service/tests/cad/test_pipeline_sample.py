"""T5 on the G+4 sample: the whole import (classify, recognise, register on T3's features, cross-sheet resolution),
the grouped question list, answers applied, the assume step and readiness, the files kept and the CLI."""

import json
from collections import Counter

import pytest

from b2m.cad import answers, assumptions, pipeline, plan_bridge, questions, readiness
from b2m.cad.project import ImportStore, LocationRequired
from tests.cad.conftest import SAMPLE_DXF

TYPICAL, GROUND, ROOF = "2.PLAN 1st to 4th.dxf", "1.GROUND FLOOR PLAN.dxf", "3.ROOF PLAN.dxf"

ROOMS_NOT_ENCLOSED = plan_bridge._group_id("room_not_enclosed", [f"room:{GROUND}:room-{n}" for n in range(2, 6)])
STAIR_AXES = plan_bridge._group_id("stair_axis", [f"stair_axis:{GROUND}:stair-1", f"stair_axis:{TYPICAL}:stair-1"])
# The sample's questions: (id, severity, places) for the questions with ids of their own (T5's, then those the
# build-plan converter T6 still needs, under T6's ids) ...
EXPECTED = [
    ("location", "blocking", 0),
    ("mark-width-d1", "blocking", 4),
    ("opening-mark-door-1000", "blocking", 4),
    ("opening-mark-window-640", "blocking", 1),
    ("door-not-in-wall-1000", "blocking", 4),
    ("rooms-share-space-dining-kitchen-open-living-room", "blocking", 4),
    ("rooms-share-space-entrance-entrance", "blocking", 1),
    ("tie-beam-depth", "blocking", 0),
    ("door-height-d", "blocking", 0),
    ("door-height-d1", "blocking", 0),
    ("door-height-lift", "blocking", 0),
    ("window-size-w", "blocking", 0),
    ("window-size-w1", "blocking", 0),
    ("window-size-w2", "blocking", 0),
    ("window-size-w3", "blocking", 0),
    ("window-size-w4", "blocking", 0),
    ("boundary_wall_height", "blocking", 5),
    ("level:roof_structure", "blocking", 16),
    ("projection:2.PLAN 1st to 4th.dxf:projection-22", "blocking", 0),
    ("projection_thickness:chajja", "blocking", 20),
    (ROOMS_NOT_ENCLOSED, "blocking", 4),  # T6's four room questions, asked once (one fact, four places)
    ("slab_outline:storey-0", "blocking", 0),
    (STAIR_AXES, "blocking", 2),  # and its two stair-axis questions
    ("rule-set", "check", 0),
    ("beam-depth", "check", 0),
    ("roof-structure", "check", 0),
    ("layer-sand-filling", "check", 0),
    ("wall-height", "check", 0),
    ("lintel", "check", 0),
    ("clear-height-nbc2016-nbc-height-habitable", "check", 0),
]
# ... and (topic, severity, places) for the findings asked as they are (ids hashed from their wording). The
# projection-kind and room-not-enclosed findings are asked by T6's questions above, in a form the plan can use.
EXPECTED_GENERIC = Counter([
    ("walls_side_by_side", "check", 0), ("column", "check", 0),
    ("outline_ambiguous", "info", 0), ("room_name_order", "info", 0),
    ("dimensions", "info", 2), ("datum", "info", 0), ("datum", "info", 0), ("rebar", "info", 2),
    ("section_mark", "info", 0), ("section_mark", "info", 0), ("section_mark", "info", 0),
    ("section_mark", "info", 0), ("grid", "info", 0), ("registration", "info", 0), ("storeys", "info", 0),
    ("storeys", "info", 0)])
# Test-only answers (the T6 fixture's values, and choices for T5's own questions); never used by the product.
FIXTURE = SAMPLE_DXF.parent / "answers-test.json"
OWN = {
    "location": {"city": "Bardhaman", "state": "West Bengal"},
    "mark-width-d1": "800 mm",
    "opening-mark-door-1000": "DE",
    "opening-mark-window-640": "W2 (640 mm)",
    "door-not-in-wall-1000": {"mode": "new_wall", "thickness_mm": 250, "function": "interior"},
    "tie-beam-depth": 450,
}


@pytest.fixture(scope="module")
def sample(tmp_path_factory):
    store = ImportStore(tmp_path_factory.mktemp("imports"))
    project = store.create(SAMPLE_DXF)
    building = pipeline.run_import(project, store)
    return store, project, building


def test_sample_question_list(sample):
    _, project, _ = sample
    own = {q.id for q in project.questions}
    got = [(q.id, q.severity, len(q.instances)) for q in project.questions if q.id in {e[0] for e in EXPECTED}]
    assert got == EXPECTED
    generic = Counter((q.topic, q.severity, len(q.instances)) for q in project.questions
                      if q.id not in {e[0] for e in EXPECTED})
    assert generic == EXPECTED_GENERIC, own
    q = project.question("mark-width-d1")
    assert q.text == ("Doors marked D1 are drawn at 2 widths: 800 mm (17x), 900 mm (4x), on ground floor plan "
                      "(1.GROUND FLOOR PLAN.dxf), floor plan, storeys 1-4 (2.PLAN 1st to 4th.dxf). A mark is one "
                      "door type. Which width is D1?")
    assert q.options[-1] == "two types: D1 800 mm and a new mark for the 900 mm ones"
    roof = project.question("roof-structure")
    assert '"LIFT ROOM" and "MUMTY ROOM"' in roof.text and "TERRACE" not in roof.text


def test_location_is_suggested_never_set(sample):
    _, project, _ = sample
    assert project.inputs.location is None
    q = project.question("location")
    assert q.open and q.options == ["Bardhaman, West Bengal", "Durgapur, West Bengal",
                                    "another place (give the city or municipality and the state)"]
    assert any(e.sheet == "4.SITE PLAN.dxf" and "BARDHAMAN STATION" in (e.note or "") for e in q.evidence)


def test_registration_uses_t3_features(sample):
    _, _, b = sample
    regs = {r.sheet: r for r in b.registrations}
    for sheet in (GROUND, ROOF, "8.Foundation Details.dxf", "9.TIE BEAM LAYOUT(GROUND FLOOR).dxf"):
        assert regs[sheet].residual_mm <= 1.0, sheet
    assert "56 column" in regs[GROUND].method and "mark" in regs[GROUND].method
    assert all(p.section_marks and all(m.cut and m.view for m in p.section_marks) for p in b.plans)
    assert not any(f.topic == "plan_recognition" for p in b.plans for f in p.findings)  # no provisional fallback


def test_lift_doors_settled_across_sheets(sample):
    _, _, b = sample
    lifts = [r for r in b.resolved if r.id.startswith("lift-door-")]
    assert len(lifts) == 4 and {r.id.split(".dxf")[0] for r in lifts} == {"lift-door-1.GROUND FLOOR PLAN",
                                                                         "lift-door-3.ROOF PLAN"}
    for r in lifts:
        assert {e.sheet for e in r.evidence} >= {TYPICAL} and len({e.sheet for e in r.evidence}) == 2
    ground = next(p for p in b.plans if p.sheet == GROUND)
    assert not [f for f in ground.findings if f.id in ("opening-without-mark-1", "opening-without-mark-2")]
    assert sum(1 for o in ground.openings if o.swing == "sliding") == 2
    assert any(r.id.startswith("founding-level-") for r in b.resolved)


def test_files_kept_and_answers_applied(sample):
    store, project, b = sample
    folder = pipeline.folder(store, project)
    assert {p.name for p in folder.iterdir()} >= {"project.json", "recognised_building.json", "building.json"}
    saved = json.loads((folder / "recognised_building.json").read_text(encoding="utf-8"))
    assert len(saved["resolved"]) == len(b.resolved)

    p = store.get(project.id)
    with pytest.raises(LocationRequired):
        assumptions.assume(p)
    answers.apply_answer(p, "mark-width-d1", "800 mm")
    answers.apply_answer(p, "door-height-d1", 2100)
    answers.apply_answer(p, "beam-depth", "450 mm below the slab (600 mm overall)")
    answers.apply_answer(p, "layer-sand-filling", "700 mm (the drawn layer)")
    applied = answers.apply(p, b)
    typical = next(x for x in applied.plans if x.sheet == TYPICAL)
    d1 = Counter((o.width_mm, o.height_mm) for o in typical.openings if o.mark_normalised == "D1")
    assert d1 == Counter({(800.0, 2100.0): 20})
    assert [s.beam_depth_mm for s in applied.sections] == [600.0, 600.0]
    assert all(("SAND FILLING", 700.0) in s.foundation_layers for s in applied.sections)

    answers.apply_answer(p, "location", {"city": "Bardhaman", "state": "West Bengal"})
    before = len(readiness.ready_to_build(p)["blocking"])
    result = assumptions.assume(p)
    assert result.assumed and all(a.basis for a in result.assumed)
    report = readiness.ready_to_build(p)
    assert len(report["blocking"]) < before and report["ready"] is False
    assert {"tie-beam-depth", "door-height-lift", "roof-structure"} <= {q for q, _ in result.not_assumed}
    assert any(q.startswith("stair_axis:") for q, _ in result.not_assumed)  # T6's questions are not assumed
    assert p.question("door-height-d").answered_by == "assumed"

    # re-running the import keeps every answer
    again = pipeline.run_import(p, store)
    assert p.question("door-height-d1").answer == {"height_mm": 2100.0} and again.plans


def test_cli(capsys, tmp_path):
    assert pipeline.main([str(SAMPLE_DXF), "--root", str(tmp_path)]) == 0
    out = capsys.readouterr().out
    for word in ("SHEETS", "RECOGNITION", "REGISTRATION", "SETTLED ACROSS SHEETS", "QUESTIONS", "BLOCKING",
                 "mark-width-d1 [4 places]", "READY TO BUILD: no"):
        assert word in out, word


def test_answers_make_the_build_plan(tmp_path):
    """Every blocking question answered (rounds: an answer can raise a follow-up) -> T6 makes the plan, and the
    readiness report says ready; project-standards.json is written with the import."""
    store = ImportStore(tmp_path)
    project = store.create(SAMPLE_DXF)
    raw = pipeline.run_import(project, store)
    assert (pipeline.folder(store, project) / "project-standards.json").is_file()
    t6 = json.loads(FIXTURE.read_text(encoding="utf-8"))
    rounds = []
    for _ in range(5):
        todo = [q for q in project.open_questions() if q.severity == "blocking"]
        rounds.append(len(todo))
        if not todo:
            break
        for q in todo:
            if q.id in OWN:
                value = OWN[q.id]
            elif q.id in t6:
                value = t6[q.id]
            elif q.key.get("members"):  # T6 questions asked once for several places: the fixture's per place
                value = {m: t6[m] for m in q.key["members"]}
            elif q.answer_type == "door_height":
                value = 2100
            elif q.answer_type == "window_size":
                value = {"sill_mm": 900, "head_mm": 2100}
            elif q.answer_type == "rooms":
                value = {"one_room": True}
            else:
                raise AssertionError(f"no test answer for {q.id}")
            answers.apply_answer(project, q.id, value)
        questions.update(project, raw)
    assert rounds[-1] == 0 and len(rounds) <= 4, rounds
    result = plan_bridge.to_plan(project, raw)
    assert result.plan is not None and not result.missing
    assert readiness.ready_to_build(project)["ready"] is True
    # asked once: T6's door, window, rooms and host ids are settled by T5's grouped questions
    ids = {q.id for q in project.questions}
    assert not any(i.startswith(("door_height:", "window_sill:", "window_height:", "rooms:", "opening_host:",
                                 "door_width:", "beam_depth:")) for i in ids)
