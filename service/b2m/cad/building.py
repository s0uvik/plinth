"""The recognised building: what the drawings say, before anything is built (PRD Step 9, stages 3-5).

Plan recognition (T3) fills one ``PlanRecognition`` per floor-plan or roof-plan sheet; section and foundation
recognition (T4) fill ``SectionRecognition``, ``FoundationRecognition`` and ``BeamLayoutRecognition``, and
registration (T4) ties every sheet to one building frame. The build plan (T6), self-check (T9), drawings (T10)
and estimate (T11) read only this model, never the CAD entities, so every fact here keeps its evidence: the
handles of the CAD entities it came from. Nothing is guessed: a value the drawings do not give is None and a
``Finding`` (question) says so.

Coordinates are millimetres. Within a recognition they are in that sheet's own drawing coordinates; the sheet's
``Registration`` maps them into the building frame (rotation in degrees counter-clockwise, then translation).

Optional fields added by T4 (all default to "not given", so older producers stay valid):
SectionRecognition.cut_path / cut_sheet / view_direction / unexplained, Footing.kind, RebarSpec.legs,
FoundationRecognition.plan_columns / unexplained, BeamLayoutRecognition.column_evidence / unexplained,
Registration.frame.

Optional fields added by T5 (the import pipeline): SectionMark.cut / view, PlanRecognition.room_separations,
RecognisedBuilding.resolved (cross-sheet resolutions, with evidence from every sheet involved) and
RecognisedBuilding.answered (which answered questions have been applied to this copy).
"""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

Point = tuple[float, float]


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Evidence(_Model):
    sheet: str  # the sheet's source file name
    handles: list[str] = []  # CAD entity handles (children of inserts: "<parent>:<index>")
    note: str | None = None


class Finding(_Model):
    """Something the recogniser could not settle from the drawings. Becomes a question in the import project."""

    id: str  # stable within a sheet, e.g. "wall-gap-12"
    topic: str  # e.g. "opening_without_mark", "unclaimed_geometry", "storey_height"
    text: str  # plain words for the user
    severity: Literal["blocking", "check", "info"] = "check"
    evidence: Evidence
    options: list[str] = []


class Grid(_Model):
    id: str
    name: str | None = None  # bubble text when drawn
    start: Point
    end: Point
    evidence: Evidence


class Wall(_Model):
    id: str
    start: Point  # centreline
    end: Point
    thickness_mm: float
    kind: Literal["exterior", "interior", "parapet", "boundary", "unknown"] = "unknown"
    height_mm: float | None = None  # only when the drawings give it (e.g. parapets, boundary walls)
    evidence: Evidence


class Column(_Model):
    id: str
    center: Point
    width_mm: float  # along x after rotation
    depth_mm: float
    rotation_deg: float = 0.0
    shape: Literal["rectangular", "circular"] = "rectangular"
    evidence: Evidence


class Opening(_Model):
    id: str
    kind: Literal["door", "window", "opening", "ventilator"]
    wall_id: str | None
    center: Point  # on the wall centreline
    width_mm: float  # clear opening width measured on the drawing
    mark: str | None = None  # as written on the drawing ("D1", "w2"); normalised separately
    mark_normalised: str | None = None  # e.g. "W2"
    height_mm: float | None = None  # only from sections, elevations, schedules or the user
    sill_mm: float | None = None
    swing: Literal["in", "out", "left", "right", "sliding", "none"] | None = None
    hinge: Point | None = None  # door hinge point, when a swing arc shows it
    leaves: int | None = None
    block: str | None = None  # effective block name for blocks (e.g. "Door - Metric")
    evidence: Evidence


class Room(_Model):
    id: str
    name: str  # as written ("BED ROOM")
    point: Point  # a point inside the room (the label position)
    labelled_size_mm: tuple[float, float] | None = None  # from text like "3500 X 4000", as written order
    boundary: list[Point] | None = None  # closed polygon when walls enclose it
    area_m2: float | None = None
    unit: str | None = None  # flat/unit id when the drawing groups rooms (e.g. by mirrored flats)
    evidence: Evidence


class Stair(_Model):
    id: str
    outline: list[Point]
    kind: Literal["dog_leg", "straight", "l_shaped", "u_shaped", "spiral", "unknown"] = "unknown"
    width_mm: float | None = None
    tread_mm: float | None = None
    risers_per_flight: list[int] = []  # counted treads/risers per flight in plan, when countable
    up_arrow: Point | None = None
    evidence: Evidence


class Shaft(_Model):
    id: str
    kind: Literal["lift", "duct", "stair", "unknown"]
    outline: list[Point]
    label: str | None = None
    evidence: Evidence


class Projection(_Model):
    """Balcony, verandah, chajja, canopy, sunshade or ledge outside the main walls."""

    id: str
    kind: Literal["balcony", "verandah", "chajja", "canopy", "ledge", "unknown"]
    outline: list[Point]
    railing: bool | None = None
    evidence: Evidence


class SectionMark(_Model):
    """A section cut line drawn on a plan (labelled arrows joined by a cut line): where section recognition (T4)
    takes the section's cut_line from. One finder (b2m.cad.marks) produces these for every sheet."""

    name: str  # "A" for section AA
    points: list[Point]  # the line as drawn, from one arrow's base to the other's, every corner (may be stepped)
    cut: list[Point] | None = None  # the cut itself: points without the tails that run along the arrows
    view: Point | None = None  # unit vector the section looks along (the arrows' direction)
    evidence: Evidence


class RoofFall(_Model):
    """A slope arrow on a roof plan: water falls from start towards end (the arrowhead)."""

    id: str
    start: Point
    end: Point
    label: str | None = None  # the text written along it, e.g. "SLOPE"
    evidence: Evidence


class RoomSeparation(_Model):
    """A line dividing rooms that share one enclosed space (no wall between them): given by the user's answer."""

    id: str
    start: Point
    end: Point
    rooms: list[str] = []  # the ids of the rooms it separates
    evidence: Evidence  # the question that gave it (note) and the rooms' handles


class PlanRecognition(_Model):
    sheet: str
    role: Literal["floor_plan", "roof_plan"]
    storeys: list[int] = []  # from classification; roof plans may be empty
    grids: list[Grid] = []
    walls: list[Wall] = []
    columns: list[Column] = []
    openings: list[Opening] = []
    rooms: list[Room] = []
    stairs: list[Stair] = []
    shafts: list[Shaft] = []
    projections: list[Projection] = []
    outline: list[Point] | None = None  # outer face of the building on this plan
    section_marks: list[SectionMark] = []  # section cut lines drawn on the plan (for T4's cut_line)
    falls: list[RoofFall] = []  # roof plans: slope arrows (no other field carries roof drainage)
    room_separations: list[RoomSeparation] = []  # from answers (rooms sharing one space)
    findings: list[Finding] = []
    unclaimed: list[Evidence] = []  # drawing geometry no recogniser used (listed, never silently dropped)


class Level(_Model):
    name: str  # label as written ("PL", "GL", "1st floor") or derived ("storey 2")
    elevation_mm: float  # relative to the section's datum (see datum)
    kind: Literal["ground", "plinth", "floor", "roof", "parapet_top", "lintel", "sill", "foundation", "other"]
    storey: int | None = None
    evidence: Evidence


class SectionRecognition(_Model):
    sheet: str
    name: str  # "AA"
    datum: str  # which level elevation 0 is ("PL" or "GL")
    levels: list[Level] = []
    storey_heights_mm: list[float] = []  # floor to floor, bottom up, from dimensions
    slab_thickness_mm: float | None = None
    beam_depth_mm: float | None = None
    parapet_height_mm: float | None = None
    plinth_height_mm: float | None = None  # PL above GL
    foundation_layers: list[tuple[str, float]] = []  # ("PCC", 150), ("BFS", 75), ...
    door_heights_mm: list[float] = []
    window_heads_mm: list[float] = []
    window_sills_mm: list[float] = []
    cut_line: tuple[Point, Point] | None = None  # in the plan's frame, from the section marks on plans
    # A straight cut: cut_line is its two ends. A jogged cut: cut_line is None and cut_path holds every corner.
    cut_path: list[Point] | None = None  # the cut from end to end, in cut_sheet's coordinates (tails to the arrows removed)
    cut_sheet: str | None = None  # the sheet whose coordinates cut_line/cut_path are in (the building frame when registered)
    view_direction: Point | None = None  # unit vector the section looks along (the marks' arrows), same coordinates
    findings: list[Finding] = []
    unexplained: list[Evidence] = []  # dimensions this recogniser could not give a meaning (listed, never dropped)


class Footing(_Model):
    id: str
    kind: Literal["isolated", "combined", "raft", "strip", "unknown"] = "unknown"  # by how many columns it carries
    center: Point
    width_mm: float
    length_mm: float
    depth_mm: float | None = None
    founding_depth_mm: float | None = None  # below GL
    column_id: str | None = None
    evidence: Evidence


class RebarSpec(_Model):
    member: str  # "footing", "column 250x500", "tie beam", ...
    text: str  # as written
    bar_dia_mm: float | None = None
    spacing_mm: float | None = None
    count: int | None = None
    legs: int | None = None  # stirrup legs ("2 legged")
    role: str | None = None  # "main", "distribution", "stirrup", ...
    cover_mm: float | None = None
    evidence: Evidence


class FoundationRecognition(_Model):
    sheet: str
    footings: list[Footing] = []
    column_sections: list[Column] = []  # typical column sections drawn on the sheet
    rebar: list[RebarSpec] = []
    plan_columns: list[Column] = []  # columns as drawn on the footing plan (positions; used to register the sheet)
    findings: list[Finding] = []
    unexplained: list[Evidence] = []


class Beam(_Model):
    id: str
    start: Point
    end: Point
    width_mm: float | None = None
    depth_mm: float | None = None
    evidence: Evidence


class BeamLayoutRecognition(_Model):
    sheet: str
    storey: int | None = None
    beams: list[Beam] = []
    grids: list[Grid] = []
    column_points: list[Point] = []
    column_evidence: list[Evidence] = []  # one per column point, same order
    findings: list[Finding] = []
    unexplained: list[Evidence] = []


class Registration(_Model):
    """Maps one sheet's drawing coordinates into the building frame."""

    sheet: str
    # "plan": (x, y) -> building plan (x, y). "vertical" (sections): only y is mapped, y + translation[1] = elevation
    # above the building datum; x is left as drawn (translation[0] = 0).
    frame: Literal["plan", "vertical"] = "plan"
    rotation_deg: float = 0.0
    translation: Point = (0.0, 0.0)
    method: str  # how it was found, e.g. "grids", "columns", "outline"
    residual_mm: float  # worst mismatch of the matched features after transforming
    evidence: Evidence


class Resolution(_Model):
    """A finding that another sheet settles, e.g. an unlabelled opening on one plan that sits in the wall of a lift
    shaft labelled on another plan. The finding is dropped from its sheet; this keeps what settled it."""

    id: str
    topic: str  # the resolved finding's topic
    text: str  # what was decided and why
    findings: list[str] = []  # "<sheet>#<finding id>" of every finding it settles
    evidence: list[Evidence] = []  # from every sheet involved


class RecognisedBuilding(_Model):
    plans: list[PlanRecognition] = []
    sections: list[SectionRecognition] = []
    foundations: list[FoundationRecognition] = []
    beam_layouts: list[BeamLayoutRecognition] = []
    registrations: list[Registration] = []
    findings: list[Finding] = Field(default_factory=list)  # cross-sheet findings
    resolved: list[Resolution] = []  # findings settled by another sheet (removed from the findings lists)
    answered: list[str] = []  # ids of the import project's questions whose answers are applied to this copy

