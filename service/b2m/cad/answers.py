"""Answers to import questions (PRD Step 9, T5): typed, checked, recorded, and applied to the recognised building.

Every question carries an ``answer_type``; ``answer`` checks the answer against it (and against the question's
options when it is a choice), records it in the project (by the user, or assumed with its basis: the project
refuses assumptions until the location is known), and ``apply`` returns a copy of the recognised building with
every recorded answer applied, in question order. The building from the drawings is never changed in place, so
applying is repeatable and an answer can be changed.

Answer types (what ``answer`` accepts):

- ``location``: {"city", "municipality", "state"} (a city or a municipality at least); sets the project location.
- ``rule_set``: {"rule_sets": ["nbc2016", ...]}, a list of rule-set ids, or one of the options; sets the
  project's rule set.
- ``choice``: one of the options (its text or its index).
- ``choice_or_text``: one of the options, or the user's own words.
- ``mark_width``: {"width_mm": 800} or {"split": true, "new_marks": {"900": "D1A"}} (or "new_mark" when there is
  one other width); an option's text or index is accepted for a width.
- ``rooms``: {"one_room": true, "name": "LIVING / DINING"}, {"skip": true}, or {"lines": {"<instance>":
  [[x1, y1, x2, y2], ...]}} with separation lines (sheet coordinates, mm) for every instance of the question.
- ``opening_host``: "skip" or {"mode": "new_wall", "thickness_mm": 250, "function": "interior"}.
- ``plan_input``: a value the build-plan converter (T6) asks for under its own id: a length in mm (a number, or
  "450 mm"), one of its options, or its dict form ({"mode": "one_room", ...}).
- ``door_height``: {"height_mm": 2100}.
- ``window_size``: {"sill_mm": 900, "head_mm": 2100}.
- ``length``: {"value_mm": 300}, a number, or an option such as "700 mm (the drawn layer)".
- ``opening_mark``: {"mark": "D2"} (an option such as "D (900 mm)" is accepted too).
- ``text``: the user's words.
"""

from __future__ import annotations

import re
from typing import Any, Callable

from pydantic import BaseModel, ConfigDict, Field, ValidationError, model_validator

from b2m.cad.building import Evidence, Opening, RecognisedBuilding, Room, RoomSeparation
from b2m.cad.project import ImportProject, Question


class AnswerError(ValueError):
    pass


class _A(BaseModel):
    model_config = ConfigDict(extra="forbid")


class LocationAnswer(_A):
    city: str | None = None
    municipality: str | None = None
    state: str | None = None

    @model_validator(mode="after")
    def _some_place(self):
        if not (self.city or self.municipality):
            raise ValueError("a location needs at least a city or a municipality")
        return self


class RuleSetAnswer(_A):
    rule_sets: list[str] = Field(min_length=1)


class MarkWidthAnswer(_A):
    width_mm: float | None = Field(default=None, gt=0)
    split: bool = False
    new_marks: dict[str, str] = {}  # drawn width (as written, e.g. "900") -> the new mark for those openings

    @model_validator(mode="after")
    def _one_way(self):
        if self.split == (self.width_mm is not None):
            raise ValueError("give either width_mm (one width for the mark) or split with new_marks")
        return self


class RoomsAnswer(_A):
    one_room: bool = False
    skip: bool = False  # leave these rooms out of the model
    name: str | None = None
    lines: dict[str, list[tuple[float, float, float, float]]] = {}


class DoorHeightAnswer(_A):
    height_mm: float = Field(gt=0)


class WindowSizeAnswer(_A):
    sill_mm: float = Field(ge=0)
    head_mm: float = Field(gt=0)

    @model_validator(mode="after")
    def _order(self):
        if self.head_mm <= self.sill_mm:
            raise ValueError("the head must be above the sill")
        return self


class LengthAnswer(_A):
    value_mm: float = Field(gt=0)


class OpeningMarkAnswer(_A):
    mark: str = Field(min_length=1)


_NUMBER = re.compile(r"(\d+(?:\.\d+)?)\s*mm")


def _option(q: Question, value: Any) -> str | None:
    """The option the value names (its index or its text, case and spacing ignored), else None."""
    if isinstance(value, bool):
        return None
    if isinstance(value, int) and 0 <= value < len(q.options):
        return q.options[value]
    if isinstance(value, str):
        norm = " ".join(value.split()).lower()
        for o in q.options:
            if " ".join(o.split()).lower() == norm:
                return o
    return None


def _first_mm(text: str) -> float | None:
    m = _NUMBER.search(text)
    return float(m.group(1)) if m else None


def normalise(q: Question, value: Any) -> Any:
    """The answer in its canonical form for the question's type, or AnswerError saying what is expected."""
    t = q.answer_type
    try:
        if t == "location":
            return LocationAnswer.model_validate(value).model_dump()
        if t == "rule_set":
            if isinstance(value, list):
                value = {"rule_sets": value}
            elif not isinstance(value, dict):
                opt = _option(q, value)
                ids = (q.key.get("option_ids") or {}).get(opt) if opt else None
                if not ids:
                    raise AnswerError(f"{q.id}: give the rule sets as a list of ids, or one of the options")
                value = {"rule_sets": ids}
            return RuleSetAnswer.model_validate(value).model_dump()
        if t == "choice":
            opt = _option(q, value.get("choice") if isinstance(value, dict) else value)
            if opt is None:
                raise AnswerError(f"{q.id}: choose one of {q.options}")
            return opt
        if t == "choice_or_text":
            opt = _option(q, value)
            if opt is not None:
                return opt
            if isinstance(value, str) and value.strip():
                return value.strip()
            raise AnswerError(f"{q.id}: choose one of {q.options} or say it in words")
        if t == "mark_width":
            if not isinstance(value, dict):
                opt = _option(q, value)
                if opt is None:
                    raise AnswerError(f"{q.id}: give {{'width_mm': ...}}, or split with new marks")
                if opt.startswith(("two types", "separate types")):
                    raise AnswerError(f"{q.id}: splitting the mark needs the new mark(s): {{'split': true, "
                                      "'new_marks': {'<width>': '<mark>'}}")
                value = {"width_mm": _first_mm(opt)}
            value = dict(value)
            if "new_mark" in value:
                others = [w for w in q.key.get("widths", []) if w != q.key.get("keep")]
                if len(others) != 1:
                    raise AnswerError(f"{q.id}: more than one other width; give new_marks by width")
                value.setdefault("new_marks", {f"{others[0]:g}": value.pop("new_mark")})
                value.setdefault("split", True)
            a = MarkWidthAnswer.model_validate(value)
            widths = [float(w) for w in q.key.get("widths", [])]
            if a.width_mm is not None and widths and a.width_mm not in widths:
                raise AnswerError(f"{q.id}: {a.width_mm:g} mm is not one of the drawn widths {widths}")
            if a.split:
                missing = [w for w in widths if w != q.key.get("keep") and f"{w:g}" not in a.new_marks]
                if missing:
                    raise AnswerError(f"{q.id}: give a new mark for the {', '.join(f'{w:g}' for w in missing)} mm ones")
            return a.model_dump()
        if t == "rooms":
            if isinstance(value, str) and value.startswith("skip"):
                value = {"skip": True}
            elif isinstance(value, str) and value.startswith("one room"):
                value = {"one_room": True}
            a = RoomsAnswer.model_validate(value)
            if not a.one_room and not a.skip:
                refs = [i["ref"] for i in q.key.get("instances", [])]
                missing = [r for r in refs if not a.lines.get(r)]
                if missing:
                    raise AnswerError(f"{q.id}: give the separation lines for every place: {missing}")
            return a.model_dump()
        if t == "door_height":
            if isinstance(value, (int, float)):
                value = {"height_mm": value}
            elif isinstance(value, str):
                opt = _option(q, value)
                value = {"height_mm": _first_mm(opt or value)}
            return DoorHeightAnswer.model_validate(value).model_dump()
        if t == "window_size":
            if isinstance(value, (str, int)) and not isinstance(value, bool):
                opt = _option(q, value)
                nums = [float(n) for n in re.findall(r"(\d+(?:\.\d+)?)\s*mm", opt or "")]
                if not opt or len(nums) < 2:
                    raise AnswerError(f"{q.id}: give {{'sill_mm': ..., 'head_mm': ...}}")
                value = {"sill_mm": nums[0], "head_mm": nums[1]}
            return WindowSizeAnswer.model_validate(value).model_dump()
        if t == "length":
            if isinstance(value, (int, float)) and not isinstance(value, bool):
                value = {"value_mm": value}
            elif isinstance(value, str):
                opt = _option(q, value)
                value = {"value_mm": _first_mm(opt or value)}
            return LengthAnswer.model_validate(value).model_dump()
        if t == "opening_mark":
            if isinstance(value, str):
                opt = _option(q, value)
                value = {"mark": (opt or value).split(" (")[0].strip()}
            return OpeningMarkAnswer.model_validate(value).model_dump()
        if t == "opening_host":
            if isinstance(value, str) and value.lower().startswith("skip"):
                return "skip"
            if isinstance(value, dict) and value.get("mode") == "new_wall":
                thickness = value.get("thickness_mm")
                if not isinstance(thickness, (int, float)) or thickness <= 0 or \
                        value.get("function") not in ("exterior", "interior"):
                    raise AnswerError(f"{q.id}: a new wall needs thickness_mm and function (exterior or interior)")
                return {"mode": "new_wall", "thickness_mm": float(thickness), "function": value["function"]}
            raise AnswerError(f"{q.id}: answer \"skip\" or {{'mode': 'new_wall', 'thickness_mm': ..., "
                              "'function': 'interior'}")
        if t == "plan_input":
            raw = q.key.get("options_raw") or []
            if isinstance(value, str):
                for text, o in zip(q.options, raw):
                    if " ".join(text.split()).lower() == " ".join(value.split()).lower():
                        value = o
                        break
            if q.key.get("unit") == "mm":
                n = value if isinstance(value, (int, float)) and not isinstance(value, bool) else \
                    (_first_mm(value) if isinstance(value, str) else None)
                if n is None and isinstance(value, str):
                    try:
                        n = float(value)
                    except ValueError:
                        n = None
                if n is None or n <= 0:
                    raise AnswerError(f"{q.id}: give a length in mm")
                return float(n)
            if isinstance(value, dict) and value.get("mode") in ("one_room", "separate", "new_wall"):
                return value
            if isinstance(value, str) and value.strip():
                return value.strip()
            raise AnswerError(f"{q.id}: choose one of {q.options}")
        if t == "text":
            if not isinstance(value, str) or not value.strip():
                raise AnswerError(f"{q.id}: say it in words")
            return value.strip()
        if t == "stage_value":  # a drawing-set or estimate input: one of the options, or the value itself
            opt = _option(q, value)
            if opt is not None:
                return opt
            if value is None or (isinstance(value, str) and not value.strip()) or value in ([], {}):
                raise AnswerError(f"{q.id}: give a value" + (f", or one of {q.options}" if q.options else ""))
            return value.strip() if isinstance(value, str) else value
    except ValidationError as exc:
        raise AnswerError(f"{q.id}: {exc.errors()[0]['msg']}") from exc
    raise AnswerError(f"{q.id}: unknown answer type {t}")


ANSWER_TYPES = ["location", "rule_set", "choice", "choice_or_text", "mark_width", "rooms", "door_height",
                "window_size", "length", "opening_mark", "opening_host", "plan_input", "text", "stage_value"]


def answer(project: ImportProject, question_id: str, value: Any, by: str = "user", basis: str | None = None,
           assumption: str | None = None) -> Question:
    """Check and record an answer (the user's, or assumed with its basis). The location and rule-set answers also
    set the project's inputs."""
    q = project.question(question_id)
    canonical = normalise(q, value)
    project.answer(q.id, canonical, by=by, basis=basis, assumption=assumption)
    if q.answer_type == "location":
        project.set_location(**canonical)
    elif q.answer_type == "rule_set":
        project.inputs.rule_set = "+".join(canonical["rule_sets"])
    return q


def apply_answer(project: ImportProject, question_id: str, value: Any, by: str = "user",
                 basis: str | None = None) -> Question:
    """Record an answer (``answer``) and add the questions it raises (a new mark needs its heights)."""
    from b2m.cad import questions

    q = answer(project, question_id, value, by=by, basis=basis)
    questions.follow_ups(project, q)
    questions.sync_inputs(project)
    return q


# ---------------------------------------------------------------- applying

def apply(project: ImportProject, building: RecognisedBuilding, rooms: bool = True) -> RecognisedBuilding:
    """A copy of the building with every answered question applied (questions in order). ``rooms=False`` leaves
    the rooms as drawn (the build-plan converter applies room answers itself, from its answer map)."""
    b = building.model_copy(deep=True)
    for q in project.questions:
        if q.open or q.answer is None:
            continue
        fn = APPLY.get(q.answer_type)
        if q.answer_type == "rooms" and not rooms:
            fn = None
        if fn:
            fn(b, q, q.answer)
        b.answered.append(q.id)
    return b


def _openings(b: RecognisedBuilding, pred: Callable[[Opening], bool]):
    for p in b.plans:
        for o in p.openings:
            if pred(o):
                yield p, o


def _note(o: Opening, text: str) -> None:
    o.evidence = o.evidence.model_copy(update={"note": f"{o.evidence.note}; {text}" if o.evidence.note else text})


def _apply_mark_width(b: RecognisedBuilding, q: Question, a: dict) -> None:
    mark, kind = q.key["mark"], q.key.get("kind")
    for _, o in _openings(b, lambda o: o.mark_normalised == mark and (kind is None or o.kind == kind)):
        if a.get("width_mm") is not None:
            if o.width_mm != a["width_mm"]:
                _note(o, f"width {a['width_mm']:g} mm by answer {q.id} (drawn {o.width_mm:g} mm)")
                o.width_mm = a["width_mm"]
        else:
            new = a["new_marks"].get(f"{o.width_mm:g}")
            if new:
                _note(o, f"mark {new} by answer {q.id} (drawn as {o.mark})")
                o.mark, o.mark_normalised = new, new.upper()


def _apply_opening_mark(b: RecognisedBuilding, q: Question, a: dict) -> None:
    targets = {(s, i) for s, i in q.key.get("openings", [])}
    for p, o in _openings(b, lambda o: True):
        if (p.sheet, o.id) in targets:
            _note(o, f"mark {a['mark']} by answer {q.id}")
            o.mark, o.mark_normalised = a["mark"], a["mark"].upper()


def _apply_door_height(b: RecognisedBuilding, q: Question, a: dict) -> None:
    if q.key.get("lift"):
        pred = lambda o: o.kind == "door" and o.swing == "sliding" and (o.evidence.note or "").startswith(  # noqa: E731
            "lift landing door")
    else:
        pred = lambda o: o.kind == "door" and o.mark_normalised == q.key["mark"]  # noqa: E731
    for _, o in _openings(b, pred):
        o.height_mm = a["height_mm"]


def _apply_window_size(b: RecognisedBuilding, q: Question, a: dict) -> None:
    kinds = set(q.key.get("kinds") or ["window", "ventilator"])
    for _, o in _openings(b, lambda o: o.kind in kinds and o.mark_normalised == q.key["mark"]):
        o.sill_mm, o.height_mm = a["sill_mm"], round(a["head_mm"] - a["sill_mm"], 1)


def _apply_rooms(b: RecognisedBuilding, q: Question, a: dict) -> None:
    plans = {p.sheet: p for p in b.plans}
    for inst in q.key.get("instances", []):
        plan = plans.get(inst["sheet"])
        if plan is None:
            continue
        rooms = [r for r in plan.rooms if r.id in inst["rooms"]]
        if not rooms or a.get("skip"):
            continue
        if a["one_room"]:
            name = a.get("name") or " / ".join(r.name for r in rooms)
            first = rooms[0]
            merged = Room(id=first.id, name=name, point=first.point, labelled_size_mm=None,
                          boundary=first.boundary, area_m2=first.area_m2, unit=first.unit,
                          evidence=Evidence(sheet=plan.sheet, handles=[h for r in rooms for h in r.evidence.handles],
                                            note=f"one room by answer {q.id} (labelled "
                                                 f"{', '.join(repr(r.name) for r in rooms)})"))
            plan.rooms = [merged if r is first else r for r in plan.rooms if r is first or r not in rooms]
        else:
            for n, (x1, y1, x2, y2) in enumerate(a["lines"].get(inst["ref"], []), 1):
                plan.room_separations.append(RoomSeparation(
                    id=f"{q.id}-{inst['ref'].split('#')[-1]}-{n}", start=(x1, y1), end=(x2, y2),
                    rooms=[r.id for r in rooms],
                    evidence=Evidence(sheet=plan.sheet, handles=[h for r in rooms for h in r.evidence.handles][:8],
                                      note=f"separation line by answer {q.id}")))


def _apply_length(b: RecognisedBuilding, q: Question, a: dict) -> None:
    v = a["value_mm"]
    what = q.key.get("sets")
    if what == "tie_beam_depth":
        for bl in b.beam_layouts:
            if bl.sheet in q.key.get("sheets", [bl.sheet]):
                for bm in bl.beams:
                    bm.depth_mm = v
    elif what == "layer":
        for s in b.sections:
            if s.sheet in q.key.get("sections", []):
                layer = q.key["layer"]
                s.foundation_layers = [(n, t) for n, t in s.foundation_layers if n != layer] + [(layer, v)]
    elif what == "slab_thickness":
        for s in b.sections:
            s.slab_thickness_mm = v


def _apply_choice(b: RecognisedBuilding, q: Question, a: Any) -> None:
    overall = q.key.get("overall_by_option")
    if overall and a in overall:  # beam depth meaning: the option chosen gives the overall depth
        for s in b.sections:
            if s.sheet in q.key.get("sections", []):
                s.beam_depth_mm = overall[a]


APPLY: dict[str, Callable[[RecognisedBuilding, Question, Any], None]] = {
    "mark_width": _apply_mark_width,
    "opening_mark": _apply_opening_mark,
    "door_height": _apply_door_height,
    "window_size": _apply_window_size,
    "rooms": _apply_rooms,
    "length": _apply_length,
    "choice": _apply_choice,
}
