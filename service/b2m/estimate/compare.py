"""Generated estimate workbook vs a reference workbook, cell by cell.

Rows are aligned by their labels (ITEM NAME, and the total label in HEIGHT), so an inserted or missing row shows up
as one difference instead of shifting everything after it; formulas are compared after mapping the generated row
numbers onto the aligned reference rows. Values are compared too: the engine's Python values against the reference's
cached values (what Excel last calculated), so a typed constant that disagrees with its own row is caught.

    report = compare_workbooks("generated.xlsx", "reference.xlsx", estimate)
    print(report.text())
"""

from __future__ import annotations

import difflib
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Literal

import openpyxl

from b2m.estimate.engine import COLUMNS, Estimate

Kind = Literal["text", "number", "blank", "formula", "formula_vs_constant", "value", "missing_row", "extra_row",
               "layout"]
REF = re.compile(r"(?<![A-Z$])(\$?)([A-Z]{1,3})(\$?)(\d+)(?![\d(])")
TOL = 1e-9


@dataclass
class Difference:
    kind: Kind
    cell: str
    column: str
    context: str
    reference: Any = None
    generated: Any = None
    generated_cell: str | None = None
    cosmetic: bool = False
    note: str = ""

    def line(self) -> str:
        where = f"{self.cell}" + (f" (generated {self.generated_cell})" if self.generated_cell and
                                    self.generated_cell != self.cell else "")
        tag = " [spelling/spacing only]" if self.cosmetic else ""
        body = f"reference {self.reference!r} | generated {self.generated!r}"
        return f"{self.kind:<20} {where:<14} {self.column:<12} {self.context}: {body}{tag}" + \
            (f" - {self.note}" if self.note else "")


@dataclass
class CompareReport:
    reference: str
    generated: str
    rows_compared: int = 0
    cells_compared: int = 0
    values_compared: int = 0
    differences: list[Difference] = field(default_factory=list)
    row_map: dict[int, int] = field(default_factory=dict, repr=False)

    @property
    def exact(self) -> bool:
        return not self.differences

    def counts(self) -> dict[str, int]:
        out: dict[str, int] = {}
        for d in self.differences:
            out[d.kind] = out.get(d.kind, 0) + 1
        return out

    def cells(self) -> set[str]:
        return {d.cell for d in self.differences}

    def text(self) -> str:
        lines = [f"Estimate comparison: {self.generated} vs reference {self.reference}",
                 f"rows aligned {self.rows_compared}, cells compared {self.cells_compared}, "
                 f"values compared {self.values_compared}",
                 "EXACT MATCH" if self.exact else
                 f"{len(self.differences)} difference(s): " + ", ".join(f"{k} {v}" for k, v in self.counts().items())]
        lines += ["  " + d.line() for d in self.differences]
        return "\n".join(lines)

    def questions(self) -> list[str]:
        """One question per reference row that differs: the user decides, nothing is corrected silently."""
        by_row: dict[str, list[Difference]] = {}
        for d in self.differences:
            by_row.setdefault(re.sub(r"^[A-Z]+", "", d.cell) or d.cell, []).append(d)
        out = []
        for row, diffs in by_row.items():
            parts = []
            for d in diffs:
                if d.kind == "missing_row":
                    parts.append(f"the reference has a row the generated estimate lacks ({d.reference!r})")
                elif d.kind == "extra_row":
                    parts.append(f"the generated estimate has a row the reference lacks ({d.generated!r})")
                elif d.kind == "layout":
                    parts.append(f"{d.column}: reference {d.reference!r}, generated {d.generated!r}")
                else:
                    parts.append(f"{d.cell} {d.column}: reference {d.reference!r}, generated {d.generated!r}"
                                 + (f" ({d.note})" if d.note else ""))
            out.append(f"Row {row} - {diffs[0].context}: " + "; ".join(parts) + ". Which is right?")
        return out


def _norm_text(s: Any) -> str:
    return re.sub(r"\s+", "", str(s)).casefold()


def _is_formula(v: Any) -> bool:
    return isinstance(v, str) and v.startswith("=")


def _is_num(v: Any) -> bool:
    return isinstance(v, (int, float)) and not isinstance(v, bool)


def _num_eq(a: float, b: float) -> bool:
    return abs(a - b) <= TOL * max(1.0, abs(a), abs(b))


def _rows(ws, first: int) -> dict[int, dict[str, Any]]:
    out: dict[int, dict[str, Any]] = {}
    for r in range(first, ws.max_row + 1):
        cells = {c: ws[f"{c}{r}"].value for c in COLUMNS if ws[f"{c}{r}"].value not in (None, "")}
        if cells:
            out[r] = cells
    return out


def _signature(cells: dict[str, Any]) -> str:
    b = _norm_text(cells.get("B", ""))
    f = cells.get("F")
    return b + "|" + (_norm_text(f) if isinstance(f, str) and not _is_formula(f) else "")


def _context(rows: dict[int, dict[str, Any]], r: int) -> str:
    label = rows.get(r, {}).get("B") or rows.get(r, {}).get("F") or ""
    section = ""
    for k in sorted((k for k in rows if k <= r), reverse=True):
        if "A" in rows[k] and "B" in rows[k]:
            section = str(rows[k]["B"])
            break
        if "B" in rows[k] and "G" not in rows[k] and "C" not in rows[k] and k != r and not section:
            section = str(rows[k]["B"])
    return f"{section} > {label}" if section and section != label else str(label)


def _remap(formula: str, row_map: dict[int, int]) -> str:
    keys = sorted(row_map)

    def sub(m: re.Match) -> str:
        n = int(m.group(4))
        if n in row_map:
            target = row_map[n]
        else:
            before = [k for k in keys if k < n]
            target = n + (row_map[before[-1]] - before[-1]) if before else n
        return f"{m.group(1)}{m.group(2)}{m.group(3)}{target}"

    return REF.sub(sub, formula)


def _norm_formula(f: str) -> str:
    return re.sub(r"\s+", "", f).upper()


def compare_workbooks(generated: str | Path, reference: str | Path, estimate: Estimate | None = None,
                      header_row: int = 4, sheet: str | None = None) -> CompareReport:
    """Compare two estimate workbooks; ``estimate`` supplies the generated values (openpyxl does not calculate)."""
    g_wb = openpyxl.load_workbook(generated)
    r_wb = openpyxl.load_workbook(reference)
    r_val_wb = openpyxl.load_workbook(reference, data_only=True)
    g_ws = g_wb[sheet] if sheet else g_wb.worksheets[0]
    r_ws = r_wb[sheet] if sheet else r_wb.worksheets[0]
    r_vals = r_val_wb[r_ws.title]
    rep = CompareReport(reference=str(reference), generated=str(generated))
    headers = {c: str(r_ws[f"{c}{header_row}"].value or c) for c in COLUMNS}

    # layout: title, merged cells, header row, column widths
    for c in COLUMNS:
        for r in range(1, header_row + 1):
            gv, rv = g_ws[f"{c}{r}"].value, r_ws[f"{c}{r}"].value
            if gv != rv:
                rep.differences.append(Difference("text", f"{c}{r}", headers[c], "title/header", rv, gv,
                                                  cosmetic=gv is not None and rv is not None and
                                                  _norm_text(gv) == _norm_text(rv)))
    g_merge = {str(m) for m in g_ws.merged_cells.ranges}
    r_merge = {str(m) for m in r_ws.merged_cells.ranges}
    if g_merge != r_merge:
        rep.differences.append(Difference("layout", "merged", "merged cells", "sheet", sorted(r_merge),
                                          sorted(g_merge)))
    for c in COLUMNS:
        gw, rw = g_ws.column_dimensions[c].width, r_ws.column_dimensions[c].width
        if gw is not None and rw is not None and abs(gw - rw) > 0.01:
            rep.differences.append(Difference("layout", f"{c}:{c}", f"width {headers[c]}", "column width", rw, gw))

    # rows, aligned by label
    g_rows, r_rows = _rows(g_ws, header_row + 1), _rows(r_ws, header_row + 1)
    g_keys, r_keys = sorted(g_rows), sorted(r_rows)
    sm = difflib.SequenceMatcher(None, [_signature(g_rows[k]) for k in g_keys],
                                 [_signature(r_rows[k]) for k in r_keys], autojunk=False)
    pairs: list[tuple[int, int]] = []
    for op, i1, i2, j1, j2 in sm.get_opcodes():
        n = min(i2 - i1, j2 - j1) if op == "replace" else (i2 - i1 if op == "equal" else 0)
        pairs += [(g_keys[i1 + k], r_keys[j1 + k]) for k in range(n)]
        for k in range(i1 + n, i2):
            rep.differences.append(Difference("extra_row", f"(gen {g_keys[k]})", "row", _context(g_rows, g_keys[k]),
                                              None, g_rows[g_keys[k]], generated_cell=f"row {g_keys[k]}"))
        for k in range(j1 + n, j2):
            rep.differences.append(Difference("missing_row", f"A{r_keys[k]}", "row", _context(r_rows, r_keys[k]),
                                              r_rows[r_keys[k]], None))
    rep.row_map = dict(pairs)
    rep.rows_compared = len(pairs)
    shifted = [(g, r) for g, r in pairs if g != r]
    if shifted:
        rep.differences.append(Difference("layout", f"A{shifted[0][1]}", "row position", "rows moved",
                                          f"{len(shifted)} rows, first at {shifted[0][1]}",
                                          f"first at {shifted[0][0]}", note="formulas compared after re-mapping"))

    g_values = estimate.values() if estimate else {}
    for g_r, r_r in pairs:
        gc, rc = g_rows[g_r], r_rows[r_r]
        est_row = estimate.row(g_r) if estimate else None
        if est_row is not None:
            label = est_row.item or est_row.total_label or ""
            ctx = " > ".join([p for p in est_row.path if p and p != label] + [label])
        else:
            ctx = _context(r_rows, r_r)
        for c in COLUMNS:
            gv, rv = gc.get(c), rc.get(c)
            cell, gcell = f"{c}{r_r}", f"{c}{g_r}"
            if gv is None and rv is None:
                continue
            rep.cells_compared += 1
            diff: Difference | None = None
            if gv is None or rv is None:
                diff = Difference("blank", cell, headers[c], ctx, rv, gv, gcell,
                                  note="blank in the reference" if rv is None else "blank in the generated estimate")
            elif _is_formula(gv) and _is_formula(rv):
                if _norm_formula(_remap(gv, rep.row_map)) != _norm_formula(rv):
                    diff = Difference("formula", cell, headers[c], ctx, rv, gv, gcell)
            elif _is_formula(gv) or _is_formula(rv):
                diff = Difference("formula_vs_constant", cell, headers[c], ctx, rv, gv, gcell,
                                  note="the reference types a value where the rules give a formula"
                                  if _is_formula(gv) else "the reference has a formula where the rules give a value")
            elif _is_num(gv) and _is_num(rv):
                if not _num_eq(gv, rv):
                    diff = Difference("number", cell, headers[c], ctx, rv, gv, gcell)
            elif gv != rv:
                cosmetic = gv is not None and rv is not None and _norm_text(gv) == _norm_text(rv)
                diff = Difference("text", cell, headers[c], ctx, rv, gv, gcell, cosmetic=cosmetic)
            if diff:
                rep.differences.append(diff)
            # values: our Python values against Excel's cached ones, wherever a formula is involved
            if (_is_formula(gv) or _is_formula(rv)) and gcell in g_values:
                cached = r_vals[cell].value
                ours = g_values[gcell]
                if _is_num(cached) and _is_num(ours):
                    rep.values_compared += 1
                    if not _num_eq(ours, cached):
                        rep.differences.append(Difference("value", cell, headers[c], ctx, cached, ours, gcell,
                                                          note="calculated value"))
    return rep


def main(argv: list[str] | None = None) -> int:
    args = argv if argv is not None else sys.argv[1:]
    if len(args) != 2:
        print("usage: python -m b2m.estimate.compare GENERATED.xlsx REFERENCE.xlsx")
        return 2
    rep = compare_workbooks(Path(args[0]), Path(args[1]))
    print(rep.text())
    return 0 if rep.exact else 1


if __name__ == "__main__":
    raise SystemExit(main())
