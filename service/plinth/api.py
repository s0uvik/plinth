"""Local HTTP API and web page. Binds 127.0.0.1 only (PRD: Security).

    python -m plinth.api            # serves http://127.0.0.1:8765
"""

from __future__ import annotations

import html
import json
import logging
import math
import secrets
import threading
from datetime import datetime, timezone
from importlib.resources import files
from pathlib import Path
from typing import Any, Callable, Literal

from fastapi import FastAPI, HTTPException
from fastapi.encoders import jsonable_encoder
from fastapi.responses import FileResponse, HTMLResponse, Response
from pydantic import BaseModel

from plinth import imports_api, logs
from plinth.cad import overlay, reader as cad_reader, sheet as cad_sheet
from plinth.cad.building import PlanRecognition, RecognisedBuilding
from plinth.cad.project import ImportProject, ImportStore, LocationRequired, Question, analyse, collect
from plinth.cad.assumptions import ProfileRequired
from plinth.imports_api import NotWired
from plinth.imported import stage_questions
from plinth.brief.agent import BriefFailed, BriefSession, MessagesClient, Questions, SpecReady, anthropic_client
from plinth.builds import BuildService, Busy, product_version
from plinth.compliance import evaluate, facts_after_build, facts_before_build, hard_failures
from plinth.compliance.pdf import write_pdf
from plinth.drawings import build_drawing_set
from plinth.gateway import Gateway, GatewayError
from plinth.engine import LayoutError, generate
from plinth.plan import BuildPlan, RoomOp
from plinth.revit import RevitClient
from plinth.revit.errors import hint
from plinth.runs import RunStore
from plinth.spec import BuildingSpec, parse_spec

HOST, PORT = "127.0.0.1", 8765
log = logging.getLogger(__name__)


class BuildRequest(BaseModel):
    plan: BuildPlan
    document_title: str | None = None


class BriefRequest(BaseModel):
    brief: str


class AnswersRequest(BaseModel):
    answers: dict[str, str] | str


class SpecRequest(BaseModel):
    spec: dict[str, Any]
    document_title: str | None = None
    brief_id: str | None = None
    override: bool = False  # build despite hard compliance failures (F5.1)
    allow_family_create: bool = False  # the user agreed Plinth may create a frame family it could not find
    family_folders: list[str] = []  # extra folders to search for .rfa families


class TargetRequest(BaseModel):
    target: str = "auto"
    verify: bool = True


class CallRequest(BaseModel):
    params: dict[str, Any] = {}
    timeout_seconds: int | None = None


class ChangeRequest(BaseModel):
    call_ids: list[str]
    reason: str


class IdentityRequest(BaseModel):
    old_title: str
    new_title: str


class ModuleRequest(BaseModel):
    name: str
    code: str
    overwrite: bool = False


class BakeRequest(BaseModel):
    name: str
    description: str


def layout_summary(spec: BuildingSpec) -> dict[str, Any]:
    """What the user should see before building: the layout and its compliance check, or why it cannot be laid out."""
    try:
        report = generate(spec).report
    except LayoutError as e:
        return {"ok": False, "error": str(e)}
    checks = evaluate(facts_before_build(spec, report))
    keep = ("template", "variant", "height_mm", "footprint_m2", "plot_area_m2", "ground_coverage", "far",
            "setbacks_mm", "notes", "levels", "lift")
    summary = {k: report[k] for k in keep}
    summary["flats_per_floor"] = [{k: f[k] for k in ("letter", "type", "target_carpet_m2", "carpet_m2", "width_mm", "depth_mm")}
                                  for f in report["flats_per_floor"]]
    return {"ok": True, **summary, "checks": checks}


def after_build_checks(spec: BuildingSpec, report: dict[str, Any], plan: BuildPlan):
    """Re-run the room rules on rooms read back from Revit and save the report with the run (F8.1)."""
    room_keys = {op.key for op in plan.ops if isinstance(op, RoomOp)}

    def check(revit, run, created: dict[str, int]) -> dict[str, Any]:
        result = evaluate(facts_after_build(spec, report, revit, [i for k, i in created.items() if k in room_keys]))
        run.save("check_after", result)
        pdf = write_pdf(result, run.dir / "report.pdf", "Compliance report (after build)")
        return {**{k: result["summary"][k] for k in ("hard_fail", "soft_fail", "provisional")}, "report_pdf": str(pdf)}

    return check


def brief_json(session: BriefSession) -> dict[str, Any]:
    outcome = session.outcome
    body: dict[str, Any] = {"brief_id": session.id}
    if isinstance(outcome, Questions):
        body.update(kind="questions", questions=outcome.questions)
    elif isinstance(outcome, SpecReady):
        body.update(kind="spec", spec=outcome.spec.model_dump(mode="json"), defaults=outcome.defaults,
                    assumptions=outcome.assumptions, layout=layout_summary(outcome.spec))
    elif isinstance(outcome, BriefFailed):
        body.update(kind="failed", errors=outcome.errors)
    return body


def create_app(service: BuildService | None = None, brief_client: Callable[[], MessagesClient] = anthropic_client,
               import_store: ImportStore | None = None) -> FastAPI:
    service = service or BuildService(RunStore())
    app = FastAPI(title="Plinth", version=product_version())
    briefs: dict[str, BriefSession] = {}
    briefs_lock = threading.Lock()

    @app.get("/", response_class=HTMLResponse)
    def page() -> str:
        return files("plinth.web").joinpath("index.html").read_text(encoding="utf-8")

    @app.get("/health")
    def health() -> dict[str, Any]:
        try:
            with RevitClient.connect() as revit:
                return {"service": product_version(), "revit": revit.health}
        except Exception as e:
            return {"service": product_version(), "revit": None, "revit_error": str(e)}

    @app.post("/briefs")
    def start_brief(request: BriefRequest) -> dict[str, Any]:
        if not request.brief.strip():
            raise HTTPException(422, "The brief is empty.")
        try:
            client = brief_client()
        except Exception as e:
            raise HTTPException(503, f"Claude is not available: {e}") from e
        session = BriefSession.start(client, request.brief)
        with briefs_lock:
            briefs[session.id] = session
        return brief_json(session)

    @app.post("/briefs/{brief_id}/answers")
    def answer_brief(brief_id: str, request: AnswersRequest) -> dict[str, Any]:
        with briefs_lock:
            session = briefs.get(brief_id)
        if session is None:
            raise HTTPException(404, "No such brief in this service session.")
        try:
            session.answer(request.answers)
        except ValueError as e:
            raise HTTPException(409, str(e)) from e
        return brief_json(session)

    @app.post("/preview")
    def preview(request: SpecRequest) -> dict[str, Any]:
        spec, errors = parse_spec(request.spec)
        if spec is None:
            raise HTTPException(422, {"errors": errors})
        return {"spec": spec.model_dump(mode="json"), "layout": layout_summary(spec)}

    @app.post("/builds/from-spec", status_code=202)
    def build_from_spec(request: SpecRequest) -> dict[str, str]:
        spec, errors = parse_spec(request.spec)
        if spec is None:
            raise HTTPException(422, {"errors": errors})
        try:
            result = generate(spec)
        except LayoutError as e:
            raise HTTPException(422, {"errors": [str(e)]}) from e
        before = evaluate(facts_before_build(spec, result.report))
        blocking = hard_failures(before)
        if blocking and not request.override:
            raise HTTPException(409, {"errors": ["Hard compliance failures; build with override to proceed anyway:"] + blocking,
                                      "checks": before})
        attachments: dict[str, Any] = {"spec": spec.model_dump(mode="json"), "layout_report": result.report,
                                       "check_before": before}
        if blocking:
            attachments["override"] = {"overridden": blocking}
        if request.brief_id:
            with briefs_lock:
                session = briefs.get(request.brief_id)
            attachments["brief"] = {"id": request.brief_id, "text": session.brief if session else None}
        try:
            run_id = service.start(result.plan, request.document_title, attachments,
                                   after_build=after_build_checks(spec, result.report, result.plan),
                                   families={"allow_create": request.allow_family_create, "folders": request.family_folders})
        except Busy as e:
            raise HTTPException(409, str(e)) from e
        write_pdf(before, service.store.root / run_id / "report_before.pdf", "Compliance report (before build)")
        return {"run_id": run_id}

    @app.post("/runs/{run_id}/drawings")
    def drawings(run_id: str) -> dict[str, Any]:
        run = service.store.get(run_id)
        if run is None:
            raise HTTPException(404, "No such run.")
        record = run.load("run")
        if run.load("import_plan") is not None:  # built from drawings: reproduce the source sheet set
            return app.state.import_drawings((run.load("import") or {}).get("id"), run_id)
        if record.get("status") not in ("built", "built_with_issues"):
            raise HTTPException(409, "Build the model before making its drawing set.")
        if (record.get("drawings") or {}).get("sheets"):
            raise HTTPException(409, "This run already has a drawing set; remove the run and build again to redo it.")
        plan = BuildPlan.model_validate(run.load("plan"))

        def make(revit, run) -> dict[str, Any]:
            def remember(key: str, element_id: int) -> None:
                current = run.load("run").get("created") or {}
                run.update(created={**current, key: element_id})
            report = run.dir / "report.pdf"
            return build_drawing_set(revit, plan, run.dir, report if report.exists() else run.dir / "report_before.pdf",
                                     record=remember)

        try:
            return service.run_on(run_id, make, "drawings")
        except Busy as e:
            raise HTTPException(409, str(e)) from e

    @app.get("/runs/{run_id}/3d.png")
    def run_image(run_id: str) -> FileResponse:
        run = service.store.get(run_id)
        path = run.dir / "3d.png" if run else None
        if path is None or not path.exists():
            raise HTTPException(404, "No image yet.")
        return FileResponse(path, media_type="image/png")

    # ---- gateway: direct catalog calls and the tools rvt-mcp kept in its own gateway (F11.2, F11.3)
    gateway = Gateway(service._lock)
    app.state.gateway = gateway

    def gw(fn, *args, **kwargs):
        try:
            return fn(*args, **kwargs)
        except GatewayError as e:
            raise HTTPException(e.status, str(e)) from e
        except ConnectionError as e:
            raise HTTPException(503, str(e)) from e

    @app.get("/revit/targets")
    def revit_targets() -> list[dict[str, Any]]:
        return gateway.list_targets()

    @app.get("/revit/target")
    def revit_target() -> dict[str, Any]:
        return gateway.current_target()

    @app.post("/revit/target")
    def revit_switch(request: TargetRequest) -> dict[str, Any]:
        return gw(gateway.switch_target, request.target, request.verify)

    @app.get("/revit/commands")
    def revit_commands(refresh: bool = False) -> list[dict[str, Any]]:
        return gw(gateway.commands, refresh)

    @app.post("/revit/call/{command}")
    def revit_call(command: str, request: CallRequest) -> dict[str, Any]:
        return gw(gateway.call, command, request.params, request.timeout_seconds)

    @app.get("/revit/operations/{call_id}")
    def revit_operation(call_id: str) -> dict[str, Any]:
        return gw(gateway.operation_status, call_id)

    @app.get("/revit/usage")
    def revit_usage(last: int = 1000) -> dict[str, Any]:
        return gateway.usage_patterns(last)

    @app.get("/revit/failures")
    def revit_failures(last: int = 100) -> list[dict[str, Any]]:
        return gateway.failure_records(last)

    @app.get("/revit/lookup")
    def revit_lookup(text: str) -> dict[str, Any]:
        return gateway.lookup_message(text)

    @app.post("/revit/changes")
    def revit_record_change(request: ChangeRequest) -> dict[str, Any]:
        return gw(gateway.record_change, request.call_ids, request.reason)

    @app.get("/revit/changes")
    def revit_changes(document_title: str | None = None, since: str | None = None) -> list[dict[str, Any]]:
        return gateway.change_records(document_title, since)

    @app.post("/revit/history-identity")
    def revit_identity(request: IdentityRequest) -> dict[str, Any]:
        return gateway.resolve_history_identity(request.old_title, request.new_title)

    @app.get("/revit/code-modules")
    def code_modules() -> list[dict[str, Any]]:
        return gateway.list_code_modules()

    @app.post("/revit/code-modules")
    def save_module(request: ModuleRequest) -> dict[str, Any]:
        return gw(gateway.save_code_module, request.name, request.code, request.overwrite)

    @app.delete("/revit/code-modules/{name}")
    def delete_module(name: str) -> dict[str, Any]:
        return gw(gateway.delete_code_module, name)

    @app.get("/revit/bake-suggestions")
    def bake_suggestions() -> list[dict[str, Any]]:
        return gateway.bake_suggestions()

    @app.post("/revit/bake-suggestions/{suggestion_id}/accept")
    def accept_bake(suggestion_id: str, request: BakeRequest) -> dict[str, Any]:
        return gw(gateway.accept_bake_suggestion, suggestion_id, request.name, request.description)

    @app.post("/revit/bake-suggestions/{suggestion_id}/dismiss")
    def dismiss_bake(suggestion_id: str) -> dict[str, Any]:
        return gateway.dismiss_bake_suggestion(suggestion_id)

    @app.post("/check")
    def check(request: SpecRequest) -> dict[str, Any]:
        spec, errors = parse_spec(request.spec)
        if spec is None:
            raise HTTPException(422, {"errors": errors})
        try:
            report = generate(spec).report
        except LayoutError as e:
            raise HTTPException(422, {"errors": [str(e)]}) from e
        return evaluate(facts_before_build(spec, report))

    @app.get("/runs/{run_id}/{name}.pdf")
    def run_pdf(run_id: str, name: str) -> FileResponse:
        run = service.store.get(run_id)
        if run is None or name not in ("report", "report_before", "set", "drawings"):
            raise HTTPException(404, "No such file.")
        path = run.dir / f"{name}.pdf"
        if not path.exists():
            raise HTTPException(404, "Not produced yet.")
        return FileResponse(path, media_type="application/pdf", filename=f"{run_id}-{name}.pdf")

    @app.post("/builds", status_code=202)
    def start_build(request: BuildRequest) -> dict[str, str]:
        try:
            return {"run_id": service.start(request.plan, request.document_title)}
        except Busy as e:
            raise HTTPException(409, str(e)) from e

    @app.get("/runs")
    def list_runs() -> list[dict[str, Any]]:
        return service.store.list()

    @app.get("/runs/{run_id}")
    def get_run(run_id: str) -> dict[str, Any]:
        run = service.store.get(run_id)
        if run is None:
            raise HTTPException(404, "No such run.")
        return run.load("run")

    @app.post("/runs/{run_id}/remove")
    def remove_run(run_id: str) -> dict[str, Any]:
        try:
            return service.remove(run_id)
        except KeyError as e:
            raise HTTPException(404, "No such run.") from e
        except Busy as e:
            raise HTTPException(409, str(e)) from e

    add_import_routes(app, service, Imports(import_store or ImportStore()))
    return app


# ---- drawing import (PRD Step 9 T8): import, review (overlay + questions), location, assumptions, approval, build

SEVERITY_ORDER = ("blocking", "check", "info")


class ImportRequest(BaseModel):
    folder: str | None = None
    files: list[str] = []


class ImportAnswerRequest(BaseModel):
    question_id: str
    answer: Any


class LocationRequest(BaseModel):
    city: str
    municipality: str | None = None
    state: str
    country: str


class AssumeRequest(BaseModel):
    question_ids: list[str] | Literal["all"]


class ApproveRequest(BaseModel):
    note: str | None = None


class ImportBuildRequest(BaseModel):
    document_title: str


class ImportDrawingsRequest(BaseModel):
    run_id: str
    remake: bool = False  # delete the run's previous drawing set (its own sheets and views only) and make it again


class ImportEstimateRequest(BaseModel):
    run_id: str | None = None  # the build to measure; none: the recognised building and the answers
    rules: str | None = None  # a rules file path or a packaged name; default the project's, else office-default
    reference_xlsx: str | None = None  # the office's own estimate workbook to compare with


class FitoutPlanRequest(BaseModel):
    run_id: str  # the building's run (built from this import)
    document_title: str
    rules: str | None = None  # a fit-out rule set id or location profile; default the project location's
    systems: list[str] | None = None  # of furniture, plumbing, electrical, hvac, fire; default all
    furniture_run: str | None = None  # an earlier fit-out run whose furniture is built: joined, never placed again
    flat: str | None = None  # a pilot of one flat (with its level)
    level: str | None = None
    exclude_runs: list[str] = []  # earlier fit-out runs (a pilot) whose elements are joined, not built again
    answers: dict[str, Any] = {}  # over the answers given to the fit-out questions


class FitoutBuildRequest(BaseModel):
    run_id: str
    document_title: str
    partial: bool = False  # build what is planned although questions are open (their items wait)


RUN_FILES = {  # files a run (or an import, for its estimate) serves, by name
    "selfcheck.html": "text/html", "selfcheck.json": "application/json",
    "drawings-comparison.html": "text/html", "drawings-comparison.json": "application/json",
    "estimate.xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    "estimate.json": "application/json", "estimate-crosscheck.html": "text/html",
    "estimate-comparison.html": "text/html",
}


def _utc() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Imports:
    """Import projects in the import store, each with an import.json status record beside its project.json, and the
    background import (read, frame, classify, then recognise and register), one at a time like builds."""

    def __init__(self, store: ImportStore, oda: Path | str | None = None):
        self.store = store
        self.oda = oda
        self.lock = threading.Lock()  # held while an import runs
        self.edit = threading.Lock()  # one read-modify-write of a project at a time
        self._sheets: dict[tuple[str, int], cad_sheet.Sheet] = {}

    def dir(self, import_id: str) -> Path:
        return self.store.root / import_id

    def record(self, import_id: str) -> dict[str, Any] | None:
        if not import_id or "/" in import_id or "\\" in import_id or import_id.startswith("."):
            return None
        path = self.dir(import_id) / "import.json"
        return json.loads(path.read_text(encoding="utf-8")) if path.is_file() else None

    def update(self, import_id: str, **fields: Any) -> dict[str, Any]:
        record = self.record(import_id) or {"import_id": import_id}
        record.update(fields)
        temp = self.dir(import_id) / ".import.json.tmp"
        temp.write_text(json.dumps(record, indent=2, ensure_ascii=False), encoding="utf-8")
        temp.replace(self.dir(import_id) / "import.json")
        return record

    def list(self, limit: int = 50) -> list[dict[str, Any]]:
        if not self.store.root.is_dir():
            return []
        found = (self.record(d.name) for d in sorted(self.store.root.iterdir(), reverse=True) if d.is_dir())
        return [r for r in found if r][:limit]

    def start(self, inputs: list[str]) -> str:
        drawings = collect(inputs)  # FileNotFoundError for a missing path
        if not drawings:
            raise ValueError("No DWG or DXF drawings found there.")
        if not self.lock.acquire(blocking=False):
            raise Busy("An import is already running; wait for it to finish.")
        try:
            stamp = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")
            import_id = f"{stamp}-{secrets.token_hex(2)}"
            self.dir(import_id).mkdir(parents=True)
            self.update(import_id, status="queued", inputs=[str(p) for p in drawings], created_utc=_utc(), runs=[])
        except Exception:
            self.lock.release()
            raise
        threading.Thread(target=self._import, args=(import_id, drawings), name=f"import-{import_id}", daemon=True).start()
        return import_id

    def _import(self, import_id: str, drawings: list[Path]) -> None:
        try:
            self.update(import_id, status="reading")
            project = analyse(drawings, self.oda, project_id=import_id)
            self.store.save(project)
            self.update(import_id, status="recognising")
            building = imports_api.run_import(project)
            imports_api.save_building(self.dir(import_id), building)
            self.store.save(project)  # recognition may have added questions
            self.update(import_id, status="ready")
        except Exception as e:  # Record it, never lose the import.
            log.exception("import %s failed", import_id)
            self.update(import_id, status="failed", error=f"{type(e).__name__}: {e}",
                        hint=getattr(e, "hint", None) or hint(str(e)))
        finally:
            self.update(import_id, finished_utc=_utc())
            self.lock.release()

    def project(self, import_id: str) -> ImportProject:
        if self.record(import_id) is None:
            raise HTTPException(404, "No such import.")
        project = self.store.get(import_id)
        if project is None:
            raise HTTPException(409, "The drawings are still being read; try again in a moment.")
        return project

    def building(self, import_id: str) -> RecognisedBuilding | None:
        return imports_api.load_building(self.dir(import_id))

    def sheet(self, project: ImportProject, n: int) -> cad_sheet.Sheet:
        key = (project.id, n)
        if key not in self._sheets:
            source = project.sources[n - 1]
            if len(self._sheets) >= 6:
                self._sheets.pop(next(iter(self._sheets)))
            self._sheets[key] = cad_sheet.detect(cad_reader.read_dxf(source.dxf, source.path))
        return self._sheets[key]


def _approved(project: ImportProject) -> bool:
    for approval in reversed(project.approvals):
        if approval.what in ("review", "review_withdrawn"):
            return approval.what == "review"
    return False


def _refresh(project: ImportProject, building: RecognisedBuilding | None) -> None:
    """Re-derive the questions after an answer (answers kept), so what the answer raises is asked now, not at build."""
    try:
        imports_api.refresh(project, building)
    except NotWired:
        pass


def _withdraw_approval(project: ImportProject, why: str) -> None:
    """Anything the user changes after approving needs a new approval before a build."""
    if _approved(project):
        project.approve("review_withdrawn", by="plinth", note=why)


def _question_groups(project: ImportProject) -> list[dict[str, Any]]:
    """Questions grouped by severity, blocking first. Until the question engine is wired, every question counts as
    blocking: an unknown severity is never understated."""
    try:
        raw = jsonable_encoder(imports_api.question_groups(project))
    except NotWired:
        raw = [{"severity": "blocking", "questions": [q.model_dump(mode="json") for q in project.questions]}]
    groups = [{"severity": k, "questions": v} for k, v in raw.items()] if isinstance(raw, dict) else raw
    known = {q.id: q for q in project.questions}
    for group in groups:
        for q in group["questions"]:
            stored = known.get(q.get("id"))
            if stored is not None:  # the project file is the record of who answered what
                q.update(answer=stored.answer, answered_by=stored.answered_by, at=stored.at)
            q.setdefault("severity", group["severity"])
    rank = {s: i for i, s in enumerate(SEVERITY_ORDER)}
    return sorted(groups, key=lambda g: rank.get(g["severity"], len(rank)))


def _readiness(project: ImportProject, building: RecognisedBuilding | None) -> dict[str, Any]:
    """The engine's readiness report, made safe: never ready without a recognised building."""
    try:
        report = jsonable_encoder(imports_api.ready_to_build(project, building))
    except NotWired as e:
        open_questions = [f"{q.id}: {q.text}" for q in project.open_questions()]
        return {"ready": False, "blocking": open_questions, "error": str(e)}
    if not isinstance(report, dict):
        report = {"ready": bool(report)}
    report.setdefault("blocking", [])
    if building is None:
        report["ready"] = False
        report["blocking"] = ["The drawings have not been recognised yet."] + list(report["blocking"])
    report["ready"] = bool(report.get("ready")) and not report["blocking"]
    return report


def _item_text(item: Any) -> str:
    if isinstance(item, dict):
        return str(item.get("text") or item.get("message") or item.get("name") or json.dumps(item))
    return str(item)


def _location(project: ImportProject) -> dict[str, Any] | None:
    if project.inputs.location is None:
        return None
    return {**project.inputs.location.model_dump(), "country": project.inputs.values.get("country")}


def _review(project: ImportProject, building: RecognisedBuilding | None) -> dict[str, Any]:
    return {"import_id": project.id, "location": _location(project), "groups": _question_groups(project),
            "assumptions": [a.model_dump(mode="json") for a in project.assumptions],
            "readiness": _readiness(project, building), "approved": _approved(project),
            "approvals": [a.model_dump(mode="json") for a in project.approvals]}


def _sheet_summary(building: RecognisedBuilding | None, file: str) -> dict[str, int]:
    """What recognition found on one sheet, as counts."""
    if building is None:
        return {}
    for p in building.plans:
        if p.sheet == file:
            kinds = [o.kind for o in p.openings]
            return {"walls": len(p.walls), "columns": len(p.columns), "doors": kinds.count("door"),
                    "windows": kinds.count("window"), "other openings": len(kinds) - kinds.count("door") - kinds.count("window"),
                    "rooms": len(p.rooms), "stairs": len(p.stairs), "shafts": len(p.shafts),
                    "projections": len(p.projections), "grids": len(p.grids), "findings": len(p.findings),
                    "unclaimed": len(p.unclaimed)}
    for s in building.sections:
        if s.sheet == file:
            return {"levels": len(s.levels), "storey heights": len(s.storey_heights_mm), "findings": len(s.findings),
                    "unexplained": len(s.unexplained)}
    for f in building.foundations:
        if f.sheet == file:
            return {"footings": len(f.footings), "column sections": len(f.column_sections), "rebar notes": len(f.rebar),
                    "findings": len(f.findings), "unexplained": len(f.unexplained)}
    for b in building.beam_layouts:
        if b.sheet == file:
            return {"beams": len(b.beams), "grids": len(b.grids), "columns": len(b.column_points),
                    "findings": len(b.findings), "unexplained": len(b.unexplained)}
    return {}


def _sheet_findings(building: RecognisedBuilding | None, file: str) -> list:
    if building is None:
        return []
    found = [f for f in building.findings if f.evidence.sheet == file]
    for group in (building.plans, building.sections, building.foundations, building.beam_layouts):
        found += [f for r in group if r.sheet == file for f in r.findings]
    return found


def _plan_of(building: RecognisedBuilding | None, file: str) -> PlanRecognition | None:
    return next((p for p in building.plans if p.sheet == file), None) if building else None


def _elements(building: RecognisedBuilding | None, file: str) -> dict[str, dict[str, Any]]:
    """What the review page shows when a shape on the overlay is clicked: kind, mark, size and evidence."""
    out: dict[str, dict[str, Any]] = {}

    def put(element_id: str, kind: str, evidence, **info: Any) -> None:
        details = {k: v for k, v in info.items() if v not in (None, "", [])}
        out[element_id] = {"id": element_id, "kind": kind, "handles": list(evidence.handles),
                           **({"note": evidence.note} if getattr(evidence, "note", None) else {}), **details}

    plan = _plan_of(building, file)
    if plan is not None:
        for w in plan.walls:
            length = math.hypot(w.end[0] - w.start[0], w.end[1] - w.start[1])
            put(w.id, f"{w.kind} wall", w.evidence, size=f"{w.thickness_mm:g} mm thick, {length:.0f} mm long",
                height=f"{w.height_mm:g} mm" if w.height_mm else None)
        for c in plan.columns:
            put(c.id, f"{c.shape} column", c.evidence, size=f"{c.width_mm:g} x {c.depth_mm:g} mm")
        for o in plan.openings:
            size = f"{o.width_mm:g} mm wide" + (f" x {o.height_mm:g} mm high" if o.height_mm else "")
            put(o.id, o.kind, o.evidence, mark=o.mark_normalised or o.mark, size=size, swing=o.swing, wall=o.wall_id)
        for r in plan.rooms:
            labelled = f"{r.labelled_size_mm[0]:g} X {r.labelled_size_mm[1]:g} (as labelled)" if r.labelled_size_mm else None
            put(r.id, "room", r.evidence, mark=r.name, size=labelled,
                area=f"{r.area_m2:.2f} m²" if r.area_m2 else None, unit=r.unit)
        for s in plan.stairs:
            put(s.id, f"{s.kind.replace('_', '-')} stair", s.evidence,
                size=f"{s.width_mm:g} mm wide" if s.width_mm else None)
        for s in plan.shafts:
            put(s.id, f"{s.kind} shaft", s.evidence, mark=s.label)
        for p in plan.projections:
            put(p.id, p.kind, p.evidence)
        for g in plan.grids:
            put(g.id, "grid", g.evidence, mark=g.name)
    for f in _sheet_findings(building, file):
        put(f.id, "finding", f.evidence, mark=f.topic, text=f.text, severity=f.severity)
    return out


def _evidence_layer(sheet: cad_sheet.Sheet, project: ImportProject, file: str) -> str:
    """A box round every entity a question points at on this sheet, hidden until the page shows it."""
    by_handle = {e.handle: e for top in sheet.drawing_entities for e in top.walk()}
    by_handle.update({e.handle: e for e in sheet.cad.walk()})
    parts = ['<g class="evidence">']
    for q in project.questions:
        for ev in q.evidence:
            if ev.sheet != file:
                continue
            for h in ev.handles:
                box = by_handle[h].bounds() if h in by_handle else None
                if not box:
                    continue
                pad = 150.0
                x0, y0, x1, y1 = box[0] - pad, box[1] - pad, box[2] + pad, box[3] + pad
                parts.append(f'<rect class="evidence" x="{x0:.1f}" y="{-y1:.1f}" width="{x1 - x0:.1f}" '
                             f'height="{y1 - y0:.1f}" fill="#d6007e" fill-opacity="0.12" stroke="#d6007e" '
                             f'stroke-width="3" vector-effect="non-scaling-stroke" visibility="hidden" '
                             f'data-question="{html.escape(q.id)}" data-handles="{html.escape(h)}"/>')
    parts.append("</g>")
    return "\n".join(parts) if len(parts) > 2 else ""


def fitout_answer(q: Question, answer: Any) -> Any:
    """A fit-out answer typed as text is read as JSON when it is JSON ({"family": ..., "type": ...}, {"create": "box"},
    [1.5, 1.5], 15), else kept as the words ("skip", "stair"): the fit-out reads structured answers. A bare number to a
    question with options stays as typed, since the answer check would take it for an option's index."""
    if not isinstance(answer, str):
        return answer
    try:
        value = json.loads(answer)
    except ValueError:
        return answer
    if q.options and isinstance(value, (int, float)) and not isinstance(value, bool):
        return answer
    return value


def import_selfcheck(import_plan, building: RecognisedBuilding, answers: dict[str, Any], review_url: str | None):
    """The after-build hook of an import build: the self-check (T9) reads the model back, writes selfcheck.json /
    selfcheck.html in the run folder, and its summary goes in the run record."""
    from plinth.imported import selfcheck

    hook = selfcheck.after_build_selfcheck(import_plan, building, answers, review_url=review_url)

    def check(revit, run, created: dict[str, int]) -> dict[str, Any]:
        out = hook(revit, run, created)
        summary = out["selfcheck"]
        run.update(selfcheck={"ok": summary["ok"], "unexplained": summary["unexplained"],
                              "explained": summary["explained"], "accounting": summary["accounting"],
                              "elements_read": summary["elements_read"], "report": f"/runs/{run.id}/selfcheck.html"})
        return out

    return check


def add_import_routes(app: FastAPI, service: BuildService, imports: Imports) -> None:
    app.state.imports = imports

    def engine(fn, *args):
        try:
            return fn(*args)
        except NotWired as e:
            raise HTTPException(501, str(e)) from e

    def finished(import_id: str) -> dict[str, Any]:
        record = imports.record(import_id)
        if record is None:
            raise HTTPException(404, "No such import.")
        if record.get("status") != "ready":
            raise HTTPException(409, "The import has not finished recognising the drawings."
                                     + (f" It failed: {record.get('error')}" if record.get("status") == "failed" else ""))
        return record

    def sheet_number(project: ImportProject, n: int) -> None:
        if not 1 <= n <= len(project.sheets):
            raise HTTPException(404, "No such sheet.")

    @app.get("/import", response_class=HTMLResponse)
    def import_page() -> str:
        return files("plinth.web").joinpath("import.html").read_text(encoding="utf-8")

    @app.post("/imports", status_code=202)
    def start_import(request: ImportRequest) -> dict[str, str]:
        inputs = ([request.folder] if request.folder and request.folder.strip() else []) + \
                 [f for f in request.files if f.strip()]
        if not inputs:
            raise HTTPException(422, "Give the folder with the DWG/DXF drawings, or the drawing files.")
        try:
            return {"import_id": imports.start([i.strip().strip('"') for i in inputs])}
        except FileNotFoundError as e:
            raise HTTPException(404, str(e)) from e
        except ValueError as e:
            raise HTTPException(422, str(e)) from e
        except Busy as e:
            raise HTTPException(409, str(e)) from e

    @app.get("/imports")
    def list_imports() -> list[dict[str, Any]]:
        return imports.list()

    @app.get("/imports/{import_id}")
    def import_status(import_id: str) -> dict[str, Any]:
        record = imports.record(import_id)
        if record is None:
            raise HTTPException(404, "No such import.")
        project = imports.store.get(import_id)
        if project is None:
            return {**record, "sheets": [], "registrations": [], "readiness": {"ready": False, "blocking": []}}
        building = imports.building(import_id)
        for run_id in reversed(record.get("runs") or []):  # the latest run that says something
            run = service.store.get(run_id)
            data = run.load("run") if run else None
            if data:
                if stage_questions.from_build(project, {**data, "run_id": run_id}):
                    imports.store.save(project)
                break
        sheets = [{"n": i, "file": s.file, "role": s.role, "storeys": s.storeys, "name": s.name, "title": s.title,
                   "scale": s.scale, "page": s.page, "page_number": s.page_number, "decided_by": s.decided_by,
                   "recognised": _sheet_summary(building, s.file)} for i, s in enumerate(project.sheets, 1)]
        open_questions = project.open_questions()
        return {**record, "sheets": sheets,
                "registrations": [r.model_dump(mode="json") for r in building.registrations] if building else [],
                "location": _location(project), "readiness": _readiness(project, building), "approved": _approved(project),
                "questions": {"total": len(project.questions), "open": len(open_questions)},
                "assumptions": len(project.assumptions)}

    @app.get("/imports/{import_id}/sheets/{n}/overlay.svg")
    def sheet_overlay(import_id: str, n: int) -> Response:
        project = imports.project(import_id)
        sheet_number(project, n)
        record = project.sheets[n - 1]
        sheet = imports.sheet(project, n)
        svg = overlay.render(sheet.drawing_entities, _plan_of(imports.building(import_id), record.file))
        evidence = _evidence_layer(sheet, project, record.file)
        if evidence:
            svg = svg[: svg.rindex("</svg>")] + evidence + "\n</svg>"
        return Response(svg, media_type="image/svg+xml")

    @app.get("/imports/{import_id}/sheets/{n}/elements")
    def sheet_elements(import_id: str, n: int) -> dict[str, Any]:
        project = imports.project(import_id)
        sheet_number(project, n)
        return {"sheet": project.sheets[n - 1].file,
                "elements": _elements(imports.building(import_id), project.sheets[n - 1].file)}

    @app.get("/imports/{import_id}/questions")
    def import_questions(import_id: str) -> dict[str, Any]:
        return _review(imports.project(import_id), imports.building(import_id))

    @app.post("/imports/{import_id}/answers")
    def import_answer(import_id: str, request: ImportAnswerRequest) -> dict[str, Any]:
        with imports.edit:
            project = imports.project(import_id)
            try:
                asked = project.question(request.question_id)
            except KeyError as e:
                raise HTTPException(404, f"No question {request.question_id}.") from e
            if isinstance(request.answer, str) and not request.answer.strip():
                raise HTTPException(422, "The answer is empty.")
            value = fitout_answer(asked, request.answer) if asked.key.get("stage") == "fitout" else request.answer
            try:
                try:
                    imports_api.apply_answer(project, request.question_id, value)
                except NotWired:  # the answer as typed, recorded as the user's
                    project.answer(request.question_id, value, by="user")
            except KeyError as e:
                raise HTTPException(404, str(e)) from e
            except ValueError as e:
                raise HTTPException(422, str(e)) from e
            q = project.question(request.question_id)
            if q.key.get("stage"):  # a drawing-set / estimate input: the model is unchanged
                stage_questions.record(project, q)
            else:
                _refresh(project, imports.building(import_id))
                _withdraw_approval(project, f"answer to {request.question_id} changed after approval")
            imports.store.save(project)
            return _review(project, imports.building(import_id))

    @app.post("/imports/{import_id}/location")
    def import_location(import_id: str, request: LocationRequest) -> dict[str, Any]:
        fields = {k: (v or "").strip() or None for k, v in request.model_dump().items()}
        missing = [k for k in ("city", "state", "country") if not fields[k]]
        if missing:
            raise HTTPException(422, f"The location needs its {', '.join(missing)}.")
        with imports.edit:
            project = imports.project(import_id)
            project.set_location(city=fields["city"], municipality=fields["municipality"], state=fields["state"])
            project.set_value("country", fields["country"])
            _withdraw_approval(project, "location changed after approval")
            imports.store.save(project)
            return _review(project, imports.building(import_id))

    @app.post("/imports/{import_id}/assume")
    def import_assume(import_id: str, request: AssumeRequest) -> dict[str, Any]:
        with imports.edit:
            project = imports.project(import_id)
            if project.inputs.location is None:
                raise HTTPException(409, "Set the project location first (city, state, country): assumptions come from "
                                         "that location's rules and typical practice. Nothing was assumed.")
            ids = None if request.question_ids == "all" else list(request.question_ids)
            if ids is not None:
                if not ids:
                    raise HTTPException(422, "Name the questions to assume, or \"all\".")
                for qid in ids:
                    try:
                        project.question(qid)
                    except KeyError as e:
                        raise HTTPException(404, f"No question {qid}.") from e
            try:
                made = engine(imports_api.assume, project, ids)
            except LocationRequired as e:
                raise HTTPException(409, str(e)) from e
            except ProfileRequired as e:  # no location profile covers this place: ask which values apply
                raise HTTPException(409, str(e)) from e
            except KeyError as e:
                raise HTTPException(404, str(e)) from e
            _refresh(project, imports.building(import_id))
            _withdraw_approval(project, "assumptions made after approval")
            imports.store.save(project)
            return {**_review(project, imports.building(import_id)), "assumed": jsonable_encoder(made)}

    @app.post("/imports/{import_id}/approve")
    def import_approve(import_id: str, request: ApproveRequest | None = None) -> dict[str, Any]:
        finished(import_id)
        with imports.edit:
            project = imports.project(import_id)
            building = imports.building(import_id)
            readiness = _readiness(project, building)
            if not readiness["ready"]:
                raise HTTPException(409, {"errors": ["Not ready to approve; settle these first:"]
                                                    + [_item_text(i) for i in readiness["blocking"]]
                                                    + ([readiness["error"]] if readiness.get("error") else []),
                                          "readiness": readiness})
            project.approve("review", by="user", note=request.note if request else None)
            imports.store.save(project)
            return _review(project, building)

    @app.post("/imports/{import_id}/build", status_code=202)
    def import_build(import_id: str, request: ImportBuildRequest) -> dict[str, str]:
        record = finished(import_id)
        title = request.document_title.strip()
        if not title:
            raise HTTPException(422, "Enter the title of the Revit document to build in.")
        with imports.edit:
            project = imports.project(import_id)
            building = imports.building(import_id)
            if not _approved(project):
                raise HTTPException(409, "Approve the review first: nothing is built until you have approved what Plinth "
                                         "understood from the drawings.")
            readiness = _readiness(project, building)
            if not readiness["ready"]:
                raise HTTPException(409, {"errors": ["Not ready to build:"] + [_item_text(i) for i in readiness["blocking"]],
                                          "readiness": readiness})
            result = engine(imports_api.to_plan, project, building)
            if isinstance(result, list):
                missing = jsonable_encoder(result)
                raise HTTPException(409, {"errors": ["Plinth needs these inputs before it can build:"]
                                                    + [_item_text(m) for m in missing], "missing": missing})
            plan = imports_api.plan_of(result)
            approval = next(a for a in reversed(project.approvals) if a.what == "review")
            attachments = {
                "import": {"id": project.id, "sources": [s.model_dump(mode="json") for s in project.sources],
                           "sheets": [s.model_dump(mode="json") for s in project.sheets],
                           "inputs": project.inputs.model_dump(mode="json")},
                "recognised_building": building.model_dump(mode="json"),
                "answers": [q.model_dump(mode="json") for q in project.questions if not q.open],
                "assumptions": [a.model_dump(mode="json") for a in project.assumptions],
                "approval": approval.model_dump(mode="json"),
            }
            after_build = None
            if not isinstance(result, BuildPlan):  # the full ImportPlan: keep it, and self-check every build
                applied, plan_answers = engine(imports_api.plan_context, project, building)
                attachments |= {"import_plan": result.model_dump(mode="json"), "plan_answers": jsonable_encoder(plan_answers),
                                "plan_building": applied.model_dump(mode="json")}
                after_build = import_selfcheck(result, applied, plan_answers, f"/import#{project.id}")
            try:
                run_id = service.start(plan, title, attachments, after_build=after_build)
            except Busy as e:
                raise HTTPException(409, str(e)) from e
            imports.update(import_id, runs=(record.get("runs") or []) + [run_id])
            return {"run_id": run_id}

    def import_run(import_id: str | None, run_id: str):
        run = service.store.get(run_id)
        if run is None:
            raise HTTPException(404, "No such run.")
        made_from = (run.load("import") or {}).get("id")
        if import_id is not None and made_from != import_id:
            raise HTTPException(409, f"Run {run_id} was not built from import {import_id}.")
        if run.load("import_plan") is None:
            raise HTTPException(409, "This run was built before Plinth kept the import's plan with its runs; build it "
                                     "again from the import.")
        return run, made_from

    def stage_conflict(project: ImportProject, stage: str, asked: list[dict[str, Any]], what: str) -> HTTPException:
        """Turn a stage's questions into import questions (ids = the answer keys) and say what to answer. Questions
        marked blocks=False (title block fields) become import questions too but are not what the stage waits for."""
        qs = stage_questions.merge(project, stage, asked)
        imports.store.save(project)
        waiting = {a["id"] for a in asked if a.get("blocks", True)}
        needed = [q for q in qs if q.open and q.id in waiting]
        return HTTPException(409, {"errors": [f"Plinth needs answers before it can make the {what}:"]
                                             + [f"{q.id}: {q.text}" for q in needed],
                                   "questions": [q.model_dump(mode="json") for q in needed]})

    def make_drawings(import_id: str | None, run_id: str, remake: bool = False) -> dict[str, Any]:
        """The source sheet set reproduced in Revit for a run built from an import (T10). A run makes one set; remake
        deletes the one it made before (the sheets and views recorded under its DWG- keys, nothing else) and makes
        it again, e.g. after answering the title block questions."""
        from plinth.imported.drawings import load_sheets, make_import_drawing_set
        from plinth.imported.to_plan import ImportPlan

        run, made_from = import_run(import_id, run_id)
        import_id = import_id or made_from
        record = run.load("run")
        if record.get("status") not in ("built", "built_with_issues"):
            raise HTTPException(409, "Build the model before making its drawing set.")
        previous = {k: v for k, v in (record.get("created") or {}).items() if k.startswith("DWG-")}
        if (previous or (record.get("drawings") or {}).get("sheets")) and not remake:
            raise HTTPException(409, "This run already has a drawing set. To make it again, post with \"remake\": "
                                     "true: Plinth deletes the sheets and views it made for that set (nothing else) "
                                     "and makes the set again.")
        project = imports.project(import_id)
        building = RecognisedBuilding.model_validate(run.load("recognised_building"))
        result = ImportPlan.model_validate(run.load("import_plan"))
        sheets = load_sheets(project)

        def make(revit, run) -> dict[str, Any]:
            def remember(key: str, element_id: int) -> None:
                current = run.load("run").get("created") or {}
                run.update(created={**current, key: element_id})

            def forget(keys: list[str]) -> None:
                gone = set(keys)
                current = run.load("run").get("created") or {}
                run.update(created={k: v for k, v in current.items() if k not in gone})
            created = run.load("run").get("created") or {}
            return make_import_drawing_set(revit, project, building, result, created, run.dir, record=remember,
                                           sheets=sheets, previous=previous or None, forget=forget)

        try:
            out = service.run_on(run_id, make, "drawings").get("drawings") or {}
        except Busy as e:
            raise HTTPException(409, str(e)) from e
        if out.get("status") == "questions":
            with imports.edit:
                raise stage_conflict(imports.project(import_id), "drawings", out.get("questions") or [],
                                     "drawing set")
        if out.get("error") and not out.get("sheets"):
            raise HTTPException(502, {"errors": [f"The drawing set failed: {out['error']}"], "drawings": out})
        with imports.edit:  # whatever it asked before is settled; the title block fields not answered stay asked
            project = imports.project(import_id)
            stage_questions.merge(project, "drawings", out.get("questions") or [])
            imports.store.save(project)
        comparison = out.get("comparison") or {}
        return {**{k: v for k, v in out.items() if k not in ("log", "comparison")},
                "comparison_summary": {"sheets_with_differences": comparison.get("sheets_with_differences"),
                                       "order": comparison.get("order")},
                "pdf_url": f"/runs/{run_id}/drawings.pdf" if out.get("pdf") else None,
                "comparison_url": f"/runs/{run_id}/drawings-comparison.html"}

    app.state.import_drawings = make_drawings

    @app.post("/imports/{import_id}/drawings")
    def import_drawings(import_id: str, request: ImportDrawingsRequest) -> dict[str, Any]:
        finished(import_id)
        return make_drawings(import_id, request.run_id, request.remake)

    @app.post("/imports/{import_id}/estimate")
    def import_estimate(import_id: str, request: ImportEstimateRequest | None = None) -> dict[str, Any]:
        """The door/window estimate (T11) from the built model (or the recognised building and the answers when no
        run is given), with the office's rules, its cross-check against the drawings and, given the office's own
        workbook, the cell-by-cell comparison with it."""
        from plinth.imported import estimate as import_estimate_mod

        request = request or ImportEstimateRequest()
        finished(import_id)
        run = None
        if request.run_id:
            run, _ = import_run(import_id, request.run_id)
            if (run.load("run") or {}).get("status") not in ("built", "built_with_issues"):
                raise HTTPException(409, "That run has no built model to measure.")
        if request.reference_xlsx and not Path(request.reference_xlsx.strip().strip('"')).is_file():
            raise HTTPException(404, f"No such workbook: {request.reference_xlsx}")
        with imports.edit:
            project = imports.project(import_id)
            building = imports.building(import_id)
            if building is None:
                raise HTTPException(409, "The drawings have not been recognised yet.")
            if request.rules:
                project.set_value(import_estimate_mod.RULES_KEY, request.rules.strip().strip('"'))
            try:
                if run is not None:
                    def measure(revit, run_) -> dict[str, Any]:
                        return import_estimate_mod.model_quantities_record(revit, run_)

                    measured = service.run_on(run.id, measure, "estimate_readback").get("estimate_readback") or {}
                    if measured.get("error"):
                        raise HTTPException(502, {"errors": [f"Reading the model back failed: {measured['error']}"]})
                    out_dir = run.dir
                else:
                    measured, out_dir = None, imports.dir(import_id)
                result = import_estimate_mod.make_estimate(
                    project, building, out_dir, run=run, measured=measured,
                    reference=request.reference_xlsx.strip().strip('"') if request.reference_xlsx else None)
            except Busy as e:
                raise HTTPException(409, str(e)) from e
            except FileNotFoundError as e:
                raise HTTPException(404, str(e)) from e
            qs = stage_questions.merge(project, "estimate", result.pop("questions"))
            imports.store.save(project)
        base = f"/runs/{run.id}/files/" if run is not None else f"/imports/{import_id}/files/"
        return {**result, "questions": [q.model_dump(mode="json") for q in qs if q.open],
                "answered": [q.id for q in qs if not q.open],
                "urls": {name: base + name for name in ("estimate.xlsx", "estimate-crosscheck.html",
                                                        "estimate-comparison.html")
                         if (out_dir / name).is_file()}}

    @app.get("/runs/{run_id}/files/{name}")
    def run_file(run_id: str, name: str) -> FileResponse:
        run = service.store.get(run_id)
        path = run.dir / name if run is not None and name in RUN_FILES else None
        if path is None or not path.is_file():
            raise HTTPException(404, "No such file (yet).")
        return FileResponse(path, media_type=RUN_FILES[name], filename=f"{run_id}-{name}")

    @app.get("/runs/{run_id}/selfcheck.html")
    def run_selfcheck(run_id: str) -> FileResponse:
        return run_file(run_id, "selfcheck.html")

    @app.get("/runs/{run_id}/drawings-comparison.html")
    def run_drawings_comparison(run_id: str) -> FileResponse:
        return run_file(run_id, "drawings-comparison.html")

    @app.get("/runs/{run_id}/estimate.xlsx")
    def run_estimate(run_id: str) -> FileResponse:
        return run_file(run_id, "estimate.xlsx")

    @app.get("/imports/{import_id}/files/{name}")
    def import_file(import_id: str, name: str) -> FileResponse:
        if imports.record(import_id) is None or name not in RUN_FILES:
            raise HTTPException(404, "No such file.")
        path = imports.dir(import_id) / name
        if not path.is_file():
            raise HTTPException(404, "Not produced yet.")
        return FileResponse(path, media_type=RUN_FILES[name], filename=f"{import_id}-{name}")

    add_fitout_routes(app, service, imports, finished, import_run)


# ---- fit-out (PRD Step 10): furniture and MEP planned on a built run of an import, reviewed, then built as its own run

FITOUT_PLAN = "fitout_plan"  # the reviewed plan, kept in the building run's folder: the build builds exactly it
BUILT = ("built", "built_with_issues")


def _fitout_items(report: dict[str, Any]) -> list[dict[str, Any]]:
    """The plan's questions as stage items: what it waits for (blocks), then the room model's own (answered the same
    way, but they never stop the plan)."""
    items, seen = [], set()
    for q, blocks in [(q, True) for q in report.get("questions") or []] + \
                     [(q, False) for q in report.get("room_questions") or []]:
        if q["id"] in seen:
            continue
        seen.add(q["id"])
        items.append({"id": q["id"], "topic": q["topic"].removeprefix("fitout_"), "text": q["text"],
                      "options": q.get("options") or [], "key": {"items": q.get("instances"), "blocks": blocks}})
    return items


def _fitout_rules(project: ImportProject, given: str | None) -> tuple[str, str]:
    """The rule set to plan with: the one given, else the project location's (as the command line's --rules), said
    as an assumption in the result; without a location or a rule set for it, the user is asked."""
    from plinth.fitout import rules as fitout_rules

    if given and given.strip():
        return given.strip(), "given"
    try:
        found = fitout_rules.for_location(project.inputs.location, project.inputs.values.get("country"))
    except (LocationRequired, fitout_rules.RulesRequired) as e:
        raise HTTPException(409, {"errors": [f"{e} Or give the fit-out rule set (rules)."]}) from e
    loc = project.inputs.location
    where = ", ".join(x for x in (loc.city, loc.municipality, loc.state) if x)
    return found.id, f"the project location ({where})"


def _fitout_run(store: RunStore, run_id: str) -> dict[str, Any]:
    """A fit-out run as the page lists it: what it builds on, its status, and its issues (joins and circuits Revit
    refused: built, but not wholly; expected on families without connectors, never failures)."""
    run = store.get(run_id)
    record = (run.load("run") if run else None) or {"run_id": run_id, "status": "missing"}
    report = (run.load("fitout_report") if run else None) or {}
    keep = ("status", "document_title", "error", "hint", "failed_key", "question", "batches", "created_utc",
            "finished_utc", "removed_utc")
    return {"run_id": run_id, **{k: record.get(k) for k in keep},
            "building_run": report.get("building_run"), "furniture_run": report.get("furniture_run"),
            "systems": report.get("systems") or [], "flat": report.get("flat"), "level": report.get("level"),
            "partial": bool(report.get("partial")), "elements": len(record.get("created") or {}),
            "readback_ok": (record.get("readback") or {}).get("ok"), "issues": record.get("issues") or []}


def _fitout_view(saved: dict[str, Any], building_run: str, stored: dict[str, Any] | None = None) -> dict[str, Any]:
    """A saved plan as the page and the MCP tool show it. ``stale``: answers given since it was planned (plan again
    to use them: the build builds what was reviewed)."""
    planned = saved["planned"]
    answers = planned.get("answers") or {}
    plan = planned.get("plan")
    return {"run_id": building_run, "document_title": saved["document_title"], "rules": saved["rules"],
            "rules_from": saved["rules_from"], "planned_utc": saved["planned_utc"], "options": saved.get("request"),
            "ready": planned["ready"], "buildable": plan is not None, "ops": len(plan["ops"]) if plan else 0,
            "built_as": saved.get("built_as"), "report": planned["report"],
            "stale": sorted(k for k, v in (stored or {}).items() if answers.get(k) != v)}


def add_fitout_routes(app: FastAPI, service: BuildService, imports: Imports, finished, import_run) -> None:
    """Plan (reads the document: families matched and measured; nothing built), review (questions are import
    questions of stage "fitout"), build (only when asked: the page's button, the MCP tool), remove (/runs/{id}/remove)."""
    from plinth.fitout import session

    def building_run(import_id: str, run_id: str):
        run, _ = import_run(import_id, run_id)
        record = run.load("run") or {}
        if record.get("status") not in BUILT:
            raise HTTPException(409, f"Run {run_id} is {record.get('status')}: the fit-out goes on a built model.")
        return run, record

    def title_of(given: str, record: dict[str, Any]) -> str:
        title = given.strip()
        if not title:
            raise HTTPException(422, "Enter the title of the Revit document the building was built in.")
        if record.get("document_title") and title != record["document_title"]:
            raise HTTPException(409, f"Run {record.get('run_id')} was built in {record['document_title']!r}, not in "
                                     f"{title!r}: the fit-out goes in the building's own document.")
        return title

    @app.post("/imports/{import_id}/fitout/plan")
    def fitout_plan(import_id: str, request: FitoutPlanRequest) -> dict[str, Any]:
        """Plan the fit-out on a built run: nothing is built. The plan is kept with the run for the build."""
        finished(import_id)
        run, record = building_run(import_id, request.run_id)
        title = title_of(request.document_title, record)
        with imports.edit:
            project = imports.project(import_id)
            rules, rules_from = _fitout_rules(project, request.rules)
            answers = {**stage_questions.stage_values(project, "fitout"), **request.answers}
        try:
            inputs = session.prepare(service.store, run.id, rules, answers=answers, systems=request.systems,
                                     furniture_run=request.furniture_run or None, exclude_runs=request.exclude_runs,
                                     flat=request.flat or None, level=request.level or None)
        except session.SessionError as e:
            raise HTTPException(409, {"errors": [str(e)]}) from e
        (run.dir / f"{FITOUT_PLAN}.json").unlink(missing_ok=True)  # a failed plan leaves none to build

        def plan(revit, run_) -> dict[str, Any]:
            revit.check(session.PLAN_COMMANDS)
            try:
                planned = session.plan(revit, inputs, title)
            except session.Drift as e:
                return {"error": str(e), "stopped": True, "mismatches": e.mismatches}
            except session.SessionError as e:
                return {"error": str(e), "stopped": True}
            run_.save(FITOUT_PLAN, {"planned": planned.model_dump(mode="json"), "document_title": title,
                                    "rules": rules, "rules_from": rules_from, "planned_utc": _utc(),
                                    "import_id": import_id,
                                    "request": request.model_dump(exclude={"answers", "document_title", "run_id"})})
            return {"ready": planned.ready}

        try:
            out = service.run_on(run.id, plan, "fitout_planning").get("fitout_planning") or {}
        except Busy as e:
            raise HTTPException(409, str(e)) from e
        if out.get("mismatches"):
            raise HTTPException(409, {"errors": [out["error"]], "drift": {
                "furniture_run": request.furniture_run, "mismatches": out["mismatches"],
                "what_to_do": "The furniture laid out now is not what that run built (different answers or "
                              "measurements). Plan with the answers that run was built with, or remove the furniture "
                              "run and build furniture and MEP together."}})
        if out.get("stopped"):
            raise HTTPException(409, {"errors": [out["error"]]})
        if out.get("error"):
            raise HTTPException(502, {"errors": [f"Planning the fit-out failed: {out['error']}"]
                                                + ([out["hint"]] if out.get("hint") else [])})
        saved = run.load(FITOUT_PLAN)
        with imports.edit:
            project = imports.project(import_id)
            qs = stage_questions.merge(project, "fitout", _fitout_items(saved["planned"]["report"]))
            imports.store.save(project)
            stored = stage_questions.stage_values(project, "fitout")
        return {**_fitout_view(saved, run.id, stored),
                "questions": [q.model_dump(mode="json") for q in qs if q.open]}

    @app.post("/imports/{import_id}/fitout/build", status_code=202)
    def fitout_build(import_id: str, request: FitoutBuildRequest) -> dict[str, str]:
        """Build the reviewed plan as its own run on top of the building's run (the user's go-ahead)."""
        finished(import_id)
        run, record = building_run(import_id, request.run_id)
        saved = run.load(FITOUT_PLAN)
        if saved is None:
            raise HTTPException(409, "Plan the fit-out on this run first: nothing is built before you have reviewed "
                                     "its plan.")
        title = request.document_title.strip()
        if title != saved["document_title"]:
            raise HTTPException(409, f"The fit-out was planned in {saved['document_title']!r}, not in {title!r}; plan "
                                     "it again in the document to build in.")
        earlier = service.store.get(saved.get("built_as") or "")
        earlier_record = (earlier.load("run") if earlier else None) or {}
        if earlier and earlier_record.get("status") != "removed" and \
                not (earlier_record.get("status") == "failed" and not earlier_record.get("created")):
            raise HTTPException(409, f"This plan was already built as run {earlier.id}; remove that run to build it "
                                     "again, or plan the fit-out again for what is not built yet.")
        planned = session.Planned.model_validate(saved["planned"])
        if planned.plan is None:
            raise HTTPException(409, "Nothing to build: the plan has no ops (see what was left out and why).")
        if not planned.ready and not request.partial:
            open_qs = planned.report.get("questions") or []
            raise HTTPException(409, {"errors": ["Not built: the fit-out has open questions. Answer them and plan "
                                                 "again, or build what is ready (partial: their items wait)."]
                                                + [f"{q['id']}: {q['text']}" for q in open_qs], "questions": open_qs})
        report = {**planned.report, "answers": planned.answers, "import_id": import_id, "partial": request.partial,
                  "planned_utc": saved["planned_utc"]}
        try:
            run_id = service.start(planned.plan, title, {"fitout_report": report},
                                   require=session.required_commands(planned.plan))
        except Busy as e:
            raise HTTPException(409, str(e)) from e
        run.save(FITOUT_PLAN, {**saved, "built_as": run_id})
        with imports.edit:
            imports.update(import_id, fitout_runs=(imports.record(import_id).get("fitout_runs") or []) + [run_id])
        return {"run_id": run_id}

    @app.get("/imports/{import_id}/fitout")
    def fitout_state(import_id: str) -> dict[str, Any]:
        """What the page shows on load: the built runs to fit out, the latest plan, the fit-out runs."""
        from plinth.fitout.mepcore import NOTICE

        record = imports.record(import_id)
        if record is None:
            raise HTTPException(404, "No such import.")
        project = imports.store.get(import_id)
        stored = stage_questions.stage_values(project, "fitout") if project else {}
        buildings, latest = [], None
        for run_id in record.get("runs") or []:
            run = service.store.get(run_id)
            data = (run.load("run") if run else None) or {}
            if data.get("status") not in BUILT:
                continue
            buildings.append({"run_id": run_id, "status": data["status"], "document_title": data.get("document_title"),
                              "finished_utc": data.get("finished_utc")})
            saved = run.load(FITOUT_PLAN)
            if saved and (latest is None or saved["planned_utc"] >= latest["planned_utc"]):
                latest = _fitout_view(saved, run_id, stored)
        return {"building_runs": buildings, "plan": latest, "systems": list(session.SYSTEMS), "notice": NOTICE,
                "runs": [_fitout_run(service.store, r) for r in record.get("fitout_runs") or []]}


def main() -> None:
    import uvicorn

    logs.configure()
    uvicorn.run(create_app(), host=HOST, port=PORT)


if __name__ == "__main__":
    main()
