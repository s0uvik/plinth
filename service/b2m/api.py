"""Local HTTP API. Binds 127.0.0.1 only (PRD: Security).

    python -m b2m.api            # serves http://127.0.0.1:8765
"""

from __future__ import annotations

from typing import Any

from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

from b2m import logs
from b2m.builds import BuildService, Busy, product_version
from b2m.plan import BuildPlan
from b2m.revit import RevitClient
from b2m.runs import RunStore

HOST, PORT = "127.0.0.1", 8765


class BuildRequest(BaseModel):
    plan: BuildPlan
    document_title: str | None = None


def create_app(service: BuildService | None = None) -> FastAPI:
    service = service or BuildService(RunStore())
    app = FastAPI(title="B2M", version=product_version())

    @app.get("/health")
    def health() -> dict[str, Any]:
        try:
            with RevitClient.connect() as revit:
                return {"service": product_version(), "revit": revit.health}
        except Exception as e:
            return {"service": product_version(), "revit": None, "revit_error": str(e)}

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
