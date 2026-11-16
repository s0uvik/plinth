"""T5 on synthetic buildings: cross-sheet resolution, grouped questions, typed answers, location profiles and the
assume step, readiness."""

import pytest

from b2m.cad import answers, assumptions, crosssheet, questions, readiness
from b2m.cad.answers import AnswerError
from b2m.cad.building import (Evidence, Finding, Level, Opening, PlanRecognition, RecognisedBuilding, Registration,
                              Room, SectionRecognition, Shaft, Wall)
from b2m.cad.project import ImportProject, LocationRequired

FRAME, GROUND = "typ.dxf", "ground.dxf"


def ev(sheet, *handles):
    return Evidence(sheet=sheet, handles=list(handles))


def _finding(fid, topic, text, sheet, *handles, severity="check", options=()):
    return Finding(id=fid, topic=topic, text=text, severity=severity, evidence=ev(sheet, *handles),
                   options=list(options))


def _section(name="AA", storeys=3):
    levels = [Level(name=f"storey {i}", elevation_mm=3000.0 * i, kind="floor", storey=i, evidence=ev(f"{name}.dxf"))
              for i in range(storeys)]
    return SectionRecognition(sheet=f"{name}.dxf", name=name, datum="PL", levels=levels,
                              storey_heights_mm=[3000.0] * storeys, slab_thickness_mm=150)


def lift_building() -> RecognisedBuilding:
    """The typical plan labels a lift shaft; the ground plan, drawn 1000/500 mm off, has an unmarked opening in the
    shaft's front wall (and another one elsewhere)."""
    typ = PlanRecognition(
        sheet=FRAME, role="floor_plan", storeys=[1, 2],
        walls=[Wall(id="w1", start=(0, -125), end=(2000, -125), thickness_mm=250, evidence=ev(FRAME, "a"))],
        shafts=[Shaft(id="s1", kind="lift", outline=[(0, 0), (2000, 0), (2000, 2000), (0, 2000)], label="LIFT",
                      evidence=ev(FRAME, "L"))])
    ground = PlanRecognition(
        sheet=GROUND, role="floor_plan", storeys=[0],
        walls=[Wall(id="w9", start=(1000, 375), end=(3000, 375), thickness_mm=250, evidence=ev(GROUND, "b")),
               Wall(id="w8", start=(9000, 375), end=(11000, 375), thickness_mm=250, evidence=ev(GROUND, "c"))],
        openings=[Opening(id="o1", kind="window", wall_id="w9", center=(2000, 375), width_mm=1000,
                          evidence=ev(GROUND, "g1", "g2")),
                  Opening(id="o2", kind="window", wall_id="w8", center=(10000, 375), width_mm=1000,
                          evidence=ev(GROUND, "g3"))],
        findings=[_finding("owm-1", "opening_without_mark", "The window o1 has no mark.", GROUND, "g1"),
                  _finding("owm-2", "opening_without_mark", "The window o2 has no mark.", GROUND, "g3")])
    regs = [Registration(sheet=FRAME, method="frame", residual_mm=0, evidence=ev(FRAME)),
            Registration(sheet=GROUND, translation=(-1000, -500), method="columns", residual_mm=0.3,
                         evidence=ev(GROUND))]
    return RecognisedBuilding(plans=[typ, ground], sections=[_section(storeys=3)], registrations=regs)


def test_lift_door_settled_across_sheets():
    b = lift_building()
    crosssheet.run(b)
    ground = b.plans[1]
    o1, o2 = ground.openings
    assert (o1.kind, o1.swing) == ("door", "sliding") and o1.evidence.note.startswith("lift landing door")
    assert o2.kind == "window"  # nowhere near a lift
    assert [f.id for f in ground.findings] == ["owm-2"]
    (res,) = b.resolved
    assert res.findings == [f"{GROUND}#owm-1"]
    assert {e.sheet for e in res.evidence} == {FRAME, GROUND}
    # the plans cover storeys 0-2 and the section shows 3 storeys: they agree (said, not asked)
    assert [f.severity for f in b.findings if f.topic == "storeys"] == ["info"]


def test_storeys_disagree_is_blocking():
    b = lift_building()
    b.sections = [_section(storeys=5)]
    crosssheet.run(b)
    (f,) = [f for f in b.findings if f.topic == "storeys"]
    assert f.severity == "blocking" and "no floor plan covers storeys 3, 4" in f.text and len(f.options) == 3


def grouped_building() -> RecognisedBuilding:
    rooms = [Room(id=f"r{i}", name=n, point=(i * 1000.0, 0), evidence=ev(FRAME, f"h{i}"))
             for i, n in enumerate(["OPEN KITCHEN", "DINING", "KITCHEN OPEN", "DINING", "TOILET"])]
    ops = [Opening(id=f"d{i}", kind="door", wall_id=None, center=(i * 1000.0, 5000), width_mm=w, mark="D1",
                   mark_normalised="D1", evidence=ev(FRAME, f"o{i}")) for i, w in enumerate([800, 800, 800, 900])]
    ops.append(Opening(id="w0", kind="window", wall_id=None, center=(0, 9000), width_mm=1200, mark="W1",
                       mark_normalised="W1", evidence=ev(FRAME, "ow")))
    plan = PlanRecognition(
        sheet=FRAME, role="floor_plan", storeys=[0], rooms=rooms, openings=ops,
        findings=[_finding("rss-1", "rooms_share_space", 'Rooms "OPEN KITCHEN", "DINING" share (20.5 m2).', FRAME,
                           "h0", "h1"),
                  _finding("rss-2", "rooms_share_space", 'Rooms "DINING", "KITCHEN OPEN" share (20.7 m2).', FRAME,
                           "h2", "h3"),
                  _finding("mwm-1", "mark_width_mismatch", "Openings marked D1 are drawn at different widths.",
                           FRAME, "o3", options=["800 mm", "900 mm"]),
                  _finding("rne-1", "room_not_enclosed", '"YARD" is not enclosed by walls.', FRAME, "x1",
                           severity="info"),
                  _finding("rne-2", "room_not_enclosed", '"PARKING 2" is not enclosed by walls.', FRAME, "x2",
                           severity="info")])
    return RecognisedBuilding(plans=[plan], sections=[_section(storeys=1)],
                              registrations=[Registration(sheet=FRAME, method="frame", residual_mm=0,
                                                          evidence=ev(FRAME))])


@pytest.fixture
def project_and_building():
    project = ImportProject(id="t", created="now")
    b = grouped_building()
    questions.update(project, b)
    return project, b


def test_findings_are_grouped(project_and_building):
    project, _ = project_and_building
    ids = {q.id: q for q in project.questions}
    rooms = [q for q in project.questions if q.topic == "rooms_share_space"]
    assert len(rooms) == 1 and len(rooms[0].instances) == 2 and rooms[0].severity == "blocking"
    assert 'also written "KITCHEN OPEN"' in rooms[0].text
    mark = ids["mark-width-d1"]
    assert mark.options == ["800 mm", "900 mm", "two types: D1 800 mm and a new mark for the 900 mm ones"]
    assert mark.findings == [f"{FRAME}#mwm-1"] and len(mark.instances) == 1
    (enclosed,) = [q for q in project.questions if q.topic == "room_not_enclosed"]
    assert enclosed.severity == "info" and len(enclosed.instances) == 2
    # inputs every import needs
    assert ids["location"].severity == "blocking" and ids["location"].answer_type == "location"
    assert ids["rule-set"].severity == "check"
    assert {"door-height-d1", "window-size-w1", "wall-height", "lintel"} <= set(ids)
    assert "slab-thickness" not in ids  # the section gives it
    # blocking first
    sev = [q.severity for q in project.questions]
    assert sev == sorted(sev, key=questions.SEVERITIES.index)


def test_answers_change_the_building(project_and_building):
    project, b = project_and_building
    with pytest.raises(AnswerError):
        answers.apply_answer(project, "mark-width-d1", "two types: D1 800 mm and a new mark for the 900 mm ones")
    answers.apply_answer(project, "mark-width-d1", {"split": True, "new_mark": "D2"})
    assert "door-height-d2" in {q.id for q in project.questions}  # a new mark needs its height asked
    answers.apply_answer(project, "door-height-d1", 2100)
    answers.apply_answer(project, "window-size-w1", {"sill_mm": 900, "head_mm": 2100})
    room_q = next(q for q in project.questions if q.topic == "rooms_share_space")
    with pytest.raises(AnswerError):  # separate rooms need lines for every place
        answers.apply_answer(project, room_q.id, {"one_room": False, "lines": {room_q.key["instances"][0]["ref"]:
                                                                                [[0, 0, 0, 1000]]}})
    answers.apply_answer(project, room_q.id, {"one_room": True, "name": "LIVING / KITCHEN"})
    applied = answers.apply(project, b)
    ops = {o.id: o for o in applied.plans[0].openings}
    assert ops["d3"].mark_normalised == "D2" and ops["d0"].mark_normalised == "D1"
    assert ops["d0"].height_mm == 2100 and ops["d3"].height_mm is None
    assert (ops["w0"].sill_mm, ops["w0"].height_mm) == (900, 1200)
    names = [r.name for r in applied.plans[0].rooms]
    assert names.count("LIVING / KITCHEN") == 2 and len(names) == 3
    assert b.plans[0].openings[3].mark_normalised == "D1"  # the recognised building is never changed in place
    q = project.question("door-height-d1")
    assert q.answered_by == "user" and q.at


def test_assume_needs_the_location_then_records_basis(project_and_building):
    project, _ = project_and_building
    with pytest.raises(LocationRequired):
        assumptions.assume(project)
    assert project.assumptions == []
    project.set_location(city="Springfield", state="Nowhere")
    with pytest.raises(assumptions.ProfileRequired):
        assumptions.assume(project)
    answers.apply_answer(project, "location", {"city": "Bardhaman", "state": "West Bengal"})
    assert project.question("rule-set").key["derived"] == ["nbc2016", "wb-municipal-2007"]
    result = assumptions.assume(project)
    by_q = {a.question: a for a in result.assumed}
    assert by_q["door-height-d1"].text == "Doors marked D1 are 2100 mm high (office practice, confirm)."
    assert "office practice, confirm" in by_q["door-height-d1"].basis and "West Bengal" in by_q["door-height-d1"].basis
    assert "rule-set" in by_q and "West Bengal Municipal (Building) Rules, 2007" in by_q["rule-set"].text
    assert project.inputs.rule_set == "nbc2016+wb-municipal-2007"
    not_assumed = dict(result.not_assumed)
    assert "mark-width-d1" in not_assumed and any(q.startswith("rooms-share-space") for q in not_assumed)
    assert project.question("door-height-d1").answered_by == "assumed"


def test_kolkata_takes_the_kmc_rules():
    loc = ImportProject(id="k", created="now")
    loc.set_location(city="Calcutta", state="WB")
    prof, refs = assumptions.governing(loc.inputs.location)
    assert prof.id == "west-bengal" and [r.id for r in refs] == ["nbc2016", "kmc2009"]
    loc.set_location(city="Pune", state="Maharashtra")
    prof, refs = assumptions.governing(loc.inputs.location)
    assert prof.id == "india-nbc2016" and [r.id for r in refs] == ["nbc2016"]


def test_every_profile_value_has_its_basis():
    for prof in assumptions.profiles().values():
        for v in prof.values.values():
            assert v.basis and (v.source or v.basis.startswith(assumptions.OFFICE)), (prof.id, v.key)
    with pytest.raises(assumptions.ProfileError):
        assumptions._value("door_height_mm", {"value": 2100, "basis": "usual"}, "test")


def test_location_suggestions_from_text():
    found = assumptions.suggest_locations([("site.dxf", "1", "NEAR BURDWAN STATION"), ("site.dxf", "2", "GT ROAD"),
                                           ("site.dxf", "3", "TO DURGAPUR")])
    assert [s.label for s in found] == ["Bardhaman, West Bengal", "Durgapur, West Bengal"]
    assert found[0].mentions == [("site.dxf", "1", "NEAR BURDWAN STATION")]


def test_readiness_flips_when_blocking_questions_are_answered(project_and_building):
    project, _ = project_and_building
    report = readiness.ready_to_build(project)
    assert report["ready"] is False and report["blocking"] and report["approvals_needed"]
    answers.apply_answer(project, "location", {"city": "Bardhaman", "state": "West Bengal"})
    assumptions.assume(project)
    for q in project.open_questions():
        if q.severity != "blocking":
            continue
        if q.answer_type == "mark_width":
            answers.apply_answer(project, q.id, {"width_mm": 800})
        elif q.answer_type == "rooms":
            answers.apply_answer(project, q.id, {"one_room": True})
        elif q.answer_type == "door_height":
            answers.apply_answer(project, q.id, 2000)
        else:
            answers.apply_answer(project, q.id, q.options[0] if q.options else "as drawn")
    report = readiness.ready_to_build(project)
    assert report["ready"] is True and report["blocking"] == [] and report["can_build"] is False
    assert report["to_confirm"] and "confirming" in report["approvals_needed"][0]
    project.approve("review")
    assert readiness.ready_to_build(project)["can_build"] is True
