"""The estimate as an Excel workbook in the office layout: merged title, header row, live formulas, SUM totals."""

from __future__ import annotations

from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, Side
from openpyxl.utils import get_column_letter

from b2m.estimate.engine import COLUMNS, Estimate
from b2m.estimate.ruleset import Rules

THIN = Side(style="thin")
BOX = Border(left=THIN, right=THIN, top=THIN, bottom=THIN)


def write_xlsx(estimate: Estimate, rules: Rules, path: str | Path, sheet: str = "Sheet1") -> Path:
    lay = rules.layout
    wb = Workbook()
    ws = wb.active
    ws.title = sheet
    plain = Font(name=lay.font, size=lay.font_size)
    bold = Font(name=lay.font, size=lay.font_size, bold=True)

    first = ws[lay.title_merge.split(":")[0]]
    first.value = estimate.title
    first.font = Font(name=lay.font, size=lay.title_font_size, bold=True)
    first.alignment = Alignment(horizontal="center", vertical="center")
    ws.merge_cells(lay.title_merge)

    h = lay.header_row
    for i, text in enumerate(lay.headers):
        c = ws.cell(row=h, column=i + 1, value=text)
        c.font = bold
        c.alignment = Alignment(horizontal="center", vertical="center")
        c.border = BOX
    if lay.header_row_height:
        ws.row_dimensions[h].height = lay.header_row_height

    last = h
    for r in estimate.rows:
        last = max(last, r.row)
        cells = r.cells()
        for col in COLUMNS:
            c = ws[f"{col}{r.row}"]
            c.border = BOX
            c.font = plain
            if col in cells:
                c.value = cells[col]
                c.number_format = lay.number_format
        if r.role in ("section", "group", "mark") or (r.role == "total"):
            for col in ("B", "F", "G"):
                if col in cells:
                    ws[f"{col}{r.row}"].font = bold
        if "H" in cells:
            ws[f"H{r.row}"].alignment = Alignment(horizontal="center", vertical="center")

    for col, width in lay.column_widths.items():
        ws.column_dimensions[col].width = width
    for i in range(len(COLUMNS)):
        letter = get_column_letter(i + 1)
        if letter not in lay.column_widths:
            ws.column_dimensions[letter].width = 12

    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    wb.save(path)
    return path
