"""Rule data shared by the layout engine and the compliance checker: one source for every code value."""

from __future__ import annotations

from functools import lru_cache
from importlib.resources import files

import yaml


@lru_cache(maxsize=None)
def rule_set(name: str) -> dict:
    """A rule file from this package, e.g. "nbc2016" or "kolkata"."""
    return yaml.safe_load(files(__name__).joinpath(f"{name}.yaml").read_text(encoding="utf-8"))
