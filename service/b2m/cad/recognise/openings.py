"""Openings in walls: doors (swing symbols), windows (infill lines between jambs), plain openings (gaps with
nothing drawn), lift landing doors (sliding-door lines in a lift shaft wall), and the marks written beside them.

A door's span runs from its hinge to the closed end of its swing (or hinge to hinge for a double door); it belongs
to the wall whose body holds that span. A door in line with a wall but beyond its end extends the wall through
the doorway to the next wall (a doorway at a T or L junction). A door in no wall is kept with wall_id None and a
question.

Marks: every short code text ("D", "D1", "w2") is matched to the nearest opening of a compatible kind (D -> door,
W -> window, V -> ventilator; other prefixes any kind), within reach of the opening (a few text heights plus half
the opening width), each text to at most one opening. A text that is about as near to a second opening is
ambiguous and asked about, as is a mark text that matches no opening and an opening that has no mark.
"""

from __future__ import annotations

import math
from collections import Counter, defaultdict
from dataclasses import dataclass, field

from b2m.cad.building import Opening
from b2m.cad.recognise.context import Ctx, Text
from b2m.cad.recognise.geometry import Point, Seg, angle_diff, axis_angle, dot, from_frame, point_seg_distance
from b2m.cad.recognise.symbols import DoorSymbol
from b2m.cad.recognise.text import box_distance, mark_kind, normalise_mark
from b2m.cad.recognise.walls import Gap, WallLine, _clean_junctions


@dataclass
class OpeningDraft:
    kind: str
    wall: WallLine | None
    t0: float  # along the wall (or along the span when there is no wall)
    t1: float
    center: Point
    width: float
    handles: list[str]
    door: DoorSymbol | None = None
    gap: Gap | None = None
    span: tuple[Point, Point] | None = None
    mark_text: Text | None = None
    note: str | None = None
    swing: str | None = None
    hinge: Point | None = None


def door_span(door: DoorSymbol, walls: list[WallLine], tol: float) -> tuple[Point, Point] | None:
    """Hinge -> closed end (single leaf, or hinge to hinge for a pair). Without a drawn leaf, the arc end that
    lies along a wall through the hinge is the closed end."""
    if door.leaves == 2:
        return door.swings[0].hinge, door.swings[1].hinge
    sw = door.swings[0]
    closed = sw.closed_end()
    if closed is not None:
        return sw.hinge, closed
    for end in (sw.arc.p_start, sw.arc.p_end):
        ang = axis_angle(sw.hinge, end)
        for w in walls:
            if angle_diff(ang, w.angle) <= 2 and abs(w.local(sw.hinge)[1]) <= w.thickness / 2 + tol:
                return sw.hinge, end
    return None


def _wall_for_span(span: tuple[Point, Point], walls: list[WallLine], tol: float) -> WallLine | None:
    a, b = span
    ang = axis_angle(a, b)
    mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    best = None
    for w in walls:
        if angle_diff(ang, w.angle) > 3:
            continue
        t, d = w.local(mid)
        if abs(d) > w.thickness / 2 + tol or not (w.t0 - tol <= t <= w.t1 + tol):
            continue
        if best is None or abs(d) < best[0]:
            best = (abs(d), w)
    return best[1] if best else None


def _extend_through(span: tuple[Point, Point], walls: list[WallLine], ctx: Ctx) -> WallLine | None:
    """A wall in line with the span whose end stops short of it (or at it): extend it through the doorway."""
    tol = ctx.params.tol_mm
    a, b = span
    ang = axis_angle(a, b)
    best = None
    for w in walls:
        if angle_diff(ang, w.angle) > 3:
            continue
        ta, da = w.local(a)
        tb, db = w.local(b)
        if max(abs(da), abs(db)) > w.thickness / 2 + tol:
            continue
        lo, hi = min(ta, tb), max(ta, tb)
        if lo >= w.t1 - tol and lo - w.t1 <= w.thickness:
            gap = lo - w.t1
        elif hi <= w.t0 + tol and w.t0 - hi <= w.thickness:
            gap = w.t0 - hi
        else:
            continue
        if best is None or gap < best[0]:
            best = (gap, w, lo, hi)
    if best is None:
        return None
    _, w, lo, hi = best
    if lo >= w.t1 - tol:
        w.t1 = hi
    else:
        w.t0 = lo
    _clean_junctions(ctx, walls)  # run the extended end on to the wall beyond
    return w


def find_openings(ctx: Ctx, walls: list[WallLine], doors: list[DoorSymbol], lift_outlines: list) -> list[OpeningDraft]:
    tol = ctx.params.tol_mm
    drafts: list[OpeningDraft] = []
    pending = []
    for door in doors:
        span = door_span(door, walls, tol)
        if span is None:
            ctx.finding("door_without_wall", f"A door swing (radius {door.swings[0].radius:.0f} mm) could not be "
                        "placed: no wall runs through its hinge along either end of its arc. Which wall is it in?",
                        door.handles)
            continue
        pending.append((door, span))
    # doors in a wall first, then doorways that extend a wall
    for door, span in pending:
        w = _wall_for_span(span, walls, tol)
        if w is None:
            w = _extend_through(span, walls, ctx)
        drafts.append(_door_draft(ctx, door, span, w))
    # gaps and infill on walls
    for w in walls:
        for gp in w.gaps:
            mid_t = (gp.t0 + gp.t1) / 2
            taken = [d for d in drafts if d.wall is w and d.t0 - tol <= mid_t <= d.t1 + tol]
            if taken:
                d = taken[0]
                d.gap = gp
                d.handles += [h for f in gp.infill for h in f.handles] + [h for s in gp.jambs for h in s.handles]
                gap_w = gp.t1 - gp.t0
                if abs(gap_w - d.width) > max(10 * tol, 0.02 * d.width) and not gp.infill:
                    d.note = f"wall gap {gap_w:.0f}"
                continue
            center = from_frame(w.center, mid_t, w.u, w.n)
            handles = [h for f in gp.infill for h in f.handles] + [h for s in gp.jambs for h in s.handles]
            kind = "window" if gp.infill else "opening"
            draft = OpeningDraft(kind, w, gp.t0, gp.t1, center, gp.t1 - gp.t0, handles, gap=gp)
            if gp.infill and any(_bounds_shaft(w, outline) for outline in lift_outlines):
                draft.kind, draft.swing = "door", "sliding"
                draft.note = "lift landing door (sliding-door lines in a lift shaft wall)"
            drafts.append(draft)
    return drafts


def _bounds_shaft(w: WallLine, outline) -> bool:
    return w.body().buffer(1.0).intersection(outline.exterior).length >= min(w.length, outline.exterior.length) * 0.2


def _door_draft(ctx: Ctx, door: DoorSymbol, span: tuple[Point, Point], w: WallLine | None) -> OpeningDraft:
    a, b = span
    width = sum(sw.radius for sw in door.swings)
    hinge = door.swings[0].hinge if door.leaves == 1 else None
    mid = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
    if w is None:
        ctx.finding("door_not_in_wall", f"A door ({width:.0f} mm) closes a gap between walls but lies in no wall "
                    "(its leaf does not run along any wall's line). Which wall should hold it, or is a wall missing?",
                    door.handles)
        return OpeningDraft("door", None, 0.0, width, mid, width, list(door.handles), door=door, span=span,
                            hinge=hinge)
    ta, tb = sorted((w.local(a)[0], w.local(b)[0]))
    center = from_frame(w.center, (ta + tb) / 2, w.u, w.n)
    draft = OpeningDraft("door", w, ta, tb, center, width, list(door.handles), door=door, span=span, hinge=hinge)
    draft.swing = _swing(door, w)
    return draft


def _swing(door: DoorSymbol, w: WallLine) -> str | None:
    """Side the leaf opens to, as left/right hand: seen from the side the door opens away from, the hinge is on
    the left ("left") or right ("right"). Exterior walls are re-labelled in/out later."""
    sw = door.swings[0]
    if door.leaves != 1 or sw.open_end is None:
        return None
    side = w.local(sw.open_end)[1] - w.local(sw.hinge)[1]  # + : opens towards +n
    closed = sw.closed_end()
    along = w.local(closed)[0] - w.local(sw.hinge)[0]  # + : closed end lies towards +u from the hinge
    # viewer stands on the side opposite the swing, facing the wall (looking towards the swing side)
    # facing +n: right hand is +u ... the hinge is on the viewer's left when the leaf runs from hinge towards +u
    facing_pos = side > 0
    hinge_left = (along > 0) if facing_pos else (along < 0)
    return "left" if hinge_left else "right"


def swing_side(draft: OpeningDraft) -> float | None:
    """+1/-1: which side of its wall (along the wall's normal) the door leaf opens to."""
    if draft.door is None or draft.wall is None or draft.door.swings[0].open_end is None:
        return None
    sw = draft.door.swings[0]
    return 1.0 if draft.wall.local(sw.open_end)[1] - draft.wall.local(sw.hinge)[1] > 0 else -1.0


# ------------------------------------------------------------------ marks


def assign_marks(ctx: Ctx, drafts: list[OpeningDraft], mark_texts: list[Text]) -> None:
    """Pair mark texts with openings (nearest compatible first, one text per opening)."""
    cands = []
    for t in mark_texts:
        mk = normalise_mark(t.flat)
        kind = mark_kind(mk) if mk else None
        for i, d in enumerate(drafts):
            if kind and not _compatible(kind, d.kind):
                continue
            dist = _distance(t, d)
            reach = 3.0 * t.height + d.width / 2
            if dist <= reach:
                cands.append((dist / reach, dist, t, i))
    cands.sort(key=lambda c: c[0])
    used_t: set[str] = set()
    used_o: set[int] = set()
    by_text: dict[str, list] = defaultdict(list)
    for c in cands:
        by_text[c[2].handle].append(c)
    for score, dist, t, i in cands:
        if t.handle in used_t or i in used_o:
            continue
        used_t.add(t.handle)
        used_o.add(i)
        drafts[i].mark_text = t
        others = [c for c in by_text[t.handle] if c[3] != i and c[3] not in used_o]
        if others and others[0][0] <= score * 1.25 + 0.05:
            o = drafts[others[0][3]]
            ctx.finding("mark_ambiguous", f"Mark \"{t.flat}\" is about as near to the {o.kind} at "
                        f"({o.center[0]:.0f}, {o.center[1]:.0f}) as to the {drafts[i].kind} at "
                        f"({drafts[i].center[0]:.0f}, {drafts[i].center[1]:.0f}) it was given to. Which one is it?",
                        [t.handle] + drafts[i].handles[:4] + o.handles[:4])
    for t in mark_texts:
        if t.handle not in used_t:
            ctx.finding("mark_without_opening", f"Mark \"{t.flat}\" at ({t.pos[0]:.0f}, {t.pos[1]:.0f}) is near no "
                        "opening it can belong to. What does it mark?", [t.handle])


def _compatible(mark: str, kind: str) -> bool:
    if mark == "door":
        return kind in ("door", "opening")
    if mark == "window":
        return kind in ("window", "opening")
    if mark == "ventilator":
        return kind in ("window", "ventilator", "opening")
    return True


def _distance(t: Text, d: OpeningDraft) -> float:
    if d.wall is not None:
        a = from_frame(d.wall.center, d.t0, d.wall.u, d.wall.n)
        b = from_frame(d.wall.center, d.t1, d.wall.u, d.wall.n)
    elif d.span is not None:
        a, b = d.span
    else:
        a = b = d.center
    dist = point_seg_distance(t.center, a, b)
    # measured to the text box, not its centre
    return max(0.0, dist - min(t.width, t.height) / 2)


def to_openings(ctx: Ctx, drafts: list[OpeningDraft], exterior_side: dict[str, float]) -> list[Opening]:
    out = []
    drafts.sort(key=lambda d: (round(-d.center[1]), round(d.center[0])))
    for d in drafts:
        oid = ctx.new_id("opening")
        mark = d.mark_text.flat if d.mark_text else None
        handles = list(d.handles) + ([d.mark_text.handle] if d.mark_text else [])
        kind = d.kind
        mk = normalise_mark(mark) if mark else None
        if kind == "window" and mk and mark_kind(mk) == "ventilator":
            kind = "ventilator"
        if kind == "opening" and mk and mark_kind(mk) in ("door", "window", "ventilator"):
            ctx.finding("opening_marked", f"A plain gap in wall {d.wall.id if d.wall else '-'} "
                        f"({d.width:.0f} mm) has mark \"{mark}\" but no {mark_kind(mk)} drawn in it. "
                        f"Is it a {mark_kind(mk)}?", handles)
        swing = d.swing
        if d.wall is not None and d.wall.id in exterior_side and d.kind == "door" and swing in ("left", "right"):
            side = swing_side(d)
            if side is not None:
                swing = "out" if side == exterior_side[d.wall.id] else "in"
        leaves = d.door.leaves if d.door else None
        o = Opening(id=oid, kind=kind, wall_id=d.wall.id if d.wall else None,
                    center=(round(d.center[0], 2), round(d.center[1], 2)), width_mm=round(d.width, 1),
                    mark=mark, mark_normalised=mk, swing=swing,
                    hinge=(round(d.hinge[0], 2), round(d.hinge[1], 2)) if d.hinge else None,
                    leaves=leaves, block=d.door.block if d.door else None,
                    evidence=ctx.ev(handles, d.note))
        ctx.claim(handles, oid)
        out.append(o)
    return out


def mark_report(ctx: Ctx, openings: list[Opening], mark_texts: list[Text]) -> dict:
    """Counts of mark texts and of marked openings by normalised mark; checks one width per door/window mark."""
    texts = Counter(normalise_mark(t.flat) for t in mark_texts)
    marked = Counter(o.mark_normalised for o in openings if o.mark_normalised)
    unmarked = Counter(o.kind for o in openings if not o.mark_normalised)
    widths: dict[str, Counter] = defaultdict(Counter)
    for o in openings:
        if o.mark_normalised:
            widths[o.mark_normalised][round(o.width_mm)] += 1
    for mk, ws in sorted(widths.items()):
        if len(ws) > 1:
            listed = ", ".join(f"{w} mm x{n}" for w, n in sorted(ws.items()))
            handles = [h for o in openings if o.mark_normalised == mk and round(o.width_mm) != ws.most_common(1)[0][0]
                       for h in o.evidence.handles[:1]]
            ctx.finding("mark_width_mismatch", f"Openings marked {mk} are drawn at different widths: {listed}. "
                        f"A mark is one type; which width is {mk}?", handles,
                        options=[f"{w} mm" for w in sorted(ws)])
    for o in openings:
        if not o.mark_normalised and o.kind in ("door", "window", "ventilator") and not (
                o.evidence.note or "").startswith("lift landing door"):
            ctx.finding("opening_without_mark", f"The {o.kind} {o.id} ({o.width_mm:.0f} mm, wall {o.wall_id or '-'}) "
                        "has no mark. Which type is it?", o.evidence.handles, severity="check")
    return {"texts": dict(sorted(texts.items(), key=lambda kv: str(kv[0]))),
            "marked": dict(sorted(marked.items())), "unmarked": dict(unmarked),
            "widths": {k: dict(v) for k, v in sorted(widths.items())}}
