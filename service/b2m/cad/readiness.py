"""Readiness report for a drawing import (PRD Step 9, T5): can the model be built yet, and what is left.

``ready`` is true when no blocking question is open: the questions are settled enough to build. Building also needs
the review approved (PRD stage 6: nothing builds until the user approves what B2M understood); that is listed under
``approvals_needed``, together with the assumptions the approval confirms (those resting on office practice).
Checks left are listed but do not stop a build; info items are counted.
"""

from __future__ import annotations

from typing import Any

from b2m.cad.project import ImportProject

OFFICE = "office practice, confirm"


def approved(project: ImportProject) -> bool:
    """The latest review approval stands (an approval withdrawn after a change does not)."""
    for a in reversed(project.approvals):
        if a.what in ("review", "review_withdrawn"):
            return a.what == "review"
    return False


def _item(q) -> dict[str, Any]:
    return {"id": q.id, "topic": q.topic, "severity": q.severity, "text": q.text, "where": q.where}


def ready_to_build(project: ImportProject) -> dict[str, Any]:
    from b2m.cad.questions import sync_inputs

    sync_inputs(project)
    open_ = project.open_questions()
    blocking = [_item(q) for q in open_ if q.severity == "blocking"]
    checks = [_item(q) for q in open_ if q.severity == "check"]
    info = [q for q in project.questions if q.severity == "info"]
    to_confirm = [{"id": a.id, "text": a.text, "basis": a.basis, "question": a.question}
                  for a in project.assumptions if OFFICE in a.basis.lower()]
    is_approved = approved(project)
    approvals = [] if is_approved else [
        "review: approve what B2M understood from the drawings"
        + (f" (confirming {len(to_confirm)} assumption{'s' if len(to_confirm) != 1 else ''} from office practice)"
           if to_confirm else "")]
    return {
        "ready": not blocking,
        "approved": is_approved,
        "can_build": not blocking and is_approved,
        "blocking": blocking,
        "checks": checks,
        "info": len(info),
        "answered": sum(1 for q in project.questions if not q.open),
        "assumptions": [{"id": a.id, "text": a.text, "basis": a.basis, "question": a.question, "at": a.at}
                        for a in project.assumptions],
        "to_confirm": to_confirm,
        "approvals_needed": approvals,
    }


def lines(report: dict[str, Any]) -> list[str]:
    out = [f"READY TO BUILD: {'yes' if report['ready'] else 'no'}"
           f" ({len(report['blocking'])} blocking, {len(report['checks'])} checks open, {report['info']} info, "
           f"{report['answered']} answered, {len(report['assumptions'])} assumed)"]
    out += [f"  blocking {b['id']}: {b['text']}" for b in report["blocking"]]
    out += [f"  check    {c['id']}: {c['text']}" for c in report["checks"]]
    out += [f"  assumed  {a['id']} ({a['question']}): {a['text']} [{a['basis']}]" for a in report["assumptions"]]
    out += [f"  approval needed: {a}" for a in report["approvals_needed"]]
    return out
