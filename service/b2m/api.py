"""Local HTTP API and web page. Binds 127.0.0.1 only (PRD: Security).

    python -m b2m.api            # serves http://127.0.0.1:8765
"""

from __future__ import annotations

import threading
from importlib.resources import files
from typing import Any, Callable

from fastapi import FastAPI, HTTPException
from fastapi.responses import HTMLResponse
from pydantic import BaseModel

from b2m import logs
from b2m.brief.agent import BriefFailed, BriefSession, MessagesClient, Questions, SpecReady, anthropic_client
from b2m.builds import BuildService, Busy, product_version
from b2m.engine import LayoutError, generate
from b2m.plan import BuildPlan
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


def layout_summary(spec: BuildingSpec) -> dict[str, Any]:
    """What the user should see before building: the layout the engine would make, or why it cannot."""
    try:
        report = generate(spec).report
    except LayoutError as e:
        return {"ok": False, "error": str(e)}
    keep = ("template", "variant", "height_mm", "footprint_m2", "plot_area_m2", "ground_coverage", "far",
            "setbacks_mm", "notes", "levels", "lift")
    summary = {k: report[k] for k in keep}
    summary["flats_per_floor"] = [{k: f[k] for k in ("letter", "type", "target_carpet_m2", "carpet_m2", "width_mm", "depth_mm")}
                                  for f in report["flats_per_floor"]]
    return {"ok": True, **summary}


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
        attachments: dict[str, Any] = {"spec": spec.model_dump(mode="json"), "layout_report": result.report}
        if request.brief_id:
            with briefs_lock:
                session = briefs.get(request.brief_id)
            attachments["brief"] = {"id": request.brief_id, "text": session.brief if session else None}
        try:
            return {"run_id": service.start(result.plan, request.document_title, attachments)}
        except Busy as e:
            raise HTTPException(409, str(e)) from e

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
