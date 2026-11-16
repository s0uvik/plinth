"""Questions the later stages of an import ask (drawing set, estimate), kept with the import's own questions.

The drawing set (``b2m.imported.drawings``) and the estimate (``b2m.imported.estimate``) refuse to guess: what they
cannot derive comes back as a question whose id is the answer key they read (``drawing:elevation:<file>``,
``estimate:material:D1`` ...). Here those become import questions, so the review page and the MCP tools ask them like
any other, and an answer is stored twice: on the question (the record of who answered what) and in
``project.inputs.values[id]``, where the stage reads it on its next run.

Stage questions never block a build (severity "check"): they block only their own stage, which says so when it runs.
An open stage question the stage no longer asks is dropped; answered ones stay as the record.
"""

from __future__ import annotations

from typing import Any, Iterable

from b2m.cad.project import ImportProject, Question

STAGES = ("drawings", "estimate", "build")


def merge(project: ImportProject, stage: str, items: Iterable[dict[str, Any]]) -> list[Question]:
    """Add (or refresh) the stage's questions; returns them as import questions, open ones first."""
    if stage not in STAGES:
        raise ValueError(f"unknown stage {stage}")
    items = list(items)
    wanted = {i["id"] for i in items}
    project.questions = [q for q in project.questions
                         if not (q.key.get("stage") == stage and q.open and q.id not in wanted)]
    out = []
    for item in items:
        options = [str(o) for o in item.get("options") or [] if o is not None]
        key = {"stage": stage, "answer_key": item["id"], **(item.get("key") or {})}
        try:
            q = project.question(item["id"])
        except KeyError:
            q = Question(id=item["id"], topic=f"{stage}_{item.get('topic') or 'input'}", text=item["text"],
                         options=options, severity=item.get("severity") or "check",
                         where=item.get("sheet") or item.get("where"), instances=list(item.get("instances") or []),
                         answer_type="stage_value", key=key)
            project.questions.append(q)
        else:
            if q.open:
                q.text, q.options, q.key = item["text"], options, {**q.key, **key}
                q.instances = list(item.get("instances") or q.instances)
        out.append(q)
    return sorted(out, key=lambda q: not q.open)


def from_build(project: ImportProject, run: dict[str, Any]) -> list[Question]:
    """A build that stopped for the user (status needs_input) because the add-in could not choose a family on its own:
    ask which family to use, keyed so the next plan carries it (``family:<category>`` in project.inputs.values)."""
    if run.get("status") != "needs_input" or run.get("error_code") != "FAMILY_CHOICE_NEEDED":
        return []
    key = run.get("failed_key") or ""
    parts = key.split("-", 2)  # TYPE-<category>-<name>
    category = parts[1] if len(parts) == 3 and parts[0] == "TYPE" else None
    if category is None:
        return []
    data = run.get("error_data") or {}
    names = [c.get("family_name") for c in data.get("candidates") or [] if c.get("family_name") and c.get("eligible", True)]
    item = {"id": f"family:{category}", "topic": "family",
            "text": f"Several loaded {category} families fit equally well, so B2M will not pick one: which family "
                    f"should the {category} types be made from? (asked by the build, run {run.get('run_id')})",
            "options": names}
    return merge(project, "build", [item])


def stage_values(project: ImportProject, stage: str) -> dict[str, Any]:
    """The answers given to the stage's questions, by answer key."""
    return {q.key.get("answer_key", q.id): q.answer for q in project.questions
            if q.key.get("stage") == stage and not q.open}


def record(project: ImportProject, q: Question) -> None:
    """After an answer: a stage question's answer is also the input value its stage reads."""
    if q.key.get("stage") in STAGES and not q.open:
        project.set_value(q.key.get("answer_key", q.id), q.answer)


def open_for(project: ImportProject, stage: str) -> list[Question]:
    return [q for q in project.questions if q.key.get("stage") == stage and q.open]
