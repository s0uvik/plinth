"""Quantities + measurement rules -> an estimate: rows in the office layout, each with its Excel formulas and the same
values computed in Python, plus the questions raised on the way.

Inconsistent or incomplete input is never resolved here: a mark with two sizes, a per-floor count that differs between
floors, an opening that no rule measures, all become questions, and the affected cells stay blank.
"""

from __future__ import annotations

import re
import string
from dataclasses import dataclass, field
from typing import Any, Iterable, Literal

from pydantic import BaseModel, Field

from b2m.estimate.quantities import Opening, Quantities, Railing
from b2m.estimate.ruleset import (ExpressionError, GroupSpec, MarkHeading, Number, Rules, RowSpec, SectionSpec,
                                TotalSpec, evaluate)

COLUMNS = "ABCDEFGHI"
DIM_COLUMNS = (("length", "D"), ("width", "E"), ("height", "F"))
DIMENSION_UNIT = {1: "length", 2: "area", 3: "volume"}
RowRole = Literal["section", "group", "mark", "item", "total", "blank"]


class Question(BaseModel):
    id: str
    topic: str
    text: str
    evidence: list[str] = Field(default_factory=list)


class EstimateRow(BaseModel):
    row: int
    role: RowRole
    sl: int | None = None
    item: str | None = None
    number: int | float | None = None
    number_formula: str | None = None
    length: int | float | None = None
    width: int | float | None = None
    height: int | float | None = None
    quantity: float | None = None
    quantity_formula: str | None = None
    total_label: str | None = None
    units: str | None = None
    remarks: str | None = None
    path: list[str] = Field(default_factory=list, description="Section / group / mark this row belongs to")
    dimensions: int = Field(default=0, exclude=True)
    spec_id: str | None = Field(default=None, exclude=True)

    def cells(self) -> dict[str, Any]:
        """What the writer puts in each column: formulas as text, constants as values."""
        out: dict[str, Any] = {
            "A": self.sl, "B": self.item,
            "C": self.number_formula if self.number_formula else self.number,
            "D": self.length, "E": self.width,
            "F": self.total_label if self.total_label is not None else self.height,
            "G": self.quantity_formula, "H": self.units, "I": self.remarks,
        }
        return {c: v for c, v in out.items() if v is not None}

    def values(self) -> dict[str, Any]:
        """The value of each cell as Excel would show it, computed in Python."""
        out = self.cells()
        if self.number is not None:
            out["C"] = self.number
        if self.quantity_formula is not None:
            if self.quantity is None:
                out.pop("G")
            else:
                out["G"] = self.quantity
        return out


class Estimate(BaseModel):
    title: str
    rules: str
    project: str
    rows: list[EstimateRow]
    questions: list[Question] = Field(default_factory=list)

    def cells(self) -> dict[str, Any]:
        return {f"{c}{r.row}": v for r in self.rows for c, v in r.cells().items()}

    def values(self) -> dict[str, Any]:
        return {f"{c}{r.row}": v for r in self.rows for c, v in r.values().items()}

    def row(self, n: int) -> EstimateRow | None:
        return next((r for r in self.rows if r.row == n), None)


# ---- helpers --------------------------------------------------------------------------------------------------------

def roman(i: int) -> str:
    vals = ((1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"),
            (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i"))
    out = ""
    for v, s in vals:
        while i >= v:
            out, i = out + s, i - v
    return out


def ordinal(i: int) -> str:
    suffix = "th" if 10 <= i % 100 <= 20 else {1: "st", 2: "nd", 3: "rd"}.get(i % 10, "th")
    return f"{i}{suffix}"


def num_text(x: float) -> str:
    """A number as written in a formula or label: 1000 not 1000.0."""
    x = clean(x)
    return str(int(x)) if float(x).is_integer() else repr(x)


def clean(x: float) -> float:
    """Drop binary noise (5.1000000000000005 -> 5.1); dimensions are never finer than a micrometre."""
    r = round(float(x), 6)
    return int(r) if r.is_integer() else r


def natural_key(s: str) -> list:
    return [int(t) if t.isdigit() else t.casefold() for t in re.split(r"(\d+)", s)]


@dataclass
class _Item:
    """One mark (or railing subkind and length) within a group, with its per-storey counts."""

    key: tuple
    label_name: str
    sources: list[Opening | Railing]
    by_storey: dict[int | None, int] = field(default_factory=dict)

    @property
    def total(self) -> int:
        return sum(self.by_storey.values())


class _Fmt(string.Formatter):
    def get_value(self, key, args, kwargs):
        if key not in kwargs:
            raise KeyError(key)
        return kwargs[key]


# ---- engine ---------------------------------------------------------------------------------------------------------

class _Builder:
    def __init__(self, q: Quantities, rules: Rules) -> None:
        self.q, self.rules = q, rules
        self.rows: list[EstimateRow] = []
        self.questions: list[Question] = []
        self.next_row = rules.layout.header_row + 1
        self.used_openings: set[int] = set()
        self.used_railings: set[int] = set()
        typical = q.project.storey_indexes("typical")
        self.base_fields: dict[str, Any] = {"project": q.project.name}
        if typical:
            self.base_fields.update(typical_from=typical[0], typical_to=typical[-1],
                                    typical_from_ord=ordinal(typical[0]), typical_to_ord=ordinal(typical[-1]))

    # -- questions
    def ask(self, topic: str, text: str, evidence: Iterable[str] = ()) -> str:
        qid = f"Q{len(self.questions) + 1}"
        self.questions.append(Question(id=qid, topic=topic, text=text, evidence=list(evidence)))
        return qid

    def label(self, template: str, fields: dict[str, Any], where: str) -> str:
        try:
            return _Fmt().format(template, **fields)
        except KeyError as e:
            self.ask("rules", f"{where}: the label {template!r} needs {{{e.args[0]}}}, which this project does not "
                              f"provide.", [f"available: {sorted(fields)}"])
            return template

    # -- rows
    def new_row(self, role: RowRole, path: list[str], **kw: Any) -> EstimateRow:
        row = EstimateRow(row=self.next_row, role=role, path=list(path), **kw)
        self.rows.append(row)
        self.next_row += 1
        return row

    # -- input checks
    def check_inputs(self) -> None:
        storeys = {s.index for s in self.q.project.storeys}
        for i, o in enumerate(self.q.openings):
            if o.storey not in storeys:
                self.ask("input", f"Opening {o.mark} is on storey {o.storey}, which the project does not list.",
                         [_src(o)])
        for r in self.q.railings:
            if r.storey is not None and r.storey not in storeys:
                self.ask("input", f"Railing {r.kind} is on storey {r.storey}, which the project does not list.",
                         [_src(r)])
        sizes: dict[str, set[tuple[float, float]]] = {}
        spelled: dict[str, set[str]] = {}
        for o in self.q.openings:
            sizes.setdefault(o.mark, set()).add((o.width_mm, o.height_mm))
            spelled.setdefault(o.mark.casefold(), set()).add(o.mark)
        for mark, s in sizes.items():
            if len(s) > 1:
                txt = ", ".join(f"{num_text(w)} x {num_text(h)}" for w, h in sorted(s))
                self.ask("input", f"Mark {mark} has more than one size ({txt}). Which is right, or are these "
                                  f"different types that need their own marks?",
                         [_src(o) for o in self.q.openings if o.mark == mark])
        for variants in spelled.values():
            if len(variants) > 1:
                self.ask("input", f"Marks {sorted(variants)} differ only in letter case. Are they the same type?")
        seen: dict[int, int] = {}
        for i, s in enumerate(self.rules.sections):
            if s.sl is not None and s.sl in seen:
                self.ask("rules", f"SL. NO {s.sl} is used by two sections ({_sec_name(self.rules.sections[seen[s.sl]])}"
                                  f" and {_sec_name(s)}). Is that intended?")
            elif s.sl is not None:
                seen[s.sl] = i

    def check_coverage(self) -> None:
        for i, o in enumerate(self.q.openings):
            if i not in self.used_openings:
                self.ask("coverage", f"No rule measures {o.count} x {o.kind} {o.mark} ({num_text(o.width_mm)} x "
                                     f"{num_text(o.height_mm)}, storey {o.storey}, material {o.material or 'not known'}"
                                     f"{', zone ' + o.zone if o.zone else ''}). How should it be estimated?", [_src(o)])
        for i, r in enumerate(self.q.railings):
            if i not in self.used_railings:
                self.ask("coverage", f"No rule measures {r.count} x {r.kind} railing {r.subkind or ''} "
                                     f"({num_text(r.length_mm)} mm). How should it be estimated?", [_src(r)])

    # -- selection
    def select(self, g: GroupSpec) -> tuple[list[_Item], list[int]]:
        sel = g.select
        storey_set = self.q.project.storey_indexes(sel.storeys)
        items: dict[tuple, _Item] = {}
        if sel.kind is not None:
            for i, o in enumerate(self.q.openings):
                if o.kind != sel.kind or (sel.marks and o.mark not in sel.marks):
                    continue
                if sel.material is not None and (o.material or "").casefold() != sel.material.casefold():
                    continue
                if not _zone_ok(sel.zone, o.zone) or (sel.storeys != "any" and o.storey not in storey_set):
                    continue
                self.used_openings.add(i)
                key = (o.mark, o.width_mm, o.height_mm)
                it = items.setdefault(key, _Item(key, o.mark, []))
                it.sources.append(o)
                it.by_storey[o.storey] = it.by_storey.get(o.storey, 0) + o.count
        else:
            for i, r in enumerate(self.q.railings):
                if r.kind.casefold() != sel.railing.casefold():
                    continue
                if sel.storeys != "any" and r.storey not in storey_set:
                    continue
                self.used_railings.add(i)
                key = (r.subkind or "", r.length_mm)
                it = items.setdefault(key, _Item(key, r.subkind or "", []))
                it.sources.append(r)
                it.by_storey[r.storey] = it.by_storey.get(r.storey, 0) + r.count
        rank = {m.casefold(): i for i, m in enumerate(self.rules.order)}
        ordered = sorted(items.values(), key=lambda it: (rank.get(it.label_name.casefold(), len(rank)),
                                                         natural_key(it.label_name), it.key[1:]))
        return ordered, storey_set

    # -- sections
    def build(self) -> None:
        self.check_inputs()
        for sec in self.rules.sections:
            self.section(sec)
        self.check_coverage()

    def section(self, sec: SectionSpec) -> None:
        chosen = [(g, *self.select(g)) for g in sec.groups]
        chosen = [(g, items, st) for g, items, st in chosen if items]
        if not chosen:
            return
        marks = []
        for _, items, _ in chosen:
            for it in items:
                if isinstance(it.sources[0], Opening) and it.label_name not in marks:
                    marks.append(it.label_name)
        fields = {**self.base_fields, "marks": ", ".join(marks)}
        start = len(self.rows)
        heading_row: EstimateRow | None = None
        title = self.label(sec.heading, fields, "section heading") if sec.heading else None
        if title is not None and sec.heading_on_previous_row and self.rows and self.rows[-1].role != "blank" \
                and self.rows[-1].item is None:
            heading_row = self.rows[-1]
            heading_row.item = title
            if sec.sl is not None:
                heading_row.sl = sec.sl
            start = len(self.rows) - 1
        elif title is not None:
            if sec.heading_on_previous_row:
                self.ask("rules", f"Section {title!r} should share the previous row, but that row has no free ITEM "
                                  f"NAME cell; it got its own row.")
            heading_row = self.new_row("section", [title], sl=sec.sl, item=title)
        path = [title] if title else []

        measured: list[EstimateRow] = []
        per_group: list[list[EstimateRow]] = []
        storeys_seen: set[int] = set()
        for g, items, storey_set in chosen:
            rows = self.group(g, items, storey_set, path, fields, g.units_on or sec.units_on)
            per_group.append(rows)
            measured += rows
            if g.count == "per_storey":
                storeys_seen.add(len(storey_set))
        if sec.sl is not None and self.rows[start].sl is None:
            self.rows[start].sl = sec.sl
        if sec.units_on == "heading" and any(g.units_on is None for g, _, _ in chosen):
            if heading_row is None:
                self.ask("rules", f"{_sec_name(sec)}: units_on is heading, but the section has no heading row.")
            else:
                heading_row.units = self.unit_of([r for (g, _, _), rs in zip(chosen, per_group)
                                                  if g.units_on is None for r in rs], title or "section")
        sec_storeys = next(iter(storeys_seen)) if len(storeys_seen) == 1 else None
        if len(storeys_seen) > 1 and any(t.of for t in sec.totals):
            self.ask("rules", f"Section {title!r}: its groups cover different numbers of storeys, so 'storeys' in its "
                              f"totals is undefined.")
        self.totals(sec.totals, measured, path, sec_storeys, title or "section")
        for _ in range(sec.space_after):
            self.new_row("blank", path)

    def group(self, g: GroupSpec, items: list[_Item], storey_set: list[int], path: list[str], fields: dict,
              units_on: str) -> list[EstimateRow]:
        gpath = list(path)
        group_row: EstimateRow | None = None
        if g.heading:
            heading = self.label(g.heading, fields, "group heading")
            group_row = self.new_row("group", path, item=heading)
            gpath.append(heading)
        measured: list[EstimateRow] = []
        for idx, it in enumerate(items, start=1):
            names, ifields, n = self.item_names(g, it, storey_set, gpath)
            ifields.update(fields, roman=roman(idx))
            mpath = gpath
            if g.mark_heading:
                mh = self.mark_heading(g.mark_heading, names, ifields, gpath)
                mpath = gpath + [mh.item or ""]
            for spec in g.rows:
                measured.append(self.measured_row(spec, names, ifields, mpath, n is None))
        if units_on == "rows":
            for r in measured:
                r.units = self.unit_of([r], r.item or "")
        elif units_on == "heading" and g.units_on == "heading":  # the group's own heading row
            if group_row is None:
                self.ask("rules", f"{' / '.join(gpath) or 'group'}: units_on is heading, but the group has no heading.")
            else:
                group_row.units = self.unit_of(measured, group_row.item or "")
        self.totals(g.totals, measured, gpath, len(storey_set) if g.count == "per_storey" else None,
                    g.heading or (path[0] if path else "group"))
        return measured

    def item_names(self, g: GroupSpec, it: _Item, storey_set: list[int], path: list[str]):
        first = it.sources[0]
        if g.count == "per_storey":
            counts = {s: it.by_storey.get(s, 0) for s in storey_set}
            if len(set(counts.values())) == 1:
                n: int | None = next(iter(counts.values()))
            else:
                n = None
                where = " / ".join(p for p in path if p)
                per = ", ".join(f"storey {s}: {c}" for s, c in counts.items())
                self.ask("input", f"{where}: {it.label_name} is counted per floor, but the floors differ ({per}). "
                                  f"Which count is right?", [_src(s) for s in it.sources])
        else:
            n = it.total
        names: dict[str, float] = dict(self.rules.constants)
        if n is not None:
            names["n"] = n
        if g.count == "per_storey":
            names["storeys"] = len(storey_set)
        fields: dict[str, Any] = {"n": n if n is not None else "?", "storeys": len(storey_set)}
        if isinstance(first, Opening):
            names.update(W=first.width_mm / 1000, H=first.height_mm / 1000)
            fields.update(mark=first.mark, width_mm=num_text(first.width_mm), height_mm=num_text(first.height_mm))
        else:
            names["L"] = first.length_mm / 1000
            sub = first.subkind or ""
            fields.update(mark=sub, subkind=sub, Subkind=sub[:1].upper() + sub[1:], length_mm=num_text(first.length_mm))
        return names, fields, n

    def number(self, spec: Number, names: dict[str, float], where: str) -> tuple[float | None, str | None]:
        factors = spec if isinstance(spec, list) else [spec]
        try:
            vals = [clean(evaluate(f, names)) for f in factors]
        except ExpressionError as e:
            if "'n'" not in str(e):  # n missing = already asked (counts differ between floors)
                self.ask("rules", f"{where}: {e}")
            return None, None
        value = 1
        for v in vals:
            value = value * v
        formula = "=" + "*".join(num_text(v) for v in vals) if len(vals) > 1 else None
        return clean(value), formula

    def mark_heading(self, mh: MarkHeading, names: dict, fields: dict, path: list[str]) -> EstimateRow:
        label = self.label(mh.label, fields, "mark heading")
        number, formula = self.number(mh.number, names, label) if mh.number is not None else (None, None)
        remark = self.label(mh.remark, fields, "remark") if mh.remark else None
        return self.new_row("mark", path, item=label, number=number, number_formula=formula, remarks=remark)

    def measured_row(self, spec: RowSpec, names: dict, fields: dict, path: list[str], unknown: bool) -> EstimateRow:
        label = self.label(spec.label, fields, "row label")
        number, nformula = self.number(spec.number, names, label)
        row = self.new_row("item", path, item=label, number=number, number_formula=nformula, spec_id=spec.id,
                           remarks=self.label(spec.remark, fields, "remark") if spec.remark else None)
        present: list[tuple[str, float]] = []
        for attr, col in DIM_COLUMNS:
            expr = getattr(spec, attr)
            if expr is None:
                continue
            try:
                v = clean(evaluate(expr, names))
            except ExpressionError as e:
                self.ask("rules", f"{label}: {e}")
                continue
            setattr(row, attr, v)
            present.append((col, v))
        row.dimensions = len(present)
        if unknown:
            row.remarks = "count differs between floors - see questions"
        if not present or number is None:
            return row
        lay = self.rules.layout
        if len(present) == 1:
            row.quantity_formula = lay.quantity_formula_one_dimension.format(r=row.row, col=present[0][0])
        else:
            row.quantity_formula = lay.quantity_formula.format(r=row.row)
        q = number
        for _, v in present:
            q = q * v
        row.quantity = q
        return row

    def unit_of(self, rows: list[EstimateRow], where: str) -> str | None:
        dims = {r.dimensions for r in rows if r.dimensions}
        if len(dims) != 1:
            if dims:
                self.ask("rules", f"{where}: rows measured in different units are added together "
                                  f"({sorted(DIMENSION_UNIT.get(d, str(d)) for d in dims)}).")
            return None
        return self.rules.units.get(DIMENSION_UNIT.get(dims.pop(), ""))

    def totals(self, specs: list[TotalSpec], measured: list[EstimateRow], path: list[str], storeys: int | None,
               where: str) -> None:
        prev: EstimateRow | None = None
        prev_rows: list[EstimateRow] = []
        skipped = False
        for t in specs:
            if t.of == "previous":
                if prev is None and skipped:
                    continue  # nothing to multiply; the reason was already asked
                if prev is None:
                    self.ask("rules", f"{where}: total {t.label!r} multiplies a previous total, but there is none.")
                    continue
                names = dict(self.rules.constants)
                if storeys is not None:
                    names["storeys"] = storeys
                try:
                    times = clean(evaluate(t.times, names))
                except ExpressionError as e:
                    self.ask("rules", f"{where}: total {t.label!r}: {e}")
                    continue
                formula = f"=G{prev.row}*{num_text(times)}"
                value = prev.quantity * times if prev.quantity is not None else None
                rows = prev_rows
            else:
                rows = [r for r in measured if r.quantity_formula and (t.sum is None or r.spec_id == t.sum)]
                if not rows:
                    skipped, prev = True, None
                    continue
                formula = self.sum_formula([r.row for r in rows])
                value = None if any(r.quantity is None for r in rows) else _sum(r.quantity for r in rows)
            row = self.new_row("total", path, total_label=t.label, quantity_formula=formula, quantity=value)
            row.dimensions = next(iter({r.dimensions for r in rows}), 0) if len({r.dimensions for r in rows}) == 1 else 0
            if t.unit:
                row.units = self.unit_of(rows, f"{where}: {t.label}")
            prev, prev_rows = row, rows

    def sum_formula(self, numbers: list[int]) -> str:
        numbers = sorted(numbers)
        g_rows = [r.row for r in self.rows if r.role == "item" and r.quantity_formula]
        between = [n for n in g_rows if numbers[0] <= n <= numbers[-1]]
        if between == numbers:
            return f"=SUM(G{numbers[0]}:G{numbers[-1]})"
        return "=" + "+".join(f"G{n}" for n in numbers)


def _sum(values: Iterable[float]) -> float:
    total = 0
    for v in values:
        total += v
    return total


def _zone_ok(want: str, zone: str | None) -> bool:
    if want == "any":
        return True
    if want == "none":
        return zone is None
    return zone is not None and zone.casefold() == want.casefold()


def _src(x: Opening | Railing) -> str:
    if isinstance(x, Opening):
        desc = f"{x.count} x {x.mark} {num_text(x.width_mm)}x{num_text(x.height_mm)} storey {x.storey}"
        desc += f" {x.zone}" if x.zone else ""
        desc += f" {x.material}" if x.material else ""
    else:
        desc = f"{x.count} x {x.kind} railing {x.subkind or ''} {num_text(x.length_mm)} mm"
        desc += f" storey {x.storey}" if x.storey is not None else ""
    return desc + (f" ({x.source})" if x.source else "")


def _sec_name(s: SectionSpec) -> str:
    return repr(s.heading or s.groups[0].heading or s.groups[0].rows[0].label)


def build_estimate(quantities: Quantities, rules: Rules) -> Estimate:
    """The estimate for these quantities under these rules (see ``b2m/estimate/rules/office-default.yaml``)."""
    b = _Builder(quantities, rules)
    b.build()
    return Estimate(title=rules.title, rules=rules.name, project=quantities.project.name, rows=b.rows,
                    questions=b.questions)
