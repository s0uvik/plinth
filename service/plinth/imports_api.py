"""Seams between the import review API (plinth.api, /imports) and the Step 9 engine (T5 pipeline, questions, answers,
assumptions and readiness; T6 build plan). Each seam is one call into the engine, so wiring or renaming the real
function is a one-line change here; tests replace these functions with fakes.

A seam whose engine module is not merged yet raises ``NotWired`` (the API answers 501 with that message) rather
than doing anything in its place: nothing is guessed.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

from plinth.cad.building import RecognisedBuilding
from plinth.cad.project import ImportProject, Question
from plinth.plan import BuildPlan

log = logging.getLogger(__name__)

BUILDING_FILE = "recognised_building.json"  # written by the API beside project.json after run_import


class NotWired(RuntimeError):
    """The engine function behind a seam is not available in this build."""


def _engine(module: str):
    import importlib
    try:
        return importlib.import_module(f"plinth.cad.{module}")
    except ModuleNotFoundError as e:
        if e.name == f"plinth.cad.{module}":
            raise NotWired(f"The drawing import step '{module}' is not installed in this Plinth build yet.") from e
        raise


def run_import(project: ImportProject) -> RecognisedBuilding:
    """T5: recognise and register every sheet; may add questions to the project."""
    return _engine("pipeline").run_import(project)


def question_groups(project: ImportProject) -> list[Any]:
    """T5: the project's questions grouped by severity ("blocking", "check", "info"), each with its evidence.
    Expected: a list of {"severity": str, "questions": [Question or dict]} or a {severity: [questions]} mapping."""
    return _engine("questions").grouped(project)


def apply_answer(project: ImportProject, question_id: str, answer: Any) -> Question:
    """T5: apply the user's typed answer (KeyError: no such question; ValueError: answer does not fit)."""
    return _engine("answers").apply_answer(project, question_id, answer)


def assume(project: ImportProject, question_ids: list[str] | None) -> list[Any]:
    """T5: assume answers (None = every open question) from the location's rule set and typical practice.
    Raises plinth.cad.project.LocationRequired when no location is set. Returns the assumptions made."""
    return _engine("assumptions").assume(project, question_ids)


def refresh(project: ImportProject, building: RecognisedBuilding | None) -> None:
    """T5: re-derive the questions after an answer or an assumption (answers kept): what the answer settles drops
    out, and what the build plan now needs (a mark given by answer needs its door heights) is asked."""
    if building is not None:
        _engine("questions").update(project, building)


def ready_to_build(project: ImportProject, building: RecognisedBuilding | None) -> Any:
    """T5: the readiness report. Expected: {"ready": bool, "blocking": [items]} (or an object with those fields)."""
    return _engine("readiness").ready_to_build(project)


def to_plan(project: ImportProject, building: RecognisedBuilding) -> Any:
    """T6: the full ImportPlan (build plan, op sources, standards, counts, not_modelled), or the list of inputs still
    missing. A plain BuildPlan is accepted from a stand-in engine. The building is the recognised one as saved; T5's
    plan bridge gives T6 the building with the answers applied and the answers in T6's own form
    (plinth.cad.plan_bridge.to_plan -> plinth.cad.to_plan.to_plan(building, answers, rules))."""
    result = _engine("plan_bridge").to_plan(project, building)
    if result.plan is None:
        return [m.model_dump(mode="json") for m in result.missing]
    return result


def plan_context(project: ImportProject, building: RecognisedBuilding) -> tuple[RecognisedBuilding, dict[str, Any]]:
    """What T6 was given for the plan: the recognised building with the answers applied (the drawings as answered,
    what the self-check compares the model with) and T6's answer map (what explains the plan's departures)."""
    bridge = _engine("plan_bridge")
    applied = bridge.building_for_plan(project, building)
    return applied, bridge.plan_answers(project, applied)


def plan_of(result: Any) -> BuildPlan:
    """The BuildPlan inside a to_plan result (an ImportPlan, or a plain BuildPlan from a stand-in)."""
    return result if isinstance(result, BuildPlan) else result.plan


def save_building(directory: Path, building: RecognisedBuilding) -> None:
    temp = directory / f".{BUILDING_FILE}.tmp"
    temp.write_text(json.dumps(building.model_dump(mode="json"), ensure_ascii=False), encoding="utf-8")
    temp.replace(directory / BUILDING_FILE)
    save_standards(directory, building)


def save_standards(directory: Path, building: RecognisedBuilding, project: ImportProject | None = None) -> None:
    """T6's project standards (project-standards.json) beside project.json, from the building as answered so far.
    Skipped while the plan converter is not installed."""
    if project is None:
        path = directory / "project.json"
        if not path.is_file():
            return
        project = ImportProject.model_validate_json(path.read_text(encoding="utf-8"))
    try:
        result = _engine("plan_bridge").to_plan(project, building)
    except NotWired:
        return
    except Exception:  # noqa: BLE001 - the standards are derived; never lose the import over them
        log.exception("project standards for %s not written", directory.name)
        return
    result.standards.save(directory)


def load_building(directory: Path) -> RecognisedBuilding | None:
    path = directory / BUILDING_FILE
    return RecognisedBuilding.model_validate_json(path.read_text(encoding="utf-8")) if path.is_file() else None
