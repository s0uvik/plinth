"""Reinforcement notes as written on structural drawings, read into numbers (PRD Step 9, T4).

Handles the usual spellings: "Ø16mm Main Bar @150 (C/C)", "%%c16", "16 dia @ 150 C/C", "16mm dia", "4-16Ø",
"4 nos 16 dia", "4T16", "Ø8 2 legged stirrup", "3-legged", "clear cover 40 mm", "75 mm Clear Cover". A bar written
as "#n" is a bar number, which reads as n mm in some offices and as the US/ASTM bar size (#8 = 25 mm) in others:
it is kept as written (``bar_mark``) and never turned into a diameter here.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

DIA = r"(?:Ø|ø|⌀|φ|Φ|%%c|%%C)"
NUM = r"(\d+(?:\.\d+)?)"
_DIA_BEFORE = re.compile(DIA + r"\s*" + NUM, re.I)  # Ø16
_DIA_WORD_BEFORE = re.compile(r"\b(?:DIA\.?|DIAMETER)\s*(?:OF\s*)?" + NUM, re.I)  # dia 16
_DIA_AFTER = re.compile(NUM + r"\s*(?:MM)?\s*(?:" + DIA + r"|\bDIA\b\.?|\bDIAMETER\b|\bφ)", re.I)  # 16 dia, 16mm Ø
_COUNT_DIA = re.compile(r"\b(\d+)\s*(?:NOS?\.?|NUMBERS?)?\s*(?:-|\s)\s*(?:" + DIA + r"|#)?\s*(\d+(?:\.\d+)?)\s*(?:MM)?\s*"
                        r"(?:" + DIA + r"|\bDIA\b|\bBARS?\b|\bTOR\b|\bTMT\b)", re.I)  # 4-16Ø, 4 nos 16 dia
_COUNT_NOS = re.compile(r"\b(\d+)\s*NOS?\.?\s*(?:OF\s*)?(?:" + DIA + r"|DIA\.?|#)?\s*(\d+(?:\.\d+)?)", re.I)  # 4 nos Ø16
_COUNT_GRADE = re.compile(r"\b(\d+)\s*[TY]\s*(\d+)\b", re.I)  # 4T16, 4Y16
_BAR_MARK = re.compile(r"#\s*(\d+)")
_DIA_GRADE = re.compile(r"\b[TY]\s*(\d{1,2})\b", re.I)  # T10, Y12 (high-yield bar of that diameter)
_SPACING = re.compile(r"@\s*" + NUM + r"\s*(?:MM)?", re.I)
_SPACING_WORDS = re.compile(NUM + r"\s*(?:MM)?\s*(?:C\s*/\s*C|C\s*-\s*C|CC|CENTRES?|CENTERS?)\b", re.I)
_LEGS = re.compile(r"\b(\d+)\s*(?:-\s*)?(?:LEG(?:GED|GEG|ED|S)?|L)\b", re.I)
_COVER = [re.compile(NUM + r"\s*(?:MM)?\s*(?:THK\.?\s*)?(?:CLEAR\s*)?COVER\b", re.I),
          re.compile(r"\b(?:CLEAR\s*)?COVER\s*(?:OF|=|:|-)?\s*" + NUM + r"\s*(?:MM)?", re.I)]
_ROLES = [("stirrup", re.compile(r"\b(?:STIRRUPS?|TIES?|LINKS?|RINGS?)\b", re.I)),
          ("distribution", re.compile(r"\bDIST(?:RIBUTION|\.|RI\.?)?\b", re.I)),
          ("main", re.compile(r"\bMAIN\b", re.I)),
          ("extra", re.compile(r"\bEXTRA\b", re.I))]


@dataclass
class RebarText:
    text: str
    bar_dia_mm: float | None = None
    bar_mark: str | None = None  # "#8": a bar number whose size depends on the office's convention
    spacing_mm: float | None = None
    count: int | None = None
    legs: int | None = None
    cover_mm: float | None = None
    role: str | None = None

    @property
    def is_rebar(self) -> bool:
        return any(v is not None for v in (self.bar_dia_mm, self.bar_mark, self.spacing_mm, self.count, self.legs,
                                           self.cover_mm))


def parse(text: str) -> RebarText:
    t = " ".join(text.replace("\\P", " ").split())
    out = RebarText(text=t)
    cover = None
    for pattern in _COVER:
        m = pattern.search(t)
        if m:
            cover = float(m.group(1))
            t_wo_cover = t[:m.start()] + " " + t[m.end():]
            break
    else:
        t_wo_cover = t
    out.cover_mm = cover
    rest = t_wo_cover
    for pattern in (_COUNT_GRADE, _COUNT_NOS, _COUNT_DIA):
        m = pattern.search(rest)
        if m:
            out.count, out.bar_dia_mm = int(m.group(1)), float(m.group(2))
            rest = rest[:m.start()] + " " + rest[m.end():]
            break
    m = _BAR_MARK.search(rest)
    if m and out.bar_dia_mm is None:
        out.bar_mark = "#" + m.group(1)
        rest = rest[:m.start()] + " " + rest[m.end():]
    if out.bar_dia_mm is None and out.bar_mark is None:
        for pattern in (_DIA_BEFORE, _DIA_AFTER, _DIA_WORD_BEFORE, _DIA_GRADE):
            m = pattern.search(rest)
            if m:
                out.bar_dia_mm = float(m.group(1))
                rest = rest[:m.start()] + " " + rest[m.end():]
                break
    m = _SPACING.search(rest) or _SPACING_WORDS.search(rest)
    if m:
        out.spacing_mm = float(m.group(1))
        rest = rest[:m.start()] + " " + rest[m.end():]
    m = _LEGS.search(rest)
    if m:
        out.legs = int(m.group(1))
    for role, pattern in _ROLES:
        if pattern.search(t):
            out.role = role
            break
    if out.role is None and out.cover_mm is not None and out.bar_dia_mm is None and out.bar_mark is None:
        out.role = "cover"
    if out.legs is not None and out.role is None:
        out.role = "stirrup"
    return out
