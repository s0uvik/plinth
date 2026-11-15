"""Build runner (PRD F7.1-F7.3).

Splits a build plan into batch_execute calls of at most 20 ops, one build phase per batch, in
dependency order. Each batch is one Revit undo step and either builds completely or not at all.
Keys are swapped for element ids as batches return. The first failed batch stops the run; what was
built so far is reported and can be removed. After a build, element counts and warnings are read back
and compared with the plan.
"""

from __future__ import annotations

import logging
from collections import Counter
from dataclasses import dataclass, field
from typing import Any, Callable, Protocol

from b2m.plan import PHASE_OF, BuildPlan, Op
from b2m.revit import commands
from b2m.revit.client import RevitError, RevitTimeout
from b2m.revit.types import TypeMap

MAX_BATCH = 20
BATCH_TIMEOUT_SECONDS = 300

log = logging.getLogger(__name__)


class Revit(Protocol):
    def call(self, command: str, params: dict[str, Any] | None = None, *, timeout_seconds: int | None = None) -> Any: ...


@dataclass
class BuildResult:
    status: str  # "built", "failed" or "unknown" (a batch timed out and may still finish)
    batches_total: int
    batches_succeeded: int
    created: dict[str, int] = field(default_factory=dict)
    error: str | None = None
    error_code: str | None = None
    failed_key: str | None = None
    error_data: Any = None
    failures: list[dict[str, Any]] = field(default_factory=list)


def plan_batches(plan: BuildPlan) -> list[list[Op]]:
    """Ops in build order, cut at every phase change and every 20 ops."""
    batches: list[list[Op]] = []
    for op in plan.ordered():
        last = batches[-1] if batches else None
        if last is None or len(last) >= MAX_BATCH or PHASE_OF[last[-1].op] != PHASE_OF[op.op]:
            batches.append([op])
        else:
            last.append(op)
    return batches


class Runner:
    def __init__(self, revit: Revit, progress: Callable[[int, int], None] | None = None, types: TypeMap | None = None):
        self.revit = revit
        self.progress = progress or (lambda done, total: None)
        self.types = types or TypeMap()

    def build(self, plan: BuildPlan) -> BuildResult:
        by_key = plan.by_key()
        batches = plan_batches(plan)
        result = BuildResult(status="built", batches_total=len(batches), batches_succeeded=0)
        self.progress(0, len(batches))
        for index, batch in enumerate(batches):
            subcommands = [commands.to_command(op, by_key, result.created, self.types) for op in batch]
            log.info("batch %d of %d: %d ops (%s)", index + 1, len(batches), len(batch), batch[0].op)
            try:
                data = self.revit.call("batch_execute", {"commands": subcommands}, timeout_seconds=BATCH_TIMEOUT_SECONDS)
            except RevitTimeout as e:
                return self._stop(result, "unknown", e, None)
            except RevitError as e:
                failed = _failed_index(e.data)
                return self._stop(result, "failed", e, batch[failed].key if failed is not None and 0 <= failed < len(batch) else None)
            for op, entry in zip(batch, data["results"]):
                result.created[op.key] = entry["data"]["elementId"]
            result.batches_succeeded += 1
            self.progress(index + 1, len(batches))
        return result

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
        ids = list(created.values())
        stats = self.revit.call("analyze_model_statistics", {"elementIds": ids, "groupByLevel": True})
        warnings = self.revit.call("get_model_warnings_summary", {"elementIds": ids})

        expected: Counter[tuple[str, str | None]] = Counter()
        level_checked: set[str] = set()
        for key in created:
            op = by_key[key]
            level = commands.expected_level(op, by_key)
            if level is not None:
                level_checked.add(commands.CATEGORY[op.op])
            expected[(commands.CATEGORY[op.op], level)] += 1

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
        return self.revit.call("delete_element", {"elementIds": ids}, timeout_seconds=BATCH_TIMEOUT_SECONDS)


def _failed_index(data: Any) -> int | None:
    """Index of the failed sub-command in a batch error's results."""
    if not isinstance(data, dict):
        return None
    for entry in data.get("results") or []:
        if entry.get("ok") is False:
            return entry.get("index")
    return None
