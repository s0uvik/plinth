"""Brief agent (PRD F2.1-F2.3, F3.1): Claude turns a written brief into a building spec.

Claude answers only through two tools: ``ask_user`` (essentials missing or unclear) or ``submit_spec``.
Free text is refused and re-prompted once. A submitted spec is validated against the Pydantic schema;
errors go back to Claude as a tool error it can fix, at most twice. Essentials (plot size, floors,
unit mix) may never appear among the defaults it applied: that is treated as inventing them.

    session = BriefSession.start(client, "G+3 on a 15 x 25 m plot, four 2BHKs per floor...")
    outcome = session.outcome            # Questions | SpecReady | BriefFailed
    if isinstance(outcome, Questions):
        session.answer({"plot_depth_mm": "25 m"})
"""

from __future__ import annotations

import json
import secrets
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Protocol

from b2m import paths
from b2m.brief.defaults import ESSENTIALS, defaults_table
from b2m.spec import BuildingSpec, parse_spec

MODEL = "claude-sonnet-5-5"  # PRD: Claude Sonnet 5.5 for brief understanding
MAX_TOKENS = 16000
MAX_SPEC_RETRIES = 2  # F3.1
MAX_FREE_TEXT_NUDGES = 1  # F2.1
FALLBACK_BETA = "server-side-fallback-2026-07-01"

SYSTEM = f"""You turn a written brief for a small apartment block in Kolkata into a building spec for a layout engine.

The engine builds G+3 or G+4 walk-up blocks on rectangular plots: a ground storey (stilt parking or flats) plus 3 or 4 upper floors, 2 to 4 flats per floor, flat types 1BHK, 2BHK and 3BHK, a dog-leg stair and an optional lift.

Reply only by calling a tool. Call submit_spec when you have everything; call ask_user when an essential is missing or unclear. Never reply with plain text.

Essentials, which you must never guess or default: {"; ".join(ESSENTIALS.values())}. If any is missing, ambiguous, or outside what the engine builds, ask for it with ask_user. Put every question in one ask_user call, and ask only about what is missing.

Reading briefs:
- Lengths go in millimetres. 1 ft = 304.8 mm. "40 x 60 ft" is frontage x depth unless the brief says otherwise.
- G+3 means 3 upper floors; G+4 means 4. "An N-storey building" means G+(N-1); note that reading in assumptions.
- A plot given only as an area (for example in katha; 1 katha = 720 sq ft) has no dimensions: ask for frontage and depth.
- A total flat count must divide evenly over the floors that hold flats (upper floors, plus the ground floor when it has flats); if it does not, ask.
- Something the engine cannot build (another city, a basement, more than G+4, more than 4 flats a floor): ask the user to choose something it can build.

When the brief is silent on anything else, use these defaults and list each one you used in defaults_applied with the reason:
{defaults_table()}

Record any other interpretation you made in assumptions, in one short sentence each."""

_UNIT = {
    "type": "object",
    "properties": {
        "type": {"type": "string", "enum": ["1BHK", "2BHK", "3BHK"]},
        "per_floor": {"type": "integer"},
        "target_carpet_m2": {"anyOf": [{"type": "number"}, {"type": "null"}]},
    },
    "required": ["type", "per_floor", "target_carpet_m2"],
    "additionalProperties": False,
}

TOOLS = [
    {
        "name": "submit_spec",
        "description": "Submit the building spec once every essential is known. List every default you applied.",
        "strict": True,
        "input_schema": {
            "type": "object",
            "properties": {
                "plot_width_mm": {"type": "integer", "description": "Frontage along the road"},
                "plot_depth_mm": {"type": "integer"},
                "road_width_mm": {"type": "integer"},
                "ground": {"type": "string", "enum": ["stilt_parking", "flats"]},
                "upper_floors": {"type": "integer", "enum": [3, 4]},
                "floor_to_floor_mm": {"type": "integer"},
                "template": {"type": "string", "enum": ["auto", "T2", "T3", "T4"]},
                "units": {"type": "array", "items": _UNIT},
                "stair_width_mm": {"type": "integer"},
                "lift": {"type": "boolean"},
                "structure": {"type": "string", "enum": ["rcc_frame", "load_bearing"]},
                "chajja_mm": {"type": "integer", "description": "Sunshade depth over outside windows and doors; 0 for none"},
                "roof_parapet_mm": {"type": "integer", "description": "Roof parapet height; 0 for none"},
                "defaults_applied": {"type": "array", "items": {
                    "type": "object",
                    "properties": {"field": {"type": "string"}, "value": {"type": "string"}, "reason": {"type": "string"}},
                    "required": ["field", "value", "reason"], "additionalProperties": False}},
                "assumptions": {"type": "array", "items": {"type": "string"}},
            },
            "required": ["plot_width_mm", "plot_depth_mm", "road_width_mm", "ground", "upper_floors", "floor_to_floor_mm",
                         "template", "units", "stair_width_mm", "lift", "structure", "chajja_mm", "roof_parapet_mm",
                         "defaults_applied", "assumptions"],
            "additionalProperties": False,
        },
    },
    {
        "name": "ask_user",
        "description": "Ask the user for missing or unclear essentials. All questions in one call.",
        "strict": True,
        "input_schema": {
            "type": "object",
            "properties": {"questions": {"type": "array", "items": {
                "type": "object",
                "properties": {"field": {"type": "string"}, "question": {"type": "string"}},
                "required": ["field", "question"], "additionalProperties": False}}},
            "required": ["questions"],
            "additionalProperties": False,
        },
    },
]


class MessagesClient(Protocol):
    """The part of ``anthropic.Anthropic().beta.messages`` the agent uses; tests pass a fake."""

    def create(self, **kwargs: Any) -> Any: ...


@dataclass
class Questions:
    questions: list[dict[str, str]]


@dataclass
class SpecReady:
    spec: BuildingSpec
    defaults: list[dict[str, str]]
    assumptions: list[str]


@dataclass
class BriefFailed:
    errors: list[str]


Outcome = Questions | SpecReady | BriefFailed


def to_spec_dict(tool_input: dict[str, Any]) -> dict[str, Any]:
    """The flat tool input as the nested spec the schema expects."""
    return {
        "schema_version": "1.0",
        "site": {"plot_mm": [tool_input.get("plot_width_mm"), tool_input.get("plot_depth_mm")],
                 "road_side": "front", "city": "kolkata", "road_width_mm": tool_input.get("road_width_mm")},
        "building": {"ground": tool_input.get("ground"), "upper_floors": tool_input.get("upper_floors"),
                     "floor_to_floor_mm": tool_input.get("floor_to_floor_mm"), "template": tool_input.get("template")},
        "units": tool_input.get("units"),
        "core": {"stair": "dog_leg", "stair_width_mm": tool_input.get("stair_width_mm"), "lift": tool_input.get("lift")},
        # Fields a submission may leave out (older transcripts, tests) keep the spec's own defaults.
        "standards": {k: tool_input[k] for k in ("structure", "chajja_mm", "roof_parapet_mm") if tool_input.get(k) is not None},
        "assumptions": list(tool_input.get("assumptions") or [])
                       + [f"{d['field']} defaulted to {d['value']}: {d['reason']}" for d in tool_input.get("defaults_applied") or []],
    }


def check_submission(tool_input: dict[str, Any]) -> tuple[BuildingSpec | None, list[str]]:
    """Validate a submit_spec call: no defaulted essentials, then the spec schema."""
    errors = []
    for d in tool_input.get("defaults_applied") or []:
        name = str(d.get("field", "")).split(".")[0].split("[")[0]
        if name in ESSENTIALS or name in ("plot_mm", "plot"):
            errors.append(f"{d.get('field')}: this is an essential; ask the user with ask_user instead of defaulting it")
    if errors:
        return None, errors
    return parse_spec(to_spec_dict(tool_input))


def _dump(block: Any) -> dict[str, Any]:
    if hasattr(block, "model_dump"):
        return block.model_dump(mode="json", exclude_none=True)
    if isinstance(block, dict):
        return block
    return {k: v for k, v in vars(block).items() if not k.startswith("_")}


@dataclass
class BriefSession:
    client: MessagesClient
    brief: str
    id: str = field(default_factory=lambda: datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ-") + secrets.token_hex(3))
    messages: list[dict[str, Any]] = field(default_factory=list)
    outcome: Outcome | None = None
    model: str = MODEL
    _pending_ask: str | None = None
    _log: list[dict[str, Any]] = field(default_factory=list)

    @classmethod
    def start(cls, client: MessagesClient, brief: str, model: str = MODEL) -> "BriefSession":
        session = cls(client=client, brief=brief, model=model)
        session.messages.append({"role": "user", "content": f"<brief>\n{brief}\n</brief>"})
        session._log.append({"role": "user", "content": brief})
        session.outcome = session._run()
        session.save()
        return session

    def answer(self, answers: dict[str, str] | str) -> Outcome:
        """Answer the pending questions and continue."""
        if self._pending_ask is None:
            raise ValueError("This brief has no open questions.")
        text = answers if isinstance(answers, str) else "\n".join(f"{k}: {v}" for k, v in answers.items())
        self.messages.append({"role": "user", "content": [{"type": "tool_result", "tool_use_id": self._pending_ask, "content": text}]})
        self._log.append({"role": "user", "answers": answers})
        self._pending_ask = None
        self.outcome = self._run()
        self.save()
        return self.outcome

    def _call(self) -> Any:
        return self.client.create(
            model=self.model,
            max_tokens=MAX_TOKENS,
            system=SYSTEM,
            tools=TOOLS,
            tool_choice={"type": "auto", "disable_parallel_tool_use": True},
            output_config={"effort": "medium"},
            cache_control={"type": "ephemeral"},
            betas=[FALLBACK_BETA],
            fallbacks="default",
            messages=self.messages,
        )

    def _run(self) -> Outcome:
        retries = nudges = 0
        while True:
            response = self._call()
            self._log.append({"role": "assistant", "stop_reason": response.stop_reason,
                              "content": [_dump(b) for b in response.content]})
            if response.stop_reason == "refusal":
                return BriefFailed(["Claude declined this brief."])
            if response.stop_reason == "max_tokens":
                return BriefFailed(["Claude's reply was cut off; try a shorter brief."])
            self.messages.append({"role": "assistant", "content": response.content})
            calls = [b for b in response.content if getattr(b, "type", None) == "tool_use"]
            if not calls:
                nudges += 1
                if nudges > MAX_FREE_TEXT_NUDGES:
                    return BriefFailed(["Claude did not call submit_spec or ask_user."])
                self.messages.append({"role": "user", "content": "Reply only by calling submit_spec or ask_user."})
                continue

            call = calls[0]
            if call.name == "ask_user":
                questions = [q for q in (call.input or {}).get("questions", []) if q.get("question")]
                if questions:
                    self._pending_ask = call.id
                    return Questions(questions)
                result, error = "ask_user needs at least one question.", True
            elif call.name == "submit_spec":
                spec, errors = check_submission(call.input or {})
                if spec is not None:
                    self.messages.append({"role": "user", "content": [
                        {"type": "tool_result", "tool_use_id": call.id, "content": "Spec accepted."}]})
                    return SpecReady(spec, list(call.input.get("defaults_applied") or []), list(call.input.get("assumptions") or []))
                retries += 1
                if retries > MAX_SPEC_RETRIES:
                    return BriefFailed(errors)
                result, error = "The spec is not valid:\n" + "\n".join(errors), True
            else:
                result, error = f"Unknown tool {call.name}; use submit_spec or ask_user.", True
            self.messages.append({"role": "user", "content": [
                {"type": "tool_result", "tool_use_id": call.id, "content": result, "is_error": error}]})

    def save(self) -> None:
        """Keep the brief, every exchange and the outcome (PRD F10.1)."""
        folder = paths.root() / "briefs"
        folder.mkdir(parents=True, exist_ok=True)
        outcome: dict[str, Any] = {"kind": type(self.outcome).__name__}
        if isinstance(self.outcome, SpecReady):
            outcome.update(spec=self.outcome.spec.model_dump(mode="json"), defaults=self.outcome.defaults)
        elif isinstance(self.outcome, Questions):
            outcome["questions"] = self.outcome.questions
        elif isinstance(self.outcome, BriefFailed):
            outcome["errors"] = self.outcome.errors
        record = {"id": self.id, "model": self.model, "brief": self.brief, "exchanges": self._log, "outcome": outcome}
        (folder / f"{self.id}.json").write_text(json.dumps(record, indent=2, ensure_ascii=False, default=str), encoding="utf-8")


def anthropic_client() -> MessagesClient:
    """The real client. Credentials come from the environment (ANTHROPIC_API_KEY or an ant profile)."""
    import anthropic

    return anthropic.Anthropic().beta.messages
