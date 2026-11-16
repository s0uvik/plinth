"""Drawing-import project (PRD Step 9): the sources, what each sheet is, the user's inputs, the open and answered
questions, the assumptions and the approvals, in %LOCALAPPDATA%\\B2M\\imports\\<id>\\project.json.

No guessing: whatever the drawings do not settle is a question. An answer is the user's, or an assumption; and an
assumption can only be made once the project's location is known, because it comes from that location's rule set
and typical practice ("ask the user for the project location first").
"""

from __future__ import annotations

import json
import re
import secrets
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable, Literal, get_args

from pydantic import BaseModel, ConfigDict, Field

from b2m import paths
from b2m.cad import classify, reader, sheet as sheets

DRAWING_SUFFIXES = {".dwg", ".dxf"}
_ROLES = [r for r in get_args(classify.Role) if r != "unknown"]


class LocationRequired(ValueError):
    pass


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Location(_Model):
    city: str | None = None
    municipality: str | None = None
    state: str | None = None


class Inputs(_Model):
    location: Location | None = None
    rule_set: str | None = None
    values: dict[str, Any] = {}  # anything else the user told us, e.g. {"floor_to_floor_mm": 3000}


class Source(_Model):
    path: str
    sha256: str
    dxf: str  # the DXF read: the source itself, or its converted copy in the cache


class SheetRecord(_Model):
    file: str  # the source file name; questions refer to sheets by it
    source: str
    role: classify.Role
    storeys: list[int] = []
    name: str | None = None
    decided_by: Literal["title", "file_name"] | None = None
    title: str | None = None
    title_handle: str | None = None
    title_rotation: float | None = None
    scale: float | None = None
    page: str | None = None
    page_number: int | None = None
    units: str
    entities: dict[str, int]  # drawing entities (inside the frame) by kind, top level
    layers: dict[str, int]  # the same by layer
    exploded: int  # drawing entities including everything inside block references
    excluded: dict[str, int]  # left out of the drawing, by reason (border, title, scale, page, margin, ...)


class Evidence(_Model):
    sheet: str
    handles: list[str] = []
    note: str | None = None


class Question(_Model):
    id: str
    topic: str
    text: str
    evidence: list[Evidence] = []
    options: list[str] = []
    # Added by the import pipeline (T5); defaults keep older project files valid.
    severity: Literal["blocking", "check", "info"] = "check"  # blocking: must be answered before building
    where: str | None = None  # a short description of where on the drawings
    instances: list[str] = []  # one line per place the question covers (grouped findings)
    findings: list[str] = []  # "<sheet>#<finding id>" of every recognition finding it covers
    answer_type: str = "choice"  # what an answer looks like (b2m.cad.answers.ANSWER_TYPES)
    key: dict[str, Any] = {}  # what the answer applies to, e.g. {"mark": "D1"}
    answer: Any = None
    answered_by: Literal["user", "assumed"] | None = None
    at: str | None = None

    @property
    def open(self) -> bool:
        return self.answered_by is None


class Assumption(_Model):
    id: str
    text: str
    basis: str  # e.g. "Kolkata Municipal Corporation building rules, typical practice"
    question: str | None = None
    at: str


class Approval(_Model):
    what: str
    by: str
    note: str | None = None
    at: str


class ImportProject(_Model):
    id: str
    created: str
    sources: list[Source] = []
    sheets: list[SheetRecord] = []
    inputs: Inputs = Field(default_factory=Inputs)
    questions: list[Question] = []
    assumptions: list[Assumption] = []
    approvals: list[Approval] = []

    def open_questions(self) -> list[Question]:
        return [q for q in self.questions if q.open]

    def question(self, question_id: str) -> Question:
        for q in self.questions:
            if q.id == question_id:
                return q
        raise KeyError(f"no question {question_id}")

    def add_question(self, topic: str, text: str, evidence: Iterable[Evidence] = (),
                     options: Iterable[str] = ()) -> Question:
        q = Question(id=f"Q{len(self.questions) + 1}", topic=topic, text=text, evidence=list(evidence),
                     options=list(options))
        self.questions.append(q)
        return q

    def set_location(self, city: str | None = None, municipality: str | None = None, state: str | None = None,
                     rule_set: str | None = None) -> None:
        if not (city or municipality):
            raise ValueError("a location needs at least a city or a municipality")
        self.inputs.location = Location(city=city, municipality=municipality, state=state)
        if rule_set:
            self.inputs.rule_set = rule_set

    def set_value(self, key: str, value: Any) -> None:
        self.inputs.values[key] = value

    def assume(self, text: str, basis: str, question: str | None = None) -> Assumption:
        if self.inputs.location is None:
            raise LocationRequired("Cannot assume anything yet: ask the user for the project location first "
                                   "(city or municipality); assumptions come from that location's rule set.")
        if not basis.strip():
            raise ValueError("an assumption needs its basis (the location's rule set or typical practice)")
        a = Assumption(id=f"A{len(self.assumptions) + 1}", text=text, basis=basis, question=question, at=_now())
        self.assumptions.append(a)
        return a

    def answer(self, question_id: str, answer: Any, by: Literal["user", "assumed"] = "user",
               basis: str | None = None, assumption: str | None = None) -> Question:
        """Record the user's answer, or (by="assumed", with its basis) an assumption made for it; ``assumption``
        is the assumption's wording (default: the question and the answer)."""
        q = self.question(question_id)
        if by == "assumed":
            self.assume(assumption or f"{q.text} -> {answer}", basis or "", question=q.id)
        elif by != "user":
            raise ValueError("answered_by is 'user' or 'assumed'")
        q.answer, q.answered_by, q.at = answer, by, _now()
        return q

    def approve(self, what: str, by: str = "user", note: str | None = None) -> Approval:
        approval = Approval(what=what, by=by, note=note, at=_now())
        self.approvals.append(approval)
        return approval


def collect(inputs: Path | str | Iterable[Path | str]) -> list[Path]:
    """Drawing files from a folder (not recursive) and/or file paths, in natural order (2 before 10)."""
    items = [inputs] if isinstance(inputs, (str, Path)) else list(inputs)
    files: list[Path] = []
    for item in map(Path, items):
        if item.is_dir():
            files.extend(p for p in item.iterdir() if p.is_file() and p.suffix.lower() in DRAWING_SUFFIXES)
        elif item.is_file():
            files.append(item)
        else:
            raise FileNotFoundError(f"no such drawing or folder: {item}")
    return sorted(files, key=lambda p: [int(t) if t.isdigit() else t.lower() for t in re.split(r"(\d+)", p.name)])


def analyse(inputs: Path | str | Iterable[Path | str], oda: Path | str | None = None,
            project_id: str = "") -> ImportProject:
    """Read, frame and classify every drawing; whatever is unclear becomes a question. Nothing is saved."""
    project = ImportProject(id=project_id, created=_now())
    for path in collect(inputs):
        cad = reader.read(path, oda)
        laid_out = sheets.detect(cad)
        result = classify.classify(laid_out.title, path.name)
        project.sources.append(Source(path=str(path), sha256=cad.sha256, dxf=cad.dxf))
        drawing = laid_out.drawing_entities
        excluded: dict[str, int] = {}
        for item in laid_out.excluded:
            excluded[item.reason] = excluded.get(item.reason, 0) + 1
        project.sheets.append(SheetRecord(
            file=path.name, source=str(path), role=result.role, storeys=result.storeys, name=result.name,
            decided_by=result.decided_by, title=laid_out.title, title_handle=laid_out.title_handle,
            title_rotation=laid_out.title_rotation, scale=laid_out.scale, page=laid_out.page,
            page_number=laid_out.page_number, units=cad.units, entities=reader.kind_counts(drawing),
            layers=reader.layer_counts(drawing), exploded=sum(reader.kind_counts(drawing, deep=True).values()),
            excluded=dict(sorted(excluded.items()))))
        for issue in cad.issues + laid_out.issues:
            project.add_question(issue.topic, issue.text, [Evidence(sheet=path.name, handles=issue.handles)])
        if result.question:
            handles = [laid_out.title_handle] if laid_out.title_handle else []
            project.add_question("sheet_role", result.question, [Evidence(sheet=path.name, handles=handles)],
                                 options=_ROLES if result.role == "unknown" else [])
    return project


class ImportStore:
    def __init__(self, root: Path | None = None):
        self.root = root or paths.imports()

    def create(self, inputs: Path | str | Iterable[Path | str], oda: Path | str | None = None) -> ImportProject:
        stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")
        project = analyse(inputs, oda, project_id=f"{stamp}-{secrets.token_hex(2)}")
        self.save(project)
        return project

    def save(self, project: ImportProject) -> Path:
        directory = self.root / project.id
        directory.mkdir(parents=True, exist_ok=True)
        temp = directory / ".project.json.tmp"
        temp.write_text(json.dumps(project.model_dump(mode="json"), indent=2, ensure_ascii=False), encoding="utf-8")
        temp.replace(directory / "project.json")
        return directory / "project.json"

    def get(self, project_id: str) -> ImportProject | None:
        if not project_id or "/" in project_id or "\\" in project_id or project_id.startswith("."):
            return None
        path = self.root / project_id / "project.json"
        return ImportProject.model_validate_json(path.read_text(encoding="utf-8")) if path.is_file() else None
