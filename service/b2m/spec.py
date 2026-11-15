"""Building spec v1 (PRD F3.1): what the brief agent writes and the layout engine reads.

All lengths are millimetres. The plot is a rectangle: ``plot_mm = [width along the road, depth]``.
Validation errors are returned as short, fixable messages (``spec_errors``) so Claude can correct a spec
in up to two retries.
"""

from __future__ import annotations

import hashlib
import json
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, ValidationError, model_validator

SCHEMA_VERSION = "1.0"
UnitType = Literal["1BHK", "2BHK", "3BHK"]
TEMPLATES = {2: "T2", 3: "T3", 4: "T4"}

# Default carpet areas when a brief gives none (recorded as assumptions by the engine).
DEFAULT_CARPET_M2: dict[str, float] = {"1BHK": 35.0, "2BHK": 50.0, "3BHK": 70.0}


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class Site(_Model):
    plot_mm: tuple[int, int] = Field(description="[width along the road, depth] in mm")
    road_side: Literal["front"] = "front"
    city: Literal["kolkata"] = "kolkata"
    road_width_mm: int = Field(ge=2400, le=60000)

    @model_validator(mode="after")
    def _plot(self) -> "Site":
        width, depth = self.plot_mm
        if not (6000 <= width <= 60000 and 6000 <= depth <= 80000):
            raise ValueError("plot_mm must be [6000..60000, 6000..80000] mm (a rectangular plot)")
        return self


class Building(_Model):
    ground: Literal["stilt_parking", "flats"]
    upper_floors: Literal[3, 4] = Field(description="3 for G+3, 4 for G+4")
    floor_to_floor_mm: int = Field(default=3000, ge=2850, le=3600)
    template: Literal["auto", "T2", "T3", "T4"] = "auto"


class UnitMix(_Model):
    type: UnitType
    per_floor: int = Field(ge=1, le=4)
    target_carpet_m2: float | None = Field(default=None, ge=25, le=150)


class Core(_Model):
    stair: Literal["dog_leg"] = "dog_leg"
    stair_width_mm: int = Field(default=1200, ge=1000, le=1800)
    lift: bool = False


class Standards(_Model):
    external_wall: Literal["Brick 230"] = "Brick 230"
    internal_wall: Literal["Brick 115"] = "Brick 115"
    slab: Literal["RCC 150"] = "RCC 150"
    door_types: Literal["default"] = "default"
    window_types: Literal["default"] = "default"
    structure: Literal["rcc_frame", "load_bearing"] = Field(
        default="rcc_frame", description="rcc_frame: RCC columns and beams with brick infill; load_bearing: brick walls only")
    chajja_mm: int = Field(default=600, ge=0, le=1200, description="Sunshade depth over external windows and doors; 0 for none")
    roof_parapet_mm: int = Field(default=1000, ge=0, le=1500, description="Roof parapet height; 0 for none")


# Standards fields added after specs were first hashed: left out of the canonical JSON while they hold their
# default, so a spec written before they existed keeps its hash.
_LATER_STANDARDS = {"structure": "rcc_frame", "chajja_mm": 600, "roof_parapet_mm": 1000}


class BuildingSpec(_Model):
    schema_version: Literal["1.0"] = SCHEMA_VERSION
    site: Site
    building: Building
    units: list[UnitMix] = Field(min_length=1, max_length=3)
    core: Core = Core()
    standards: Standards = Standards()
    assumptions: list[str] = Field(default_factory=list)

    @model_validator(mode="after")
    def _mix(self) -> "BuildingSpec":
        types = [u.type for u in self.units]
        if len(set(types)) != len(types):
            raise ValueError("units: list each unit type once and use per_floor for the count")
        total = self.units_per_floor
        if total not in TEMPLATES:
            raise ValueError(f"units: {total} flats per floor; v1 supports 2, 3 or 4 (templates T2, T3, T4)")
        if self.building.template != "auto" and self.building.template != TEMPLATES[total]:
            raise ValueError(f"building.template {self.building.template} holds {self.building.template[1]} flats per floor, "
                             f"but units give {total}; use \"auto\" or {TEMPLATES[total]}")
        return self

    @property
    def units_per_floor(self) -> int:
        return sum(u.per_floor for u in self.units)

    def canonical_json(self) -> str:
        data = self.model_dump(mode="json")
        for name, default in _LATER_STANDARDS.items():
            if data["standards"][name] == default:
                del data["standards"][name]
        return json.dumps(data, sort_keys=True, separators=(",", ":"))

    def hash(self) -> str:
        return "sha256:" + hashlib.sha256(self.canonical_json().encode("utf-8")).hexdigest()


def spec_errors(error: ValidationError) -> list[str]:
    """One plain line per problem, naming the field path, for Claude to fix."""
    lines = []
    for e in error.errors():
        path = ".".join(str(p) for p in e["loc"]) or "spec"
        message = e["msg"].removeprefix("Value error, ")
        lines.append(f"{path}: {message}")
    return lines


def parse_spec(data: dict) -> tuple[BuildingSpec | None, list[str]]:
    """Validate a spec dict. Returns the spec or the list of fixable errors."""
    try:
        return BuildingSpec.model_validate(data), []
    except ValidationError as e:
        return None, spec_errors(e)
