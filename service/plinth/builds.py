"""Build and remove runs: connects to Revit, confirms the document, runs the plan, reads it back,
and keeps everything in the run store. One Revit operation at a time.

A run may build on an earlier run's elements (a fit-out on the building's walls, MEP on the furniture run's fixtures,
the rest of a fit-out on a pilot's mains: ``BuildPlan.existing``). The run record keeps whose they are
(``existing_runs``: key -> run id, when the caller gives it), so a piece a tee breaks off an earlier run's main is
recorded in that run (removed with it, never with the later run), and a run another run still builds on is not
removed (``InUse``): the later run goes first."""

from __future__ import annotations

import logging
import threading
from datetime import datetime, timezone
from importlib.metadata import PackageNotFoundError, version
from typing import Any, Callable

from plinth.plan import KEY_SEPARATOR, BuildPlan
from plinth.revit import RevitClient
from plinth.revit.commands import PIECE_MARK, REQUIRED_COMMANDS
from plinth.revit.errors import hint
from plinth.revit.types import TypeMap
from plinth.runner import Runner
from plinth.runs import Run, RunStore

log = logging.getLogger(__name__)

# A fit-out's type steps that stop for the user (see Runner): another family type is chosen when re-planning.
FITOUT_TYPE_QUESTIONS = frozenset({"TYPE_NOT_IN_FILE", "PARAMETER_NOT_FOUND", "SET_FAILED"})


def product_version() -> str:
    try:
        return version("plinth")
    except PackageNotFoundError:
        return "0.0.0"


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Busy(Exception):
    """Another build or removal is running."""


class InUse(Busy):
    """The run cannot be removed yet: another run builds on its elements (hosts on its walls, joins its fixtures or
    mains), and removing it first would cut them. A Busy, so a caller that answers Busy (the API's 409) says it as
    it is."""


class BuildService:
    def __init__(self, store: RunStore, connect: Callable[[], RevitClient] | None = None, types: TypeMap | None = None):
        self.store = store
        self.types = types if types is not None else TypeMap.load()
        self._connect = connect or (lambda: RevitClient.connect(required_commands=REQUIRED_COMMANDS))
        self._lock = threading.Lock()

    def start(self, plan: BuildPlan, document_title: str | None = None, attachments: dict[str, Any] | None = None,
              after_build: Callable[[RevitClient, Run, dict[str, int]], dict[str, Any]] | None = None,
              families: dict[str, Any] | None = None, require: list[str] | None = None,
              owners: dict[str, str] | None = None) -> str:
        """Start a build in the background and return its run id. Attachments (spec, layout report, brief)
        are saved in the run beside the plan. ``require``: add-in commands the plan needs on top of the connection's
        own (a fit-out's), checked before anything is built, so an older add-in fails the run at once, not mid-way.
        ``owners``: the run each of the plan's existing elements belongs to (key -> run id), kept as the record's
        ``existing_runs`` (without it, they are found by their ids in the run store)."""
        if not self._lock.acquire(blocking=False):
            raise Busy("A build or removal is already running.")
        try:
            run = self.store.create()
            run.save("plan", plan.model_dump(mode="json"))
            for name, data in (attachments or {}).items():
                run.save(name, data)
            run.update(status="queued", created_utc=_now(), expected_document_title=document_title,
                       **({"existing_runs": dict(owners)} if owners else {}))
        except Exception:
            self._lock.release()
            raise
        threading.Thread(target=self._build, args=(run, plan, document_title, after_build, families or {}, require),
                         name=f"build-{run.id}", daemon=True).start()
        return run.id

    def build_now(self, plan: BuildPlan, document_title: str | None = None, families: dict[str, Any] | None = None,
                  owners: dict[str, str] | None = None) -> dict[str, Any]:
        """Run a build in this thread and return the final run record. ``families``: {"allow_create": bool,
        "folders": [paths]} for frame families the project lacks (see Runner); ``owners``: see ``start``."""
        run_id = self.start(plan, document_title, families=families, owners=owners)
        with self._lock:  # Waits for the background build to finish.
            pass
        return self.store.get(run_id).load("run")

    def _build(self, run: Run, plan: BuildPlan, document_title: str | None, after_build=None,
               families: dict[str, Any] | None = None, require: list[str] | None = None) -> None:
        families = families or {}
        try:
            with self._open(run) as revit:
                if require:
                    revit.check(require)
                title = revit.confirm_document(document_title)
                run.update(status="building", document_title=title, versions=self._versions(revit, plan))
                runner = Runner(revit, progress=lambda done, total: run.update(batches={"total": total, "succeeded": done}),
                                types=self.types, allow_family_create=bool(families.get("allow_create")),
                                family_folders=families.get("folders"))
                result = runner.build(plan)
                run.update(
                    status=result.status,
                    batches={"total": result.batches_total, "succeeded": result.batches_succeeded},
                    created=result.created,
                    error=result.error, error_code=result.error_code, failed_key=result.failed_key, hint=hint(result.error),
                    error_data=result.error_data, failures=result.failures, issues=result.issues,
                    # an MEP run's log and the pieces of earlier mains (a result without them has none)
                    **({"mep_log": result.mep_log} if getattr(result, "mep_log", None) else {}),
                    **({"pieces_of_existing": self._adopt(run, plan, result.adopted)}
                       if getattr(result, "adopted", None) else {}),
                )
                if result.status == "needs_input" and result.error_code in FITOUT_TYPE_QUESTIONS:
                    data = result.error_data if isinstance(result.error_data, dict) else {}
                    run.update(question={
                        "text": "A fit-out type could not be brought in or sized as planned. Choose another family type "
                                "for it (or leave the item out) and plan the fit-out again.",
                        "missing": result.error, "items": data.get("items"), "path": data.get("path")})
                elif result.status == "needs_input":
                    data = result.error_data if isinstance(result.error_data, dict) else {}
                    run.update(question={
                        "text": "Plinth could not find a family it needs, nor a similar one to adapt. May it create one from "
                                "Revit's family template, or do you have a family file to use?",
                        "missing": result.error, "candidates": data.get("candidates"),
                        "searched": data.get("searched_roots"), "can_create": data.get("can_create")})
                if result.status == "built":
                    readback = runner.verify(plan, result.created)
                    # built with issues: the read-back differs, or the add-in did not wholly build some op (issues)
                    ok = readback["ok"] and not result.issues
                    run.update(readback=readback, status="built" if ok else "built_with_issues")
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

    def _adopt(self, run: Run, plan: BuildPlan, adopted: dict[str, list[int]]) -> dict[str, Any]:
        """Pieces this run's tees broke off mains of earlier runs, recorded in the main's run (key#p<n>, after its own
        pieces), so removing that run removes the whole main and removing this one never cuts it. The main's run is
        the record's ``existing_runs`` entry, else the run whose record has the main's key with its id. A main whose
        run is not found keeps its pieces unrecorded: they are listed here, and neither run removes them."""
        owners = (run.load("run") or {}).get("existing_runs") or {}
        out: dict[str, Any] = {}
        for main, ids in adopted.items():
            owner = self.store.get(owners[main]) if owners.get(main) else self._built_by(main, plan.existing.get(main))
            if owner is None:
                out[main] = {"run": None, "ids": ids, "note": "the main's run is not known: not removed with any run"}
                continue
            created = dict((owner.load("run") or {}).get("created") or {})
            mark = f"{main}{KEY_SEPARATOR}{PIECE_MARK}"
            n = max([int(k[len(mark):]) for k in created if k.startswith(mark) and k[len(mark):].isdigit()], default=0)
            for i in ids:
                n += 1
                created[f"{mark}{n}"] = i
            owner.update(created=created)
            out[main] = {"run": owner.id, "ids": ids}
            log.info("run %s: %d piece(s) of %s recorded in run %s", run.id, len(ids), main, owner.id)
        return out

    def _built_by(self, key: str, element_id: int | None) -> Run | None:
        """The run (not removed) whose record has ``key`` with ``element_id``."""
        if element_id is None:
            return None
        for record in self.store.list(limit=1_000_000):
            if record.get("status") != "removed" and (record.get("created") or {}).get(key) == element_id:
                return self.store.get(record["run_id"])
        return None

    def dependants(self, run_id: str, created: dict[str, int]) -> list[tuple[str, list[str]]]:
        """The runs (not removed) that build on this run's elements, with the keys they use: by their record's
        ``existing_runs``, else (a run recorded before it was kept) by their plan's existing element ids."""
        ids = set(created.values())
        out = []
        for record in self.store.list(limit=1_000_000):
            other = record.get("run_id")
            if other == run_id or record.get("status") == "removed" or not record.get("created"):
                continue
            owners = record.get("existing_runs")
            if owners is not None:
                keys = sorted(k for k, owner in owners.items() if owner == run_id)
            else:
                found = self.store.get(other)
                existing = ((found.load("plan") or {}).get("existing") or {}) if found else {}
                keys = sorted(k for k, i in existing.items() if i in ids)
            if keys:
                out.append((other, keys))
        return out

    def remove(self, run_id: str) -> dict[str, Any]:
        """Delete everything a run created. Only ids recorded in the run store are ever deleted. A run another run
        still builds on is not removed (``InUse``: remove that run first)."""
        run = self.store.get(run_id)
        if run is None:
            raise KeyError(run_id)
        record = run.load("run")
        created: dict[str, int] = record.get("created") or {}
        later = self.dependants(run_id, created)
        if later:
            other, keys = later[0]
            raise InUse(f"Run {run_id} is not removed: run {other} builds on {len(keys)} of its elements (e.g. "
                        f"{keys[0]}), and removing this one first would cut them. Remove run {other} first"
                        + (f", and {', '.join(o for o, _ in later[1:])}" if len(later) > 1 else "") + ".")
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
