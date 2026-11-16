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

STAGES = ("drawings", "estimate")


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
