"""Build runner (PRD F7.1-F7.3).

Splits a build plan into batch_execute calls of at most 20 ops, one build phase per batch, in
dependency order. Each batch is one Revit undo step and either builds completely or not at all.
Stairs, railings and type pre-steps are the exception: the add-in will not run create_stair inside a batch,
and a type pre-step (ensure_wall_type, ensure_floor_type, ensure_family_type) returns the project type every later
op must use, so each of those ops is its own direct call (and undo step); a type the add-in cannot find or copy
without the user stops the run as needs_input. Keys are swapped for element ids as batches return;
an op that creates several elements (a stair and the railings Revit gives it) records them as key,
key#2, key#3... The first failed batch stops the run; what was
built so far is reported and can be removed. After a build, element counts and warnings are read back
and compared with the plan.
"""

from __future__ import annotations

import logging
import re
from collections import Counter
from dataclasses import dataclass, field
from typing import Any, Callable, Protocol

from b2m.plan import KEY_SEPARATOR, PHASE_OF, TYPE_OPS, BeamOp, BuildPlan, ColumnOp, Op, RailingOp
from b2m.revit import commands
from b2m.revit.client import RevitError, RevitTimeout
from b2m.revit.types import TypeMap

MAX_BATCH = 20
BATCH_TIMEOUT_SECONDS = 300
ENSURE_TIMEOUT_SECONDS = 600  # creating a family from its template can take a while

# Logical frame types carry their section in the name ("RCC Column 230x450": b 230, h 450).
SECTION = re.compile(r"(\d+)\s*[xX×]\s*(\d+)")
ENSURE_CATEGORY = {"column": "structural_column", "beam": "structural_framing"}
# Errors of a type pre-step (ensure_wall_type, ensure_floor_type, ensure_family_type) that need the user's answer:
# nothing to find or copy (no foundation slab type to copy, no family), a tie to choose from, a mark or size clash,
# a material that is not in the project. The run stops before anything is built, as needs_input.
TYPE_QUESTIONS = frozenset({
    "FAMILY_NOT_FOUND", "FAMILY_CHOICE_NEEDED", "TYPE_MARK_IN_USE", "TYPE_MARK_MISMATCH",
    "BASE_TYPE_NOT_FOUND", "TYPE_CHOICE_NEEDED", "TYPE_DIMENSION_MISMATCH", "MATERIAL_NOT_FOUND",
})

log = logging.getLogger(__name__)


class Revit(Protocol):
    def call(self, command: str, params: dict[str, Any] | None = None, *, timeout_seconds: int | None = None) -> Any: ...


@dataclass
class BuildResult:
    status: str  # "built", "failed", "unknown" (a batch timed out and may still finish) or "needs_input"
    batches_total: int
    batches_succeeded: int
    created: dict[str, int] = field(default_factory=dict)
    error: str | None = None
    error_code: str | None = None
    failed_key: str | None = None
    error_data: Any = None
    failures: list[dict[str, Any]] = field(default_factory=list)


def plan_batches(plan: BuildPlan) -> list[list[Op]]:
    """Ops in build order, cut at every phase change and every 20 ops. Ops called directly stand alone."""
    batches: list[list[Op]] = []
    for op in plan.ordered():
        last = batches[-1] if batches else None
        if (last is None or len(last) >= MAX_BATCH or PHASE_OF[last[-1].op] != PHASE_OF[op.op]
                or op.op in commands.DIRECT_OPS):
            batches.append([op])
        else:
            last.append(op)
    return batches


class Runner:
    def __init__(self, revit: Revit, progress: Callable[[int, int], None] | None = None, types: TypeMap | None = None,
                 allow_family_create: bool = False, family_folders: list[str] | None = None):
        self.revit = revit
        self.progress = progress or (lambda done, total: None)
        self.types = types or TypeMap()
        # A family B2M cannot find or adapt is only created from Revit's template when the user said yes.
        self.allow_family_create = allow_family_create
        self.family_folders = list(family_folders or [])
        # The type mapping in use during a build: the loaded one plus what the plan's type pre-steps resolved.
        self._active = self.types

    def build(self, plan: BuildPlan) -> BuildResult:
        by_key = plan.by_key()
        batches = plan_batches(plan)
        self._active = self.types
        result = BuildResult(status="built", batches_total=len(batches), batches_succeeded=0)
        stopped = self._ensure_frame_types(plan, result)
        if stopped is not None:
            return stopped
        self.progress(0, len(batches))
        for index, batch in enumerate(batches):
            if batch[0].op in commands.DIRECT_OPS:
                stopped = self._direct(batch[0], by_key, result)
                if stopped is not None:
                    return stopped
                result.batches_succeeded += 1
                self.progress(index + 1, len(batches))
                continue
            subcommands = [commands.to_command(op, by_key, result.created, self._active) for op in batch]
            log.info("batch %d of %d: %d ops (%s)", index + 1, len(batches), len(batch), batch[0].op)
            try:
                data = self.revit.call("batch_execute", {"commands": subcommands}, timeout_seconds=BATCH_TIMEOUT_SECONDS)
            except RevitTimeout as e:
                return self._stop(result, "unknown", e, None)
            except RevitError as e:
                failed = _failed_index(e.data)
                return self._stop(result, "failed", e, batch[failed].key if failed is not None and 0 <= failed < len(batch) else None)
            for op, entry in zip(batch, data["results"]):
                result.created.update(commands.created_keys(op, commands.created_ids(op, entry["data"])))
            result.batches_succeeded += 1
            self.progress(index + 1, len(batches))
        return result

    def _ensure_frame_types(self, plan: BuildPlan, result: BuildResult) -> BuildResult | None:
        """Before anything is built, make sure every column and beam type the plan uses exists in the project:
        the add-in uses it, duplicates it to the section, loads the family from disk or creates it from Revit's
        family template (ensure_family_type). A project without concrete frame families then still builds."""
        wanted: dict[tuple[str, str, str], tuple[Op, tuple[int, int]]] = {}
        for op in plan.ops:
            if not isinstance(op, (ColumnOp, BeamOp)):
                continue
            mapped = (self.types.column if isinstance(op, ColumnOp) else self.types.beam)(op.type, op.family)
            section = SECTION.search(op.type) or SECTION.search(mapped.type)
            if mapped.family is None or section is None:
                continue  # nothing to create it from; the build itself reports a missing type
            wanted.setdefault((op.op, mapped.family, mapped.type), (op, (int(section.group(1)), int(section.group(2)))))
        for (kind, family, type_name), (op, (b, h)) in wanted.items():
            params = {"category": ENSURE_CATEGORY[kind], "family_name": family, "type_name": type_name,
                      "dimensions": {"b_mm": b, "h_mm": h}}
            if self.family_folders:
                params["search_folders"] = self.family_folders
            if self.allow_family_create:
                params["allow_create"] = True
            try:
                data = self.revit.call("ensure_family_type", params, timeout_seconds=ENSURE_TIMEOUT_SECONDS)
            except RevitTimeout as e:
                return self._stop(result, "unknown", e, op.key)
            except RevitError as e:
                if e.code == "UNKNOWN_COMMAND":
                    log.warning("add-in has no ensure_family_type; building with the types already in the project")
                    return None
                # Not found and nothing to adapt: nothing was built; the user decides (create it, or name a family file).
                return self._stop(result, "needs_input" if e.code == "FAMILY_NOT_FOUND" else "failed", e, op.key)
            log.info("%s %s: %s (%s)", family, type_name, data.get("action"), data.get("source"))
        return None

    def _direct(self, op: Op, by_key: dict[str, Op], result: BuildResult) -> BuildResult | None:
        """One op as its own command. Returns the stopped result on failure, else None."""
        if isinstance(op, RailingOp) and op.stair_key is not None and f"{op.stair_key}{KEY_SEPARATOR}2" in result.created:
            # Revit already gave the stair its railings (the add-in refuses a second set): they stand for this op.
            log.info("%s: stair %s came with its own railings; not adding more", op.key, op.stair_key)
            return None
        command = commands.to_command(op, by_key, result.created, self._active)
        log.info("direct call: %s (%s)", command["command"], op.key)
        type_op = op.op in TYPE_OPS
        try:
            data = self.revit.call(command["command"], command["params"],
                                   timeout_seconds=ENSURE_TIMEOUT_SECONDS if type_op else BATCH_TIMEOUT_SECONDS)
        except RevitTimeout as e:
            return self._stop(result, "unknown", e, op.key)
        except RevitError as e:
            # A type that cannot be found, copied or chosen without the user is a question, not a failure.
            return self._stop(result, "needs_input" if type_op and e.code in TYPE_QUESTIONS else "failed", e, op.key)
        if type_op:
            self._active = commands.resolved_type(op, data, self._active)
            log.info("%s %s: %s %r", op.op, op.name, data.get("action"), data.get("type_name"))
        result.created.update(commands.created_keys(op, commands.created_ids(op, data)))
        return None

    def _stop(self, result: BuildResult, status: str, error: RevitError, failed_key: str | None) -> BuildResult:
        result.status = status
        result.error, result.error_code, result.failed_key = error.error, error.code, failed_key
        result.error_data = error.data
        result.failures = error.response.get("failures") or []
        log.warning("build stopped after %d of %d batches: %s", result.batches_succeeded, result.batches_total, error.error)
        return result

    def verify(self, plan: BuildPlan, created: dict[str, int]) -> dict[str, Any]:
        """Read back what was built: counts per category and level against the plan, missing ids, warnings."""
        by_key = plan.by_key()
        # Types a run made are removed with it but are not model elements: they are not read back.
        created = {k: v for k, v in created.items() if by_key[commands.op_key(k)].op not in TYPE_OPS}
        ids = list(created.values())
        stats = self.revit.call("analyze_model_statistics", {"elementIds": ids, "groupByLevel": True})
        warnings = self.revit.call("get_model_warnings_summary", {"elementIds": ids})

        expected: Counter[tuple[str, str | None]] = Counter()
        level_checked: set[str] = set()
        for key in created:
            op = by_key[commands.op_key(key)]
            category = commands.category_of(key, op)
            level = commands.expected_level(op, by_key) if key == op.key else None
            if level is not None:
                level_checked.add(category)
            expected[(category, level)] += 1

        actual: Counter[tuple[str, str | None]] = Counter()
        for row in stats["categories"]:
            category = row["builtInCategory"]
            actual[(category, row.get("level") if category in level_checked else None)] += row["count"]

        mismatches = [
            {"category": c, "level": lv, "expected": expected[(c, lv)], "actual": actual[(c, lv)]}
            for c, lv in sorted(set(expected) | set(actual), key=lambda k: (k[0], k[1] or ""))
            if expected[(c, lv)] != actual[(c, lv)]
        ]
        missing = stats.get("missingIds") or []
        return {
            "ok": not mismatches and not missing and warnings["total_warnings"] == 0,
            "elements": sum(expected.values()),
            "mismatches": mismatches,
            "missing_ids": missing,
            "warnings_total": warnings["total_warnings"],
            "warnings": warnings["warnings"],
        }

    def remove(self, created: dict[str, int]) -> dict[str, Any]:
        """Delete everything a run created, newest first, as one Revit undo step."""
        if not created:
            return {"deleted": 0, "failedCount": 0}
        ids = list(reversed(list(created.values())))
        result = self.revit.call("delete_element", {"elementIds": ids}, timeout_seconds=BATCH_TIMEOUT_SECONDS)
        if result.get("failedIds"):
            # Elements the user already removed (Ctrl+Z on a batch, or deleted by hand) are not failures.
            missing = set(self.revit.call("analyze_model_statistics", {"elementIds": result["failedIds"]}).get("missingIds") or [])
            if missing:
                result["alreadyGoneIds"] = [i for i in result["failedIds"] if i in missing]
                result["failedIds"] = [i for i in result["failedIds"] if i not in missing]
                result["failedCount"] = len(result["failedIds"])
                result["errors"] = [e for e in result.get("errors") or [] if int(e.split(":", 1)[0]) not in missing]
        return result


def _failed_index(data: Any) -> int | None:
    """Index of the failed sub-command in a batch error's results."""
    if not isinstance(data, dict):
        return None
    for entry in data.get("results") or []:
        if entry.get("ok") is False:
            return entry.get("index")
    return None
