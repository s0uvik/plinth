import json
from types import SimpleNamespace

import pytest

from b2m.brief.agent import MODEL, BriefFailed, BriefSession, Questions, SpecReady, check_submission

GOOD = {
    "plot_width_mm": 15000, "plot_depth_mm": 25000, "road_width_mm": 9000, "ground": "stilt_parking",
    "upper_floors": 3, "floor_to_floor_mm": 3000, "template": "auto",
    "units": [{"type": "2BHK", "per_floor": 2, "target_carpet_m2": None}],
    "stair_width_mm": 1200, "lift": False,
    "defaults_applied": [{"field": "floor_to_floor_mm", "value": "3000", "reason": "Typical residential floor height"}],
    "assumptions": [],
}


def tool(name, input, id="t1"):
    return SimpleNamespace(type="tool_use", name=name, input=input, id=id)


def reply(*blocks, stop="tool_use"):
    return SimpleNamespace(stop_reason=stop, content=list(blocks))


class FakeClaude:
    """Returns scripted replies and records every request."""

    def __init__(self, *replies):
        self.replies = list(replies)
        self.requests = []

    def create(self, **kwargs):
        self.requests.append(json.loads(json.dumps(kwargs, default=lambda o: vars(o))))
        return self.replies.pop(0)


@pytest.fixture(autouse=True)
def home(tmp_path, monkeypatch):
    monkeypatch.setenv("B2M_HOME", str(tmp_path))
    return tmp_path


def test_complete_brief_gives_a_spec_with_its_defaults(home):
    claude = FakeClaude(reply(tool("submit_spec", GOOD)))
    session = BriefSession.start(claude, "G+3 stilt, 15 x 25 m plot, 9 m road, two 2BHKs per floor")
    assert isinstance(session.outcome, SpecReady)
    assert session.outcome.spec.site.plot_mm == (15000, 25000)
    assert session.outcome.defaults[0]["field"] == "floor_to_floor_mm"
    assert any("floor_to_floor_mm defaulted" in a for a in session.outcome.spec.assumptions)
    request = claude.requests[0]
    assert request["model"] == MODEL == "claude-sonnet-5-5"
    assert request["tool_choice"]["type"] == "auto" and request["fallbacks"] == "default"
    assert {t["name"] for t in request["tools"]} == {"submit_spec", "ask_user"}
    saved = json.loads((home / "briefs" / f"{session.id}.json").read_text(encoding="utf-8"))
    assert saved["outcome"]["kind"] == "SpecReady" and saved["brief"].startswith("G+3")


def test_missing_essentials_are_asked_then_answered(home):
    claude = FakeClaude(
        reply(tool("ask_user", {"questions": [{"field": "plot_depth_mm", "question": "How deep is the plot?"}]}, id="ask1")),
        reply(tool("submit_spec", GOOD, id="sub1")),
    )
    session = BriefSession.start(claude, "G+3 on a 15 m wide plot, two 2BHKs per floor")
    assert isinstance(session.outcome, Questions)
    assert session.outcome.questions[0]["field"] == "plot_depth_mm"
    outcome = session.answer({"plot_depth_mm": "25 m"})
    assert isinstance(outcome, SpecReady)
    answered = claude.requests[1]["messages"][-1]["content"][0]
    assert answered == {"type": "tool_result", "tool_use_id": "ask1", "content": "plot_depth_mm: 25 m"}
    with pytest.raises(ValueError):
        session.answer("again")


def test_invalid_spec_goes_back_as_a_fixable_error():
    bad = {**GOOD, "units": [{"type": "2BHK", "per_floor": 5, "target_carpet_m2": None}]}
    claude = FakeClaude(reply(tool("submit_spec", bad, id="s1")), reply(tool("submit_spec", GOOD, id="s2")))
    session = BriefSession.start(claude, "brief")
    assert isinstance(session.outcome, SpecReady)
    error = claude.requests[1]["messages"][-1]["content"][0]
    assert error["is_error"] is True and "units.0.per_floor" in error["content"]


def test_three_invalid_specs_fail_after_two_retries():
    bad = {**GOOD, "plot_width_mm": 3000}
    claude = FakeClaude(*[reply(tool("submit_spec", bad, id=f"s{i}")) for i in range(3)])
    session = BriefSession.start(claude, "brief")
    assert isinstance(session.outcome, BriefFailed)
    assert len(claude.requests) == 3
    assert any("plot_mm" in e for e in session.outcome.errors)


def test_defaulting_an_essential_is_refused():
    invented = {**GOOD, "defaults_applied": [{"field": "plot_depth_mm", "value": "25000", "reason": "typical"}]}
    spec, errors = check_submission(invented)
    assert spec is None and "ask the user" in errors[0]
    _, errors = check_submission({**GOOD, "defaults_applied": [{"field": "units[0].per_floor", "value": "2", "reason": "x"}]})
    assert errors


def test_free_text_is_nudged_once_then_fails():
    text = SimpleNamespace(type="text", text="Here is your building!")
    claude = FakeClaude(reply(text, stop="end_turn"), reply(tool("submit_spec", GOOD)))
    assert isinstance(BriefSession.start(claude, "brief").outcome, SpecReady)
    assert claude.requests[1]["messages"][-1]["content"] == "Reply only by calling submit_spec or ask_user."

    claude = FakeClaude(reply(text, stop="end_turn"), reply(text, stop="end_turn"))
    assert isinstance(BriefSession.start(claude, "brief").outcome, BriefFailed)


def test_refusal_and_truncation_fail_cleanly():
    assert isinstance(BriefSession.start(FakeClaude(reply(stop="refusal")), "brief").outcome, BriefFailed)
    assert isinstance(BriefSession.start(FakeClaude(reply(stop="max_tokens")), "brief").outcome, BriefFailed)


def test_gate_briefs_are_well_formed_and_their_expected_specs_lay_out():
    """Every gate brief's expected essentials must describe a building the engine can lay out."""
    from pathlib import Path

    import yaml

    from b2m.engine import generate
    from b2m.spec import BuildingSpec

    briefs = yaml.safe_load((Path(__file__).parent / "briefs" / "briefs.yaml").read_text(encoding="utf-8"))
    assert len(briefs) == 20 and len({b["id"] for b in briefs}) == 20
    assert sum("answers" in b for b in briefs) == 4
    for b in briefs:
        e = b["expect"]
        spec = BuildingSpec.model_validate({
            "site": {"plot_mm": e["plot"], "road_width_mm": 9000},
            "building": {"ground": e["ground"], "upper_floors": e["upper_floors"]},
            "units": [{"type": t, "per_floor": n} for t, n in e["units"].items()],
            "core": {"lift": e["lift"]},
        })
        generate(spec)  # raises LayoutError if the brief's building cannot be laid out
