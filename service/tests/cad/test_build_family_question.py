"""A build that stops on a family choice becomes an import question; its answer reaches the plan."""

from b2m.cad.project import ImportProject
from b2m.imported import stage_questions


def _project() -> ImportProject:
    return ImportProject(id="p", created="2026-11-16T00:00:00Z")


RUN = {"run_id": "r1", "status": "needs_input", "error_code": "FAMILY_CHOICE_NEEDED", "failed_key": "TYPE-window-W",
       "error_data": {"candidates": [{"family_name": "M_Fixed", "eligible": True},
                                     {"family_name": "M_Window-Casement-Double", "eligible": True},
                                     {"family_name": "Skylight", "eligible": False}]}}


def test_a_family_stop_becomes_one_question_with_the_eligible_families():
    p = _project()
    qs = stage_questions.from_build(p, RUN)
    assert [q.id for q in qs] == ["family:window"]
    assert qs[0].options == ["M_Fixed", "M_Window-Casement-Double"] and qs[0].severity == "check"
    assert stage_questions.from_build(p, RUN)[0] is p.question("family:window")  # asked once


def test_other_stops_ask_nothing():
    p = _project()
    assert stage_questions.from_build(p, {**RUN, "status": "built"}) == []
    assert stage_questions.from_build(p, {**RUN, "error_code": "TYPE_NOT_FOUND"}) == []
