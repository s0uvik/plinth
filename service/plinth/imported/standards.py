"""Project standards of an imported drawing set (PRD Step 9, T6): the office's catalogue as this building uses it.

Where the brief engine maps its logical type names through the firm's standards file (plinth.revit.types.TypeMap), an
imported project carries its own standards, derived from the drawings and the user's answers: which wall thickness
classes exist and the wall type each becomes, the slab, chajja, raft and floor build-up types, the door and window
catalogue by mark (width from the plans, height and sill from the sections or the user), and the column and beam
sections. It is saved with the import (``project-standards.json`` beside ``project.json``) so the drawings (T10) and
the estimate (T11) use the same catalogue, and the build plan's type pre-steps are written from it.

Type names follow the add-in's own defaults ("Plinth 250mm Exterior", "Plinth 150mm Slab"), so a project built twice finds
its types the second time. They are logical names: the add-in returns the project type actually found or made, and
the runner uses that.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

from plinth.plan import TypeLayer

FILE_NAME = "project-standards.json"

# Revit's metric concrete frame families: the add-in finds them, loads them from the content library, or adapts a
# related family (ensure_family_type); the user is asked before one is built.
COLUMN_FAMILY = "M_Concrete-Rectangular-Column"
BEAM_FAMILY = "M_Concrete-Rectangular Beam"


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


def mm(value: float) -> str:
    """A length for a type name: whole millimetres without a decimal point."""
    return f"{value:g}"


class WallStandard(_Model):
    name: str
    thickness_mm: float
    function: Literal["exterior", "interior"]
    count: int = 0  # wall ops using it


class FloorStandard(_Model):
    name: str
    use: Literal["slab", "ground", "projection", "foundation"]
    thickness_mm: float
    structural: bool = True
    foundation: bool = False
    layers: list[TypeLayer] | None = None
    layer_names: list[str] = []  # as written on the drawing ("PCC", "BFS"), top first, when layers are given
    count: int = 0


class OpeningStandard(_Model):
    """One door or window type: a mark as written (normalised), or an unmarked size ("door-1000")."""

    name: str  # the logical type name the plan's door/window ops use
    category: Literal["door", "window"]
    mark: str | None = None
    width_mm: float | None = None
    height_mm: float | None = None
    sill_mm: float | None = None  # windows
    hint: str | None = None
    widths_drawn: dict[str, int] = {}  # drawn width -> openings, when they differ
    count: int = 0  # door/window ops using it (all storeys)


class FrameStandard(_Model):
    name: str
    category: Literal["column", "beam"]
    family: str
    b_mm: float
    h_mm: float
    count: int = 0


class ProjectStandards(_Model):
    walls: list[WallStandard] = []
    floors: list[FloorStandard] = []
    doors: list[OpeningStandard] = []
    windows: list[OpeningStandard] = []
    columns: list[FrameStandard] = []
    beams: list[FrameStandard] = []
    notes: list[str] = Field(default_factory=list)

    def save(self, directory: Path) -> Path:
        """Write the standards beside the import's project.json."""
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / FILE_NAME
        temp = directory / f".{FILE_NAME}.tmp"
        temp.write_text(json.dumps(self.model_dump(mode="json"), indent=2, ensure_ascii=False), encoding="utf-8")
        temp.replace(path)
        return path

    @classmethod
    def load(cls, directory: Path) -> "ProjectStandards | None":
        path = directory / FILE_NAME
        return cls.model_validate_json(path.read_text(encoding="utf-8")) if path.is_file() else None


def wall_type_name(thickness: float, function: str) -> str:
    return f"Plinth {mm(thickness)}mm {function.capitalize()}"


def floor_type_name(thickness: float, use: str, layer_names: list[str] | None = None,
                    layers: list[TypeLayer] | None = None) -> str:
    if use == "foundation":
        return f"Plinth {mm(thickness)}mm Foundation Slab"
    if layers and layer_names:
        build_up = " + ".join(f"{n} {mm(layer.thickness_mm)}" for n, layer in zip(layer_names, layers))
        return f"Plinth {mm(thickness)}mm {build_up}"
    return f"Plinth {mm(thickness)}mm Slab"


def column_type_name(b: float, h: float) -> str:
    return f"RCC Column {mm(b)}x{mm(h)}"


def beam_type_name(b: float, h: float) -> str:
    return f"RCC Beam {mm(b)}x{mm(h)}"
