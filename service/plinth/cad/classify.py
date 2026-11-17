"""Sheet role and storeys from the title (PRD Step 9, stage 2), falling back to the file name (also when the title
names the role but leaves a question, such as which storeys a typical plan covers, that the file name answers).

Storeys are numbered from the ground: GROUND = 0, FIRST / 1ST = 1, ... A range ("FIRST STOREY TO FOURTH
STOREY", "1st to 4th", "1ST-4TH") gives every storey in it; "&", "AND" and "," list storeys. Words that are not
plainly a storey number (STILT, BASEMENT, MEZZANINE, a bare "2") are not mapped: the sheet gets a question.
A sheet is classified only when exactly one role matches; anything else is ``unknown`` with a question.
"""

from __future__ import annotations

import re
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict

Role = Literal["floor_plan", "roof_plan", "site_plan", "elevation", "section", "foundation", "beam_layout", "unknown"]

ORDINALS = {
    "GROUND": 0, "FIRST": 1, "SECOND": 2, "THIRD": 3, "FOURTH": 4, "FIFTH": 5, "SIXTH": 6, "SEVENTH": 7,
    "EIGHTH": 8, "NINTH": 9, "TENTH": 10, "ELEVENTH": 11, "TWELFTH": 12, "THIRTEENTH": 13, "FOURTEENTH": 14,
    "FIFTEENTH": 15, "SIXTEENTH": 16, "SEVENTEENTH": 17, "EIGHTEENTH": 18, "NINETEENTH": 19, "TWENTIETH": 20,
}
STOREY_RE = re.compile(r"\b(?:(\d+)\s*(?:ST|ND|RD|TH)|(" + "|".join(ORDINALS) + r"))\b")
STOREY_WORDS = re.compile(r"\b(?:STOREYS?|STORIES|STORY|FLOORS?|LEVELS?|FL|FLR)\b")
RANGE_SEP = re.compile(r"^\s*(?:TO|-|–|THRU|THROUGH)\s*$")
LIST_SEP = re.compile(r"^\s*(?:&|AND|,)\s*$")

# Role patterns; a plan word alone (PLAN) is a floor plan only together with a storey or FLOOR/STOREY/TYPICAL.
PATTERNS: dict[str, re.Pattern] = {
    "beam_layout": re.compile(r"\bBEAM\s+(?:LAYOUT|PLAN|DETAILS?)\b|\bFRAMING\s+PLAN\b"),
    "foundation": re.compile(r"\bFOUNDATIONS?\b|\bFOOTINGS?\b"),
    "site_plan": re.compile(r"\b(?:SITE|LOCATION|KEY)\s+PLAN\b"),
    "section": re.compile(r"\bSECTIONS?\b"),
    "elevation": re.compile(r"\bELEVATIONS?\b"),
    "roof_plan": re.compile(r"\b(?:ROOF|TERRACE)\s+(?:FLOOR\s+)?PLAN\b"),
    "floor_plan": re.compile(r"\bPLAN\b"),
}
PLAN_ROLES = {"roof_plan", "site_plan", "foundation", "beam_layout"}  # more specific than a floor plan


class Classification(BaseModel):
    model_config = ConfigDict(extra="forbid")
    role: Role
    storeys: list[int] = []
    name: str | None = None  # section or elevation name: "AA", "FRONT"
    decided_by: Literal["title", "file_name"] | None = None
    question: str | None = None


def normalise(text: str) -> str:
    text = text.upper().replace("%%U", "").replace("_", " ")
    return " ".join(text.split())


def file_title(path: str | Path) -> str:
    """The title a file name carries: "2.PLAN 1st to 4th.dwg" -> "PLAN 1st to 4th"."""
    return re.sub(r"^\s*\d+\s*[.\-_)]\s*", "", Path(path).stem)


def classify(title: str | None, file_name: str | Path | None = None) -> Classification:
    """Classify from the title; when the title says nothing, from the file name. Disagreement is a question."""
    by_title = _classify(normalise(title)) if title else None
    by_file = _classify(normalise(file_title(file_name))) if file_name else None
    if (by_title and by_title.question and by_file and not by_file.question and by_file.role != "unknown"
            and by_file.role == by_title.role):  # e.g. "TYPICAL FLOOR PLAN" in "PLAN 1st to 4th.dwg"
        return by_file.model_copy(update={"decided_by": "file_name"})
    if by_title and by_title.role != "unknown":
        result = by_title.model_copy(update={"decided_by": "title"})
        if by_file and by_file.role != "unknown" and (by_file.role, by_file.storeys, by_file.name) != (
                result.role, result.storeys, result.name):
            result.question = (f"The title \"{title}\" reads as {_describe(result)} but the file name "
                               f"\"{Path(file_name).name}\" reads as {_describe(by_file)}. Which is right?")
        return result
    if by_file and by_file.role != "unknown":
        return by_file.model_copy(update={"decided_by": "file_name"})
    shown = f"title \"{title}\"" if title else "no title"
    question = (by_title or by_file).question if (by_title or by_file) else None
    return Classification(role="unknown", question=question or (
        f"What is this sheet ({shown}, file \"{Path(file_name).name if file_name else '-'}\")? It could not be "
        "classified as a floor, roof or site plan, elevation, section, foundation or beam layout."))


def _describe(c: Classification) -> str:
    parts = [c.role.replace("_", " ")]
    if c.storeys:
        parts.append("storeys " + ", ".join(map(str, c.storeys)))
    if c.name:
        parts.append(c.name)
    return " ".join(parts)


def _classify(text: str) -> Classification:
    storeys, storey_problem = _storeys(text)
    matched = [role for role, pattern in PATTERNS.items() if pattern.search(text)]
    if "floor_plan" in matched and (set(matched) & PLAN_ROLES):
        matched.remove("floor_plan")
    if "floor_plan" in matched and not (storeys or storey_problem or STOREY_WORDS.search(text) or "TYPICAL" in text):
        return Classification(role="unknown", question=f"\"{text}\" is a plan but does not say which storey.")
    if len(matched) != 1:
        if len(matched) > 1:
            return Classification(role="unknown", question=f"\"{text}\" matches several sheet kinds: "
                                                           + ", ".join(matched) + ". Which is it?")
        return Classification(role="unknown")
    role = matched[0]
    if role == "section":
        name = _section_name(text)
        if not name:
            return Classification(role="section", question=f"\"{text}\" is a section but has no name (e.g. AA).")
        return Classification(role="section", name=name)
    if role == "elevation":
        name = _elevation_name(text)
        if not name:
            return Classification(role="elevation", question=f"\"{text}\" is an elevation but does not say "
                                                             "which side (front, rear, side, ...).")
        return Classification(role="elevation", name=name)
    if role in ("floor_plan", "beam_layout"):
        if storey_problem:
            return Classification(role=role, question=storey_problem)
        if not storeys:
            if role == "beam_layout":
                return Classification(role=role, question=f"\"{text}\": at which storey is this beam layout?")
            if "TYPICAL" in text:
                return Classification(role=role, question=f"\"{text}\" is a typical floor plan; which storeys "
                                                          "does it apply to?")
            return Classification(role=role, question=f"\"{text}\": which storey is this plan?")
        return Classification(role=role, storeys=storeys)
    return Classification(role=role)


def _storeys(text: str) -> tuple[list[int], str | None]:
    """Storey numbers in the text, ranges expanded; or a reason they cannot be read."""
    hits = list(STOREY_RE.finditer(text))
    if not hits:
        return [], None
    numbers = [int(h.group(1)) if h.group(1) else ORDINALS[h.group(2)] for h in hits]
    storeys = [numbers[0]]
    for i in range(1, len(hits)):
        between = STOREY_WORDS.sub(" ", text[hits[i - 1].end():hits[i].start()])
        if RANGE_SEP.match(between):
            if numbers[i] <= numbers[i - 1]:
                return [], f"\"{text}\": the storey range runs backwards."
            storeys.extend(range(numbers[i - 1] + 1, numbers[i] + 1))
        elif LIST_SEP.match(between):
            storeys.append(numbers[i])
        else:
            return [], f"\"{text}\": the storeys named could not be read as one list or range."
    return sorted(set(storeys)), None


def _section_name(text: str) -> str | None:
    match = re.search(r"\bSECTIONS?\b\s*(?:[-:]\s*)?(?:AT\s+|ON\s+|THROUGH\s+)?([A-Z0-9]+(?:\s*-\s*[A-Z0-9]+)?)\b", text)
    if not match:
        return None
    name = re.sub(r"[\s\-]", "", match.group(1))
    return name if name not in {"PLAN", "DETAILS", "DETAIL", "AND"} else None


def _elevation_name(text: str) -> str | None:
    """The words before ELEVATION, as written: "FRONT ELEVATION" -> "FRONT"."""
    match = re.search(r"^(.*?)\bELEVATIONS?\b", text)
    name = re.sub(r"[^A-Z0-9 ]", " ", match.group(1)).split() if match else []
    return " ".join(name) or None
