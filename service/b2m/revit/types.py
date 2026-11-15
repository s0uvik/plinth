"""Type-name conversion (PRD F6.2, user story 9): the engine names logical types ("Brick 230",
"Door 900x2100"); a firm's standards file maps them to the project's own types. Unmapped names pass
through unchanged, so a project built from the B2M template needs no file at all.

standards.json, in %LOCALAPPDATA%\\B2M or given explicitly:

    {
      "walls":   {"Brick 230": "Generic - 225mm", "Brick 115": "Generic - 125mm"},
      "floors":  {"RCC 150": "Generic 150mm"},
      "doors":   {"Door 900x2100": {"family": "M_Single-Flush", "type": "0915 x 2134mm"}},
      "windows": {"Window 1200x1200": {"family": "M_Fixed", "type": "1200 x 1200mm"}}
    }
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path

from b2m import paths


@dataclass(frozen=True)
class FamilyType:
    type: str
    family: str | None = None


@dataclass(frozen=True)
class TypeMap:
    walls: dict[str, str] = field(default_factory=dict)
    floors: dict[str, str] = field(default_factory=dict)
    doors: dict[str, FamilyType] = field(default_factory=dict)
    windows: dict[str, FamilyType] = field(default_factory=dict)

    def wall(self, name: str) -> str:
        return self.walls.get(name, name)

    def floor(self, name: str) -> str:
        return self.floors.get(name, name)

    def door(self, name: str, family: str | None) -> FamilyType:
        return self.doors.get(name, FamilyType(name, family))

    def window(self, name: str, family: str | None) -> FamilyType:
        return self.windows.get(name, FamilyType(name, family))

    @classmethod
    def from_dict(cls, data: dict) -> "TypeMap":
        unknown = set(data) - {"walls", "floors", "doors", "windows"}
        if unknown:
            raise ValueError(f"standards file: unknown sections {sorted(unknown)}")

        def family_types(section: dict) -> dict[str, FamilyType]:
            out = {}
            for logical, target in section.items():
                if isinstance(target, str):
                    out[logical] = FamilyType(target)
                elif isinstance(target, dict) and isinstance(target.get("type"), str):
                    out[logical] = FamilyType(target["type"], target.get("family"))
                else:
                    raise ValueError(f"standards file: {logical!r} must map to a type name or {{family, type}}")
            return out

        return cls(walls=dict(data.get("walls", {})), floors=dict(data.get("floors", {})),
                   doors=family_types(data.get("doors", {})), windows=family_types(data.get("windows", {})))

    @classmethod
    def load(cls, path: Path | None = None) -> "TypeMap":
        """The given file, else the user's standards.json if present, else no mapping."""
        path = path or paths.root() / "standards.json"
        if not path.exists():
            return cls()
        return cls.from_dict(json.loads(path.read_text(encoding="utf-8")))
