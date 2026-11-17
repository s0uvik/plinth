"""The build plan: what the layout engine emits and the runner executes (PRD: Data contracts).

It deliberately knows nothing about add-in command names; plinth.revit.commands maps ops to commands.
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
    """A structural foundation slab (raft): top at ``level`` (+ ``offset_mm``), outline ``points``; its thickness is
    its type's (a foundation slab floor type, made sure of by a ``floor_type`` op with ``foundation``)."""

    op: Literal["foundation_slab"]
    level: str
    type: str
    points: list[Point] = Field(min_length=3)
    offset_mm: float = 0


class _TypeOp(_Op):
    """A type the plan's elements use, made sure of before anything is built: found, else a copy of another type is
    made and set (duplicate before edit; an existing type is never changed). ``name`` is the logical name the
    elements refer to; the runner swaps in the project type the add-in returns."""

    name: str = Field(min_length=1)
    base_type_name: str | None = None


class TypeLayer(BaseModel):
    """One layer of a wall or floor type, outside (walls) or top (floors) first. Without a material the copied
    type's material for that function is kept."""

    model_config = ConfigDict(extra="forbid", frozen=True)
    function: Literal["structure", "finish1", "finish2", "substrate", "insulation", "membrane"]
    thickness_mm: float = Field(ge=0)
    material: str | None = None


class _HostTypeOp(_TypeOp):
    thickness_mm: float = Field(gt=0)
    layers: list[TypeLayer] | None = None

    @model_validator(mode="after")
    def _layers_add_up(self) -> "_HostTypeOp":
        if self.layers is not None:
            if abs(sum(layer.thickness_mm for layer in self.layers) - self.thickness_mm) > 0.01:
                raise ValueError(f"{self.key}: the layers must add up to thickness_mm")
            if not any(layer.function == "structure" for layer in self.layers):
                raise ValueError(f"{self.key}: a type needs a structure layer")
        return self


class WallTypeOp(_HostTypeOp):
    op: Literal["wall_type"]
    function: Literal["exterior", "interior"]


class FloorTypeOp(_HostTypeOp):
    op: Literal["floor_type"]
    structural: bool = False
    foundation: bool = False  # a structural foundation slab type, for foundation_slab ops


class OpeningTypeOp(_TypeOp):
    """A door or window type by size and type mark; the add-in picks the family unless ``family`` is given."""

    op: Literal["opening_type"]
    category: Literal["door", "window"]
    width_mm: float = Field(gt=0)
    height_mm: float = Field(gt=0)
    type_mark: str | None = None
    family: str | None = None
    hint: str | None = None


# ------------------------------------------------------------------------------------------------ fit-out (Step 10, F5)

TemplateTypeKind = Literal["family_type", "pipe_type", "pipe_system_type", "duct_type", "duct_system_type",
                           "conduit_type", "cable_tray_type", "distribution_system", "voltage_type"]
Point3 = tuple[float, float, float]


class TemplateType(BaseModel):
    """One type to bring in from a template or project file: a family type (with its family) or an MEP type, named as
    the file lists it (``list_types_in_file``)."""

    model_config = ConfigDict(extra="forbid", frozen=True)
    kind: TemplateTypeKind
    name: str = Field(min_length=1)
    family: str | None = None  # family types: the family it belongs to
    category: str | None = None  # family types: built-in category, e.g. OST_Furniture
    source_id: int | None = None  # the file's own id, when the name alone is ambiguous

    @model_validator(mode="after")
    def _family(self) -> "TemplateType":
        if self.kind == "family_type" and not self.family:
            raise ValueError(f"{self.name}: a family type needs its family")
        if self.kind != "family_type" and (self.family or self.category):
            raise ValueError(f"{self.name}: an MEP type has no family or category")
        return self


class TypesFromFileOp(_Op):
    """Types the fit-out uses, copied in from an installed template (or another file) as NEW types before anything is
    placed. A type the project already has is used as it is and never touched; only what the copy added belongs to
    the run (and is removed with it)."""

    op: Literal["types_from_file"]
    path: str = Field(min_length=1)
    items: list[TemplateType] = Field(min_length=1, max_length=100)


class FamilyTypeCopyOp(_Op):
    """A family type at another size: a copy of ``base_type`` named ``name`` with its size parameters set (duplicate
    before edit: the base type is never changed). Only planned when the family has those parameters."""

    op: Literal["family_type_copy"]
    category: str = Field(min_length=1)  # built-in, e.g. OST_Furniture
    family: str = Field(min_length=1)
    base_type: str = Field(min_length=1)
    name: str = Field(min_length=1)
    values_mm: dict[str, float] = Field(min_length=1)  # type parameter name -> length in mm, e.g. {"Width": 1800}


class FamilyCreateOp(_Op):
    """A plain box family Plinth creates for a fit-out item, only because the user answered so (``{"create": "box"}``:
    no family suits it): one extrusion ``width_mm`` x ``depth_mm`` x ``height_mm`` with type parameters Width, Depth,
    Height and Material, its origin at the middle of its back edge (back on +Y, front facing -Y) and its base at the
    level, loaded as a NEW family ``family`` with the one type ``type``. A family of that name already in the project is
    used as it is (never overwritten) and is not the run's to remove."""

    op: Literal["family_create"]
    kind: Literal["box"] = "box"
    category: str = Field(min_length=1)  # built-in, e.g. OST_Furniture
    family: str = Field(min_length=1)
    type: str = Field(min_length=1)
    width_mm: float = Field(gt=0, le=20000)
    depth_mm: float = Field(gt=0, le=20000)
    height_mm: float = Field(gt=0, le=20000)
    material: str | None = None


class FamilyOp(_Op):
    """A placed family instance (furniture, fixture, device, equipment) at ``point`` on ``level``.

    Level-based families stand at the point, turned by ``rotation_deg`` (anticlockwise, about the point) and raised by
    ``offset_mm``. With ``host_key`` (a wall or floor of this plan or of ``BuildPlan.existing``), a wall-hosted or
    face-based family sits on that host: on a wall at the height ``offset_mm``, on the side given by ``wall_side`` or
    ``toward`` (a plan point on that side, e.g. inside the room). ``face_down`` puts a work-plane-based family (a light
    or fan with no ceiling) on a plane facing down at ``offset_mm``."""

    op: Literal["family"]
    category: str = Field(min_length=1)  # built-in, e.g. OST_Furniture, OST_LightingFixtures
    family: str | None = None
    type: str = Field(min_length=1)
    level: str
    point: Point
    rotation_deg: float = 0
    offset_mm: float = 0
    host_key: str | None = None
    wall_side: Literal["exterior", "interior"] | None = None
    toward: Point | None = None
    face_down: bool = False
    flip_hand: bool = False
    room_key: str | None = None
    source: str | None = None

    @model_validator(mode="after")
    def _placement(self) -> "FamilyOp":
        if (self.wall_side or self.toward) and not self.host_key:
            raise ValueError(f"{self.key}: a wall side needs host_key")
        if self.wall_side and self.toward:
            raise ValueError(f"{self.key}: give wall_side or toward, not both")
        if self.face_down and self.host_key:
            raise ValueError(f"{self.key}: face_down places a family without a host")
        if self.host_key and self.rotation_deg:
            raise ValueError(f"{self.key}: a hosted family follows its host; no rotation")
        return self


class MepRunOp(_Op):
    """A pipe, duct, conduit or cable tray run through ``points`` (x, y, z in mm; z absolute, as the add-in takes it),
    with an elbow at every bend; each end may be joined to a placed family or another run (``start_key``/``end_key``:
    a family op of this plan, a run op listed before it, or a key of ``BuildPlan.existing``)."""

    op: Literal["mep_run"]
    kind: Literal["pipe", "duct", "conduit", "cable_tray"]
    level: str
    type: str = Field(min_length=1)
    system_type: str | None = None  # pipes and ducts: the piping / duct system type
    points: list[Point3] = Field(min_length=2)
    diameter_mm: float | None = Field(default=None, gt=0)
    width_mm: float | None = Field(default=None, gt=0)
    height_mm: float | None = Field(default=None, gt=0)
    start_key: str | None = None
    end_key: str | None = None
    require_connections: bool = True  # a join Revit refuses undoes the run and stops the build: no silent disconnects
    source: str | None = None

    @model_validator(mode="after")
    def _shape(self) -> "MepRunOp":
        piped = self.kind in ("pipe", "duct")
        if piped and not self.system_type:
            raise ValueError(f"{self.key}: a {self.kind} run needs a system_type")
        if not piped and self.system_type:
            raise ValueError(f"{self.key}: a {self.kind} run has no system_type")
        if self.diameter_mm is not None and (self.width_mm is not None or self.height_mm is not None):
            raise ValueError(f"{self.key}: give diameter_mm or width_mm/height_mm, not both")
        if self.kind in ("pipe", "conduit") and (self.width_mm is not None or self.height_mm is not None):
            raise ValueError(f"{self.key}: a {self.kind} takes diameter_mm")
        if self.kind == "cable_tray" and self.diameter_mm is not None:
            raise ValueError(f"{self.key}: a cable tray takes width_mm and height_mm")
        return self


class CircuitOp(_Op):
    """One electrical circuit: the placed devices ``device_keys`` on it, assigned to the panel ``panel_key``."""

    op: Literal["circuit"]
    device_keys: list[str] = Field(min_length=1, max_length=200)
    panel_key: str | None = None
    system_type: str = "PowerCircuit"
    source: str | None = None


Op = Annotated[Union[LevelOp, GridOp, ColumnOp, WallOp, FloorOp, ShaftOp, BeamOp, StairOp, RailingOp, DoorOp, WindowOp,
                     RoomOp, RoomSeparatorOp, FoundationSlabOp, WallTypeOp, FloorTypeOp, OpeningTypeOp,
                     TypesFromFileOp, FamilyTypeCopyOp, FamilyCreateOp, FamilyOp, MepRunOp, CircuitOp],
               Field(discriminator="op")]

# Ops that make sure of a type rather than create a model element: never counted in the read-back.
TYPE_OPS = frozenset({"wall_type", "floor_type", "opening_type", "types_from_file", "family_type_copy", "family_create"})

# Build order (PRD): types (before anything uses them; a type copied to another size after the type it copies),
# levels, grids, columns, walls (which then join the columns), floors and foundation slabs, shaft openings (which cut
# the floors), beams (onto columns, under the slabs), stairs (inside the cut shafts), railings (on their stairs), doors
# and windows (into their walls), room separation lines, rooms (once every bounding element exists); then the fit-out:
# placed families, MEP runs (joined to the families), circuits (of the placed devices).
PHASES: tuple[tuple[str, ...], ...] = (("wall_type", "floor_type", "opening_type", "types_from_file", "family_create"),
                                       ("family_type_copy",), ("level",), ("grid",), ("column",),
                                       ("wall",), ("floor", "foundation_slab"), ("shaft",), ("beam",), ("stair",),
                                       ("railing",), ("door", "window"), ("room_separator",), ("room",),
                                       ("family",), ("mep_run",), ("circuit",))
PHASE_OF = {name: i for i, names in enumerate(PHASES) for name in names}


def references(op: _Op) -> list[str]:
    """Keys of the elements an op is built on or joined to (its host, stair, run ends, circuit devices and panel)."""
    match op:
        case _HostedOp():
            return [op.host_key]
        case RailingOp():
            return [op.stair_key] if op.stair_key else []
        case FamilyOp():
            return [op.host_key] if op.host_key else []
        case MepRunOp():
            return [k for k in (op.start_key, op.end_key) if k]
        case CircuitOp():
            return [*op.device_keys, *([op.panel_key] if op.panel_key else [])]
    return []


class BuildPlan(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)
    plan_version: Literal["1.0"] = PLAN_VERSION
    spec_hash: str | None = None
    ops: list[Op] = Field(min_length=1)
    # A plan built on top of an earlier run (a fit-out on a built building): the levels it uses that are already in
    # the model (name -> elevation) and the elements it hosts on or joins to (their key in that run -> element id).
    # This plan's run never creates nor removes them. Left out of dumps when empty.
    existing_levels: dict[str, float] = Field(default_factory=dict, exclude_if=lambda v: not v)
    existing: dict[str, int] = Field(default_factory=dict, exclude_if=lambda v: not v)

    @model_validator(mode="after")
    def _references(self) -> "BuildPlan":
        keys: dict[str, _Op] = {}
        for op in self.ops:
            if KEY_SEPARATOR in op.key:
                raise ValueError(f"{op.key}: keys may not contain {KEY_SEPARATOR!r}")
            if op.key in keys:
                raise ValueError(f"duplicate key {op.key!r}")
            if op.key in self.existing:
                raise ValueError(f"{op.key}: the key of an existing element")
            keys[op.key] = op
        self._fitout_references(keys)
        levels = {op.name for op in self.ops if isinstance(op, LevelOp)} | set(self.existing_levels)
        elevation = {**self.existing_levels, **{op.name: op.elevation_mm for op in self.ops if isinstance(op, LevelOp)}}
        if len(levels) != sum(isinstance(op, LevelOp) for op in self.ops) + len(self.existing_levels):
            raise ValueError("level names must be unique")
        type_names = [(op.op, op.name) for op in self.ops if isinstance(op, _TypeOp)]
        if len(set(type_names)) != len(type_names):
            raise ValueError("a type may be made sure of only once per plan")
        floor_types = {op.name: op for op in self.ops if isinstance(op, FloorTypeOp)}
        for op in self.ops:
            if isinstance(op, FoundationSlabOp) and op.type in floor_types and not floor_types[op.type].foundation:
                raise ValueError(f"{op.key}: type {op.type!r} is not a foundation slab type")
            if isinstance(op, FloorOp) and op.type in floor_types and floor_types[op.type].foundation:
                raise ValueError(f"{op.key}: type {op.type!r} is a foundation slab type, not a floor type")
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

    def _fitout_references(self, keys: dict[str, _Op]) -> None:
        """Fit-out ops point at elements of this plan (runs at runs listed before them) or of ``existing``."""
        seen: set[str] = set()
        for op in self.ops:
            def check(ref: str, what: str, kinds: tuple[type, ...]) -> None:
                if ref in self.existing:
                    return
                target = keys.get(ref)
                if not isinstance(target, kinds):
                    names = " or ".join(k.__name__.removesuffix("Op").lower() for k in kinds)
                    raise ValueError(f"{op.key}: {what} {ref!r} is not a {names} in this plan nor an existing element")
                if isinstance(target, MepRunOp) and ref not in seen:
                    raise ValueError(f"{op.key}: {what} {ref!r} is a run listed after it; list the run it joins first")
            if isinstance(op, FamilyOp) and op.host_key:
                check(op.host_key, "host_key", (WallOp, FloorOp))
            if isinstance(op, MepRunOp):
                for ref in (op.start_key, op.end_key):
                    if ref:
                        check(ref, "run end", (FamilyOp, MepRunOp))
            if isinstance(op, CircuitOp):
                for ref in op.device_keys:
                    check(ref, "device", (FamilyOp,))
                if op.panel_key:
                    check(op.panel_key, "panel_key", (FamilyOp,))
                    panel = keys.get(op.panel_key)
                    if isinstance(panel, FamilyOp) and panel.category != "OST_ElectricalEquipment":
                        raise ValueError(f"{op.key}: panel {op.panel_key!r} is not electrical equipment")
            if isinstance(op, FamilyTypeCopyOp) and op.name == op.base_type:
                raise ValueError(f"{op.key}: a type copy needs a new name")
            seen.add(op.key)
        copies = [(op.family, op.name) for op in self.ops if isinstance(op, FamilyTypeCopyOp)]
        if len(set(copies)) != len(copies):
            raise ValueError("a family type may be copied to a name only once per plan")
        made = [op.family.casefold() for op in self.ops if isinstance(op, FamilyCreateOp)]
        if len(set(made)) != len(made):
            raise ValueError("a family may be created only once per plan")

    def ordered(self) -> list[Op]:
        """Ops in build order; stable within a phase, so the same plan always builds the same way."""
        return sorted(self.ops, key=lambda op: PHASE_OF[op.op])

    def by_key(self) -> dict[str, Op]:
        return {op.key: op for op in self.ops}


def translate(plan: BuildPlan, dx: float, dy: float) -> BuildPlan:
    """The same plan moved by (dx, dy) mm, e.g. to build a test clear of an existing model."""

    def move(value):
        if isinstance(value, tuple) and len(value) in (2, 3) and all(isinstance(v, (int, float)) for v in value):
            return (value[0] + dx, value[1] + dy, *value[2:])
        if isinstance(value, list):
            return [move(v) for v in value]
        return value

    ops = []
    for op in plan.ops:
        data = op.model_dump()
        for name in ("start", "end", "point", "points", "path", "toward"):
            if data.get(name) is not None:
                data[name] = move(data[name])
        for run in data.get("runs") or []:
            run["start"], run["end"] = move(run["start"]), move(run["end"])
        ops.append(data)
    return BuildPlan.model_validate({"plan_version": plan.plan_version, "spec_hash": plan.spec_hash, "ops": ops,
                                     "existing_levels": dict(plan.existing_levels), "existing": dict(plan.existing)})
