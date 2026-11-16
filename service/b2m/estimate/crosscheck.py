"""Estimate quantities vs what the drawings show: every disagreement becomes a question, nothing is corrected.

Drawing observations are counts read off a sheet: mark texts (D, D1, w1) or drawn symbols of one width (unmarked
doors), per storey the sheet stands for. Facts the drawings do not show at all (materials, heights, railing lengths)
are listed as unverified, so the user knows which numbers rest on the estimate alone.
"""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

from b2m.estimate.engine import num_text
from b2m.estimate.quantities import Opening, Quantities


class DrawingObservation(BaseModel):
    model_config = ConfigDict(extra="forbid")

    source: str = Field(description="Sheet and entities, e.g. \"2.PLAN 1st to 4th.dxf: TEXT 'D1'\"")
    storeys: list[int] = Field(min_length=1, description="Storeys the sheet stands for (typical plan: 1..4)")
    kind: Literal["door", "window"]
    mark: str | None = Field(default=None, description="Mark text; compared ignoring letter case")
    width_mm: float | None = Field(default=None, description="Drawn width, for symbols counted by size")
    count_per_storey: int = Field(ge=0)


class Finding(BaseModel):
    status: Literal["agrees", "disagrees", "not_on_drawing"]
    subject: str
    drawing: str
    estimate: str
    question: str | None = None


class CrossCheck(BaseModel):
    findings: list[Finding]
    unverified: list[str]

    @property
    def questions(self) -> list[str]:
        return [f.question for f in self.findings if f.question]

    def text(self) -> str:
        lines = ["Estimate quantities vs drawings"]
        for f in self.findings:
            lines.append(f"  {f.status:<15} {f.subject}: drawing {f.drawing} | estimate {f.estimate}")
        if self.unverified:
            lines.append("  Not shown on the drawings (estimate only):")
            lines += [f"    - {u}" for u in self.unverified]
        return "\n".join(lines)


def _breakdown(ops: list[Opening]) -> str:
    parts: dict[str, int] = {}
    for o in ops:
        key = f"{o.mark} {num_text(o.width_mm)}" + (f" {o.material}" if o.material else "")
        parts[key] = parts.get(key, 0) + o.count
    return " + ".join(f"{v} {k}" for k, v in parts.items()) or "none"


def _per_storey(ops: list[Opening]) -> str:
    by: dict[int, list[Opening]] = {}
    for o in ops:
        by.setdefault(o.storey, []).append(o)
    counts = {s: sum(o.count for o in v) for s, v in by.items()}
    if len(set(counts.values())) == 1:
        return f"{next(iter(counts.values()))} per storey = {_breakdown(next(iter(by.values())))}"
    return ", ".join(f"storey {s}: {c}" for s, c in sorted(counts.items()))


def _storeys(s: list[int]) -> str:
    s = sorted(s)
    return f"storey {s[0]}" if len(s) == 1 else (f"storeys {s[0]}-{s[-1]}" if s == list(range(s[0], s[-1] + 1))
                                                   else f"storeys {s}")


def crosscheck(q: Quantities, observations: list[DrawingObservation]) -> CrossCheck:
    findings: list[Finding] = []

    def matching(obs: DrawingObservation, storey: int) -> list[Opening]:
        return [o for o in q.openings if o.kind == obs.kind and o.storey == storey
                and (obs.mark is None or o.mark.casefold() == obs.mark.casefold())
                and (obs.width_mm is None or abs(o.width_mm - obs.width_mm) < 0.5)]

    for obs in observations:
        what = (f"{obs.kind} mark {obs.mark!r}" if obs.mark else f"{obs.kind}s") + \
               (f" drawn {num_text(obs.width_mm)} wide" if obs.width_mm else "")
        per = {s: matching(obs, s) for s in obs.storeys}
        counts = {s: sum(o.count for o in ops) for s, ops in per.items()}
        subject = f"{what}, {_storeys(obs.storeys)}"
        drawing = f"{obs.count_per_storey} per storey ({obs.source})"
        if len(set(counts.values())) == 1:
            est = f"{next(iter(counts.values()))} per storey = {_breakdown(next(iter(per.values())))}"
        else:
            est = ", ".join(f"storey {s}: {c}" for s, c in counts.items())
        if all(c == obs.count_per_storey for c in counts.values()):
            findings.append(Finding(status="agrees", subject=subject, drawing=drawing, estimate=est))
        else:
            findings.append(Finding(status="disagrees", subject=subject, drawing=drawing, estimate=est,
                                    question=f"{subject}: the drawing shows {obs.count_per_storey} per storey "
                                             f"({obs.source}) but the estimate has {est}. Which is right?"))

    # marks in the estimate that the drawing never names, on storeys whose sheets were read for marks
    marked = {(o.kind, s) for o in observations if o.mark for s in o.storeys}
    named = {(o.kind, s, o.mark.casefold()) for o in observations if o.mark for s in o.storeys}
    missing: dict[tuple[str, str], list[Opening]] = {}
    for o in q.openings:
        if (o.kind, o.storey) in marked and (o.kind, o.storey, o.mark.casefold()) not in named:
            missing.setdefault((o.kind, o.mark), []).append(o)
    for (kind, mark), ops in missing.items():
        st = sorted({o.storey for o in ops})
        sizes = sorted({f"{num_text(o.width_mm)} x {num_text(o.height_mm)}" for o in ops})
        widths = {o.width_mm for o in ops}
        by_width = [ob for ob in observations if ob.kind == kind and ob.width_mm in widths and not ob.mark]
        hint = (f"; counted only as drawn {kind}s of that width ({', '.join(ob.source for ob in by_width)})"
                if by_width else "")
        findings.append(Finding(
            status="not_on_drawing", subject=f"{kind} {mark!r}, {_storeys(st)}",
            drawing=f"no mark text {mark!r}{hint}", estimate=_per_storey(ops) + f" ({', '.join(sizes)})",
            question=f"The estimate has {kind} {mark!r} ({', '.join(sizes)}) on {_storeys(st)}, but no mark "
                     f"{mark!r} appears on the drawing{hint}. Is {mark!r} the right name and size for these?"))

    # materials split one mark into several: the drawings show no material
    splits: dict[tuple[str, int], dict[str, int]] = {}
    for o in q.openings:
        d = splits.setdefault((o.mark, o.storey), {})
        d[o.material or "not known"] = d.get(o.material or "not known", 0) + o.count
    grouped: dict[tuple[str, tuple], list[int]] = {}
    for (mark, storey), mats in splits.items():
        if len(mats) > 1:
            grouped.setdefault((mark, tuple(sorted(mats.items()))), []).append(storey)
    for (mark, mats), storeys in grouped.items():
        txt = " + ".join(f"{c} {m}" for m, c in mats)
        findings.append(Finding(
            status="not_on_drawing", subject=f"material of {mark}, {_storeys(storeys)}",
            drawing="no material shown", estimate=f"{txt} per storey",
            question=f"{mark} on {_storeys(storeys)} is split by material in the estimate ({txt} per storey); the "
                     f"drawing shows no material. Which {mark} doors are which material?"))

    unverified = []
    observed_widths = {(o.kind, o.width_mm) for o in observations if o.width_mm}
    for mark in dict.fromkeys(o.mark for o in q.openings):
        ops = [o for o in q.openings if o.mark == mark]
        w_ok = all((o.kind, o.width_mm) in observed_widths for o in ops)
        sizes = sorted({f"{num_text(o.width_mm)} x {num_text(o.height_mm)}" for o in ops})
        unverified.append(f"{mark}: {'height' if w_ok else 'width and height'} ({', '.join(sizes)})")
    for r in q.railings:
        name = " ".join(p for p in ("railing", r.kind, r.subkind) if p)
        unverified.append(f"{name}: {r.count} x {num_text(r.length_mm)} mm")
    mats = sorted({f"{o.mark} {o.material}" for o in q.openings if o.material})
    if mats:
        unverified.append("materials: " + ", ".join(mats))
    return CrossCheck(findings=findings, unverified=unverified)
