"""The build plan: what the layout engine emits and the runner executes (PRD: Data contracts).

It deliberately knows nothing about add-in command names; b2m.revit.commands maps ops to commands.
All lengths are millimetres. Every op has a stable ``key``; hosted ops point at their host by key.
"""

from __future__ import annotations

from typing import Annotated, Literal, Union

from pydantic import BaseModel, ConfigDict, Field, model_validator

Point = tuple[float, float]
PLAN_VERSION = "1.0"


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


Op = Annotated[Union[LevelOp, GridOp, WallOp, FloorOp, ShaftOp, DoorOp, WindowOp, RoomOp], Field(discriminator="op")]

# Build order (PRD): levels, grids, walls, floors, openings, doors and windows, rooms.
PHASES: tuple[tuple[str, ...], ...] = (("level",), ("grid",), ("wall",), ("floor",), ("shaft",), ("door", "window"), ("room",))
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
            if op.key in keys:
                raise ValueError(f"duplicate key {op.key!r}")
            keys[op.key] = op
        levels = {op.name for op in self.ops if isinstance(op, LevelOp)}
        if len(levels) != sum(isinstance(op, LevelOp) for op in self.ops):
            raise ValueError("level names must be unique")
        for op in self.ops:
            for field in ("level", "top_level", "base_level"):
                name = getattr(op, field, None)
                if name is not None and name not in levels:
                    raise ValueError(f"{op.key}: {field} {name!r} is not a level in this plan")
            if isinstance(op, _HostedOp):
                host = keys.get(op.host_key)
                if not isinstance(host, WallOp):
                    raise ValueError(f"{op.key}: host_key {op.host_key!r} is not a wall in this plan")
        return self

    def ordered(self) -> list[Op]:
        """Ops in build order; stable within a phase, so the same plan always builds the same way."""
        return sorted(self.ops, key=lambda op: PHASE_OF[op.op])

    def by_key(self) -> dict[str, Op]:
        return {op.key: op for op in self.ops}
