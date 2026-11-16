"""Local HTTP API and web page. Binds 127.0.0.1 only (PRD: Security).

    python -m b2m.api            # serves http://127.0.0.1:8765
"""

from __future__ import annotations

import threading
from importlib.resources import files
from typing import Any, Callable

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse, HTMLResponse
from pydantic import BaseModel

from b2m import logs
from b2m.brief.agent import BriefFailed, BriefSession, MessagesClient, Questions, SpecReady, anthropic_client
from b2m.builds import BuildService, Busy, product_version
from b2m.compliance import evaluate, facts_after_build, facts_before_build, hard_failures
from b2m.compliance.pdf import write_pdf
from b2m.drawings import build_drawing_set
from b2m.gateway import Gateway, GatewayError
from b2m.engine import LayoutError, generate
from b2m.plan import BuildPlan, RoomOp
from b2m.revit import RevitClient
from b2m.runs import RunStore
from b2m.spec import BuildingSpec, parse_spec

HOST, PORT = "127.0.0.1", 8765


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
    allow_family_create: bool = False  # the user agreed B2M may create a frame family it could not find
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


def create_app(service: BuildService | None = None, brief_client: Callable[[], MessagesClient] = anthropic_client) -> FastAPI:
    service = service or BuildService(RunStore())
    app = FastAPI(title="B2M", version=product_version())
    briefs: dict[str, BriefSession] = {}
    briefs_lock = threading.Lock()

    @app.get("/", response_class=HTMLResponse)
    def page() -> str:
        return files("b2m.web").joinpath("index.html").read_text(encoding="utf-8")

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
        if run is None or name not in ("report", "report_before", "set"):
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

    return app


def main() -> None:
    import uvicorn

    logs.configure()
    uvicorn.run(create_app(), host=HOST, port=PORT)


if __name__ == "__main__":
    main()
