"""The estimate's input contract: what was counted, not how it is measured (that is the rules file's job).

Filled later from the recognised building or the Revit model (Step 9 T6/T9); tests build it by hand. Every value is
a fact from the drawings, the model or the user: an opening whose material is unknown stays ``None`` and becomes a
question in the estimate, it is never defaulted.
"""

from __future__ import annotations

from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

StoreyKind = Literal["ground", "typical", "roof", "other"]


class _Strict(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Storey(_Strict):
    """One storey of the building. ``kind`` says which drawing it comes from (typical = the repeated floor plan)."""

    index: int = Field(description="0 = ground, 1 = first storey, ...")
    name: str
    kind: StoreyKind


class ProjectInfo(_Strict):
    name: str
    location: str | None = None
    storeys: list[Storey] = Field(min_length=1)

    @model_validator(mode="after")
    def _unique_storeys(self) -> "ProjectInfo":
        idx = [s.index for s in self.storeys]
        if len(idx) != len(set(idx)):
            raise ValueError(f"storey indexes repeat: {idx}")
        return self

    def storey_indexes(self, kind: str) -> list[int]:
        """Indexes of the storeys of one kind ("any" = all), in ascending order."""
        return sorted(s.index for s in self.storeys if kind == "any" or s.kind == kind)


class Opening(_Strict):
    """``count`` openings of one type on one storey (and zone, when the plan names one, e.g. "terrace")."""

    mark: str = Field(min_length=1, description="Type mark as on the drawing (D, D1, w1), or the office's name for "
                                                "an unmarked type (e.g. Entrance) once the user confirmed it")
    kind: Literal["door", "window"]
    width_mm: float = Field(gt=0)
    height_mm: float = Field(gt=0)
    storey: int
    zone: str | None = None
    material: str | None = Field(default=None, description="wood, pvc, steel ...; None = not known (asked)")
    count: int = Field(ge=1)
    source: str | None = Field(default=None, description="Where the fact came from (sheet, entity handles, element ids)")


class Railing(_Strict):
    """``count`` railings of one length; ``storey`` None when the source gives a whole-building count."""

    kind: str = Field(min_length=1, description="stair, verandah ...")
    subkind: str | None = Field(default=None, description="e.g. main, attached")
    length_mm: float = Field(gt=0)
    count: int = Field(ge=1)
    storey: int | None = None
    source: str | None = None


class Quantities(_Strict):
    project: ProjectInfo
    openings: list[Opening] = Field(default_factory=list)
    railings: list[Railing] = Field(default_factory=list)
