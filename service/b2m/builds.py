"""Build and remove runs: connects to Revit, confirms the document, runs the plan, reads it back,
and keeps everything in the run store. One Revit operation at a time."""

from __future__ import annotations

import logging
import threading
from datetime import datetime, timezone
from importlib.metadata import PackageNotFoundError, version
from typing import Any, Callable

from b2m.plan import BuildPlan
from b2m.revit import RevitClient
from b2m.revit.commands import REQUIRED_COMMANDS
from b2m.revit.errors import hint
from b2m.revit.types import TypeMap
from b2m.runner import Runner
from b2m.runs import Run, RunStore

log = logging.getLogger(__name__)


def product_version() -> str:
    try:
        return version("b2m")
    except PackageNotFoundError:
        return "0.0.0"


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Busy(Exception):
    """Another build or removal is running."""


class BuildService:
    def __init__(self, store: RunStore, connect: Callable[[], RevitClient] | None = None, types: TypeMap | None = None):
        self.store = store
        self.types = types if types is not None else TypeMap.load()
        self._connect = connect or (lambda: RevitClient.connect(required_commands=REQUIRED_COMMANDS))
        self._lock = threading.Lock()

    def start(self, plan: BuildPlan, document_title: str | None = None, attachments: dict[str, Any] | None = None,
              after_build: Callable[[RevitClient, Run, dict[str, int]], dict[str, Any]] | None = None) -> str:
        """Start a build in the background and return its run id. Attachments (spec, layout report, brief)
        are saved in the run beside the plan."""
        if not self._lock.acquire(blocking=False):
            raise Busy("A build or removal is already running.")
        try:
            run = self.store.create()
            run.save("plan", plan.model_dump(mode="json"))
            for name, data in (attachments or {}).items():
                run.save(name, data)
            run.update(status="queued", created_utc=_now(), expected_document_title=document_title)
        except Exception:
            self._lock.release()
            raise
        threading.Thread(target=self._build, args=(run, plan, document_title, after_build), name=f"build-{run.id}",
                         daemon=True).start()
        return run.id

    def build_now(self, plan: BuildPlan, document_title: str | None = None) -> dict[str, Any]:
        """Run a build in this thread and return the final run record."""
        run_id = self.start(plan, document_title)
        with self._lock:  # Waits for the background build to finish.
            pass
        return self.store.get(run_id).load("run")

    def _build(self, run: Run, plan: BuildPlan, document_title: str | None, after_build=None) -> None:
        try:
            with self._open(run) as revit:
                title = revit.confirm_document(document_title)
                run.update(status="building", document_title=title, versions=self._versions(revit, plan))
                runner = Runner(revit, progress=lambda done, total: run.update(batches={"total": total, "succeeded": done}),
                                types=self.types)
                result = runner.build(plan)
                run.update(
                    status=result.status,
                    batches={"total": result.batches_total, "succeeded": result.batches_succeeded},
                    created=result.created,
                    error=result.error, error_code=result.error_code, failed_key=result.failed_key, hint=hint(result.error),
                    error_data=result.error_data, failures=result.failures,
                )
                if result.status == "built":
                    readback = runner.verify(plan, result.created)
                    run.update(readback=readback, status="built" if readback["ok"] else "built_with_issues")
                    if after_build is not None:  # e.g. compliance checks on rooms read back from Revit (F8.1)
                        try:
                            run.update(checks=after_build(revit, run, result.created))
                        except Exception as e:  # The model is built; a failed check must not hide that.
                            log.exception("after-build step failed for run %s", run.id)
                            run.update(checks={"error": f"{type(e).__name__}: {e}"})
        except Exception as e:  # Connection or protocol problems: record them, never lose the run.
            log.exception("run %s failed", run.id)
            run.update(status="failed", error=f"{type(e).__name__}: {e}", hint=hint(str(e)) or getattr(e, "hint", None))
        finally:
            run.update(finished_utc=_now())
            self._lock.release()

    def remove(self, run_id: str) -> dict[str, Any]:
        """Delete everything a run created. Only ids recorded in the run store are ever deleted."""
        run = self.store.get(run_id)
        if run is None:
            raise KeyError(run_id)
        record = run.load("run")
        created: dict[str, int] = record.get("created") or {}
        if not self._lock.acquire(blocking=False):
            raise Busy("A build or removal is already running.")
        try:
            with self._open(run) as revit:
                revit.confirm_document(record.get("document_title"))
                run.update(status="removing")
                result = Runner(revit).remove(created)
                failed = result.get("failedCount", 0)
                return run.update(status="removed" if not failed else "remove_incomplete", removal=result, removed_utc=_now())
        except Exception as e:
            log.exception("removing run %s failed", run_id)
            return run.update(status="remove_failed", error=f"{type(e).__name__}: {e}")
        finally:
            self._lock.release()

    def run_on(self, run_id: str, work: Callable[[RevitClient, Run], dict[str, Any]], status: str) -> dict[str, Any]:
        """Run work against a run's own document (confirmed by its title), one Revit operation at a time."""
        run = self.store.get(run_id)
        if run is None:
            raise KeyError(run_id)
        record = run.load("run")
        if not self._lock.acquire(blocking=False):
            raise Busy("A build or removal is already running.")
        try:
            with self._open(run) as revit:
                revit.confirm_document(record.get("document_title"))
                run.update(activity=status)
                return run.update(**{status: work(revit, run)}, activity=None)
        except Exception as e:
            log.exception("%s failed for run %s", status, run_id)
            return run.update(activity=None, **{status: {"error": f"{type(e).__name__}: {e}", "hint": hint(str(e))}})
        finally:
            self._lock.release()

    def _open(self, run: Run) -> RevitClient:
        revit = self._connect()
        revit.observer = lambda request, response: run.append("tool_calls", {"at": _now(), "request": request, "response": response})
        return revit

    @staticmethod
    def _versions(revit: RevitClient, plan: BuildPlan) -> dict[str, Any]:
        return {
            "product": product_version(),
            "addin": revit.health.get("addin_version"),
            "revit": revit.health.get("revit_year"),
            "plan": plan.plan_version,
        }
