"""The compliance report as a PDF (PRD Step 4; merged into the drawing set in Step 5)."""

from __future__ import annotations

from pathlib import Path
from typing import Any

from reportlab.lib import colors
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.platypus import Paragraph, SimpleDocTemplate, Spacer, Table, TableStyle

STATUS = {"fail": "FAIL", "pass": "pass", "not_checked": "not checked"}


def write_pdf(report: dict[str, Any], path: Path, title: str = "Compliance report") -> Path:
    styles = getSampleStyleSheet()
    small = ParagraphStyle("small", parent=styles["BodyText"], fontSize=8, leading=10)
    banner = ParagraphStyle("banner", parent=styles["BodyText"], fontSize=9, leading=12,
                            backColor=colors.HexColor("#fff4d6"), borderPadding=6, textColor=colors.HexColor("#6b4a00"))
    s = report["summary"]
    story = [
        Paragraph(title, styles["Title"]),
        Paragraph(f"{'Before build (engine geometry)' if report['phase'] == 'before_build' else 'After build (read from Revit)'}"
                  f" &middot; checked {report['checked_utc']} &middot; spec {report['spec_hash'][:19]}", small),
        Spacer(1, 4 * mm),
        Paragraph(f"<b>{s['hard_fail']}</b> hard failures &nbsp; <b>{s['soft_fail']}</b> soft failures &nbsp; "
                  f"{s['passed']} passed &nbsp; {s['not_checked']} not checked", styles["Heading3"]),
        Paragraph(report["advisory"], small),
        Spacer(1, 3 * mm),
    ]
    unverified = [r["name"] for r in report["rule_sets"] if not r["verified"]]
    if unverified:
        story += [Paragraph("<b>Provisional rule values.</b> " + "; ".join(
            f"{r['name']}: {r['source']}" for r in report["rule_sets"] if not r["verified"])
            + " Results marked provisional must not be relied on.", banner), Spacer(1, 4 * mm)]

    rows = [["Result", "Check", "Where", "Measured", "Required", "Clause"]]
    for f in report["findings"]:
        result = STATUS[f["status"]] + (" (soft)" if f["severity"] == "soft" and f["status"] == "fail" else "")
        if not f["verified"]:
            result += "\nprovisional"
        rows.append([result, Paragraph(f["title"], small), Paragraph(f["subject"], small), Paragraph(f["measured"], small),
                     Paragraph(f["required"], small), Paragraph(f"{f['rule_set']}: {f['clause']}", small)])
    table = Table(rows, repeatRows=1, colWidths=[24 * mm, 55 * mm, 55 * mm, 45 * mm, 45 * mm, 50 * mm])
    style = [("FONT", (0, 0), (-1, 0), "Helvetica-Bold", 8), ("FONT", (0, 1), (0, -1), "Helvetica", 8),
             ("VALIGN", (0, 0), (-1, -1), "TOP"), ("LINEBELOW", (0, 0), (-1, 0), 0.6, colors.grey),
             ("ROWBACKGROUNDS", (0, 1), (-1, -1), [colors.white, colors.HexColor("#f5f7fa")])]
    for i, f in enumerate(report["findings"], 1):
        if f["status"] == "fail":
            style.append(("TEXTCOLOR", (0, i), (0, i), colors.HexColor("#b42318") if f["severity"] == "hard" else colors.HexColor("#8a5a00")))
    table.setStyle(TableStyle(style))
    story.append(table)
    path.parent.mkdir(parents=True, exist_ok=True)
    SimpleDocTemplate(str(path), pagesize=landscape(A4), leftMargin=12 * mm, rightMargin=12 * mm,
                      topMargin=12 * mm, bottomMargin=12 * mm, title=title).build(story)
    return path
