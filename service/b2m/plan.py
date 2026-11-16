"""The build plan: what the layout engine emits and the runner executes (PRD: Data contracts).

It deliberately knows nothing about add-in command names; b2m.revit.commands maps ops to commands.
All lengths are millimetres. Every op has a stable ``key``; hosted ops point at their host by key.
"""

from __future__ import annotations

from typing import Annotated, Literal, Union

from pydantic import BaseModel, ConfigDict, Field, model_validator

Point = tuple[float, float]
PLAN_VERSION = "1.0"
# Separates an op's key from the number of an extra element it created (a stair op's second railing: "RAIL#2").
KEY_SEPARATOR = "#"


class _Op(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    key: str = Field(min_length=1)


class LevelOp(_Op):
    op: Literal["level"]
    name: str = Field(min_length=1)
    elevation_mm: float


class GridOp(_Op):
    op: Literal["grid"]
    name: str = Field(min_length=1)
    start: Point
    end: Point


class WallOp(_Op):
    op: Literal["wall"]
    level: str
    type: str | None = None
    start: Point
    end: Point
    height_mm: float | None = Field(default=None, gt=0)
    top_level: str | None = None
    top_offset_mm: float = 0
    base_offset_mm: float = 0
    location_line: Literal[
        "wall_centerline", "core_centerline", "finish_face_exterior",
        "finish_face_interior", "core_face_exterior", "core_face_interior",
    ] = "wall_centerline"

    @model_validator(mode="after")
    def _height_or_top(self) -> "WallOp":
        if (self.height_mm is None) == (self.top_level is None):
            raise ValueError("a wall needs exactly one of height_mm or top_level")
        return self


class FloorOp(_Op):
    op: Literal["floor"]
    level: str
    type: str | None = None
    points: list[Point] = Field(min_length=3)
    # Top of the floor above its level (a chajja at lintel height); left out of dumps when 0, so plans without
    # offsets serialise exactly as before.
    offset_mm: float = Field(default=0, exclude_if=lambda v: v == 0)


class ColumnOp(_Op):
    """A structural column standing on ``level`` (+ base offset) up to ``top_level`` (+ top offset)."""

    op: Literal["column"]
    level: str
    top_level: str
    point: Point
    type: str
    family: str | None = None
    rotation_deg: float = 0
    base_offset_mm: float = 0
    top_offset_mm: float = 0


class BeamOp(_Op):
    """A structural beam between two plan points, top at its level."""

    op: Literal["beam"]
    level: str
    start: Point
    end: Point
    type: str
    family: str | None = None

    @model_validator(mode="after")
    def _length(self) -> "BeamOp":
        if self.start == self.end:
            raise ValueError("a beam needs distinct start and end points")
        return self


class StairRun(BaseModel):
    """One straight flight: ``start`` is on its centreline at the first riser; ``end`` gives the direction only."""

    model_config = ConfigDict(extra="forbid", frozen=True)
    start: Point
    end: Point
    risers: int = Field(ge=2)

    @model_validator(mode="after")
    def _direction(self) -> "StairRun":
        if self.start == self.end:
            raise ValueError("a stair run needs distinct start and end points")
        return self


class StairOp(_Op):
    """A stair from one level to the next; consecutive runs are joined by landings."""

    op: Literal["stair"]
    base_level: str
    top_level: str
    type: str | None = None
    width_mm: float = Field(gt=0)
    tread_mm: float = Field(gt=0)
    base_offset_mm: float = 0
    runs: list[StairRun] = Field(min_length=1)


class RailingOp(_Op):
    """A railing on a stair (``stair_key``) or along a sketched path on a level."""

    op: Literal["railing"]
    type: str | None = None
    stair_key: str | None = None
    placement: Literal["treads", "stringer"] = "treads"
    path: list[Point] | None = Field(default=None, min_length=2)
    level: str | None = None
    base_offset_mm: float = 0

    @model_validator(mode="after")
    def _host(self) -> "RailingOp":
        if (self.stair_key is None) == (self.path is None):
            raise ValueError("a railing needs exactly one of stair_key or path")
        if self.path is not None and self.level is None:
            raise ValueError("a railing along a path needs a level")
        return self


class ShaftOp(_Op):
    """A vertical opening through every floor between two levels (stair core)."""

    op: Literal["shaft"]
    base_level: str
    top_level: str
    points: list[Point] = Field(min_length=3)
    base_offset_mm: float = 0
    top_offset_mm: float = 0


class _HostedOp(_Op):
    host_key: str
    type: str
    family: str | None = None
    offset_mm: float = Field(ge=0)
    flip_facing: bool = False
    flip_hand: bool = False


class DoorOp(_HostedOp):
    op: Literal["door"]


class WindowOp(_HostedOp):
    op: Literal["window"]
    sill_mm: float | None = Field(default=None, ge=0)


class RoomOp(_Op):
    op: Literal["room"]
    level: str
    point: Point
    name: str
    number: str | None = None
    flat: str | None = None


class RoomSeparatorOp(_Op):
    """Room separation lines on a level through ``points`` (an open polyline, or a loop with ``close_loop``)."""

    op: Literal["room_separator"]
    level: str
    points: list[Point] = Field(min_length=2)
    close_loop: bool = False


class FoundationSlabOp(_Op):
    """A structural foundation slab (raft): top at ``level`` (+ ``offset_mm``), outline ``points``."""

    op: Literal["foundation_slab"]
    level: str
    type: str
    points: list[Point] = Field(min_length=3)
    thickness_mm: float | None = Field(default=None, gt=0)
    offset_mm: float = 0


class _TypeOp(_Op):
    """A type the plan's elements use, made sure of before anything is built: found, else a copy of another type is
    made and set (duplicate before edit; an existing type is never changed). ``name`` is the logical name the
    elements refer to; the runner swaps in the project type the add-in returns."""

    name: str = Field(min_length=1)
    base_type_name: str | None = None


class TypeLayer(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    thickness_mm: float = Field(gt=0)
    function: str
    material: str | None = None


class WallTypeOp(_TypeOp):
    op: Literal["wall_type"]
    thickness_mm: float = Field(gt=0)
    function: Literal["exterior", "interior"]
    layers: list[TypeLayer] | None = None


class FloorTypeOp(_TypeOp):
    op: Literal["floor_type"]
    thickness_mm: float = Field(gt=0)
    structural: bool = False


class OpeningTypeOp(_TypeOp):
    """A door or window type by size and type mark; the add-in picks the family unless ``family`` is given."""

    op: Literal["opening_type"]
    category: Literal["door", "window"]
    width_mm: float = Field(gt=0)
    height_mm: float = Field(gt=0)
    type_mark: str | None = None
    family: str | None = None
    hint: str | None = None


Op = Annotated[Union[LevelOp, GridOp, ColumnOp, WallOp, FloorOp, ShaftOp, BeamOp, StairOp, RailingOp, DoorOp, WindowOp,
                     RoomOp, RoomSeparatorOp, FoundationSlabOp, WallTypeOp, FloorTypeOp, OpeningTypeOp],
               Field(discriminator="op")]

# Ops that make sure of a type rather than create a model element: never counted in the read-back.
TYPE_OPS = frozenset({"wall_type", "floor_type", "opening_type"})

# Build order (PRD): types (before anything uses them), levels, grids, columns, walls (which then join the columns),
# floors and foundation slabs, shaft openings (which cut the floors), beams (onto columns, under the slabs), stairs
# (inside the cut shafts), railings (on their stairs), doors and windows (into their walls), room separation lines,
# rooms (once every bounding element exists).
PHASES: tuple[tuple[str, ...], ...] = (("wall_type", "floor_type", "opening_type"), ("level",), ("grid",), ("column",),
                                       ("wall",), ("floor", "foundation_slab"), ("shaft",), ("beam",), ("stair",),
                                       ("railing",), ("door", "window"), ("room_separator",), ("room",))
PHASE_OF = {name: i for i, names in enumerate(PHASES) for name in names}


class BuildPlan(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    plan_version: Literal["1.0"] = PLAN_VERSION
    spec_hash: str | None = None
    ops: list[Op] = Field(min_length=1)

    @model_validator(mode="after")
    def _references(self) -> "BuildPlan":
        keys: dict[str, _Op] = {}
        for op in self.ops:
            if KEY_SEPARATOR in op.key:
                raise ValueError(f"{op.key}: keys may not contain {KEY_SEPARATOR!r}")
            if op.key in keys:
                raise ValueError(f"duplicate key {op.key!r}")
            keys[op.key] = op
        levels = {op.name for op in self.ops if isinstance(op, LevelOp)}
        elevation = {op.name: op.elevation_mm for op in self.ops if isinstance(op, LevelOp)}
        if len(levels) != sum(isinstance(op, LevelOp) for op in self.ops):
            raise ValueError("level names must be unique")
        type_names = [(op.op, op.name) for op in self.ops if isinstance(op, _TypeOp)]
        if len(set(type_names)) != len(type_names):
            raise ValueError("a type may be made sure of only once per plan")
        for op in self.ops:
            for field in ("level", "top_level", "base_level"):
                name = getattr(op, field, None)
                if name is not None and name not in levels:
                    raise ValueError(f"{op.key}: {field} {name!r} is not a level in this plan")
            if isinstance(op, _HostedOp):
                host = keys.get(op.host_key)
                if not isinstance(host, WallOp):
                    raise ValueError(f"{op.key}: host_key {op.host_key!r} is not a wall in this plan")
            if isinstance(op, RailingOp) and op.stair_key is not None and not isinstance(keys.get(op.stair_key), StairOp):
                raise ValueError(f"{op.key}: stair_key {op.stair_key!r} is not a stair in this plan")
            if isinstance(op, (ColumnOp, StairOp)):
                base = op.level if isinstance(op, ColumnOp) else op.base_level
                if elevation[op.top_level] <= elevation[base]:
                    raise ValueError(f"{op.key}: top_level {op.top_level!r} is not above {base!r}")
        return self

    def ordered(self) -> list[Op]:
        """Ops in build order; stable within a phase, so the same plan always builds the same way."""
        return sorted(self.ops, key=lambda op: PHASE_OF[op.op])

    def by_key(self) -> dict[str, Op]:
        return {op.key: op for op in self.ops}


def translate(plan: BuildPlan, dx: float, dy: float) -> BuildPlan:
    """The same plan moved by (dx, dy) mm, e.g. to build a test clear of an existing model."""

    def move(value):
        if isinstance(value, tuple) and len(value) == 2 and all(isinstance(v, (int, float)) for v in value):
            return (value[0] + dx, value[1] + dy)
        if isinstance(value, list):
            return [move(v) for v in value]
        return value

    ops = []
    for op in plan.ops:
        data = op.model_dump()
        for name in ("start", "end", "point", "points", "path"):
            if data.get(name) is not None:
                data[name] = move(data[name])
        for run in data.get("runs") or []:
            run["start"], run["end"] = move(run["start"]), move(run["end"])
        ops.append(data)
    return BuildPlan.model_validate({"plan_version": plan.plan_version, "spec_hash": plan.spec_hash, "ops": ops})
