"""Text on a plan: what kind each text is (opening mark, room name, room size, shaft label, ...) and the helpers
for nearness. Patterns are drafting conventions (a mark is a short letter code with an optional number, a size is
"N X M"), never words or values taken from one drawing.
"""

from __future__ import annotations

import math
import re

from plinth.cad.recognise.context import Ctx, Text
from plinth.cad.recognise.geometry import Point, dot, frame, sub

MARK_RE = re.compile(r"^([A-Za-z]{1,3})\s*-?\s*(\d{0,3})$")
SIZE_RE = re.compile(r"^\s*(\d+(?:\.\d+)?)\s*[xX×*]\s*(\d+(?:\.\d+)?)\s*$")
NUMBER_RE = re.compile(r"^[\s\d.,'\"+\-/()%]+$")
LIFT_RE = re.compile(r"\b(?:LIFTS?|ELEVATORS?)\b", re.I)
DUCT_RE = re.compile(r"\b(?:DUCTS?|SHAFTS?|SHAFT|VOID|OTS|CUT\s*OUT)\b", re.I)
STAIR_RE = re.compile(r"\b(?:STAIRS?|STAIRCASE|STAIR\s*CASE)\b", re.I)
SLOPE_RE = re.compile(r"\b(?:SLOPES?|FALLS?|GRADIENT)\b", re.I)
# drafting words for spaces with no roof over them (a roof plan's parapets enclose these)
OPEN_TO_SKY_RE = re.compile(r"\b(?:TERRACES?|ROOF|ROOF\s*TOP|OPEN\s+TO\s+SKY|COURT(?:YARD)?)\b", re.I)
UP_RE = re.compile(r"^\s*(?:UP|DN|DOWN)\s*$", re.I)
# drafting prefixes of opening marks: door, window, ventilator (D, W, V; also DW, FD, SD, KW, ... as written)
KIND_BY_PREFIX = {"D": "door", "W": "window", "V": "ventilator"}


def normalise_mark(text: str) -> str | None:
    m = MARK_RE.match(text.strip())
    if not m:
        return None
    return m.group(1).upper() + m.group(2)


def mark_kind(mark: str) -> str | None:
    """door/window/ventilator from the mark's first letter, None when the prefix says nothing."""
    m = MARK_RE.match(mark)
    if not m:
        return None
    prefix = m.group(1).upper()
    if prefix in KIND_BY_PREFIX:
        return KIND_BY_PREFIX[prefix]
    return KIND_BY_PREFIX.get(prefix[-1]) if len(prefix) > 1 else None


def parse_size(text: str) -> tuple[float, float] | None:
    m = SIZE_RE.match(text)
    return (float(m.group(1)), float(m.group(2))) if m else None


def is_name(text: str) -> bool:
    """Words that can name a space: at least one word of 2+ letters, not a mark, size or number."""
    t = text.strip()
    if not t or NUMBER_RE.match(t) or SIZE_RE.match(t) or MARK_RE.match(t):
        return False
    return bool(re.search(r"[A-Za-z]{2,}", t))


def local(t: Text, p: Point) -> Point:
    """p in the text's own frame (x along the baseline, y up the letters), origin at the text centre."""
    u, n = frame(t.rotation)
    d = sub(p, t.center)
    return (dot(d, u), dot(d, n))


def box_distance(t: Text, p: Point) -> float:
    """Distance from p to the text's estimated box (0 inside)."""
    x, y = local(t, p)
    w = t.width
    dx = max(abs(x) - w / 2, 0.0)
    dy = max(abs(y) - t.height / 2, 0.0)
    return math.hypot(dx, dy)


def stacked(a: Text, b: Text, max_gap_heights: float = 1.6) -> float | None:
    """When b sits directly above or below a (same rotation, overlapping across, line gap within
    max_gap_heights text heights), the signed vertical offset of b from a in a's frame (positive = above);
    else None."""
    if abs(((a.rotation - b.rotation + 180) % 360) - 180) > 2:
        return None
    x, y = local(a, b.center)
    wa = a.width
    wb = b.width
    if abs(x) > (wa + wb) / 2:  # no overlap across
        return None
    h = max(a.height, b.height)
    if abs(y) > h * (1 + max_gap_heights) or abs(y) < h * 0.5:
        return None
    return y


def free_texts(ctx: Ctx) -> list[Text]:
    return [t for t in ctx.texts if not ctx.claimed(t.handle)]
