"""Measurement rules as data: the format of ``b2m/estimate/rules/*.yaml`` and the small expression language used in it.

A rules file lists the estimate's sections in sheet order. Each section has groups; a group selects openings (or
railings), counts them per storey or in total, and emits, for every mark it selected, an optional mark heading row
and one or more measured rows. Totals sum a group or a section. Quantities, units and row placement follow from the
data; the engine has no knowledge of any particular building or office. See ``rules/office-default.yaml``.

Expressions (``number``, ``length``, ``width``, ``height``) are arithmetic over these names:

    n        count of the mark: on one storey (``count: per_storey``) or in total (``count: total``)
    storeys  number of storeys the group's ``storeys`` selector covers in this project
    W, H     opening width and height in metres;  L  railing length in metres
    and every name in the file's ``constants``.

A ``number`` given as a list of expressions is written to Excel as their product, e.g. ``[members * n, storeys]``
becomes ``=48*4``; a single expression is written as its value.
"""

from __future__ import annotations

import ast
import operator
from functools import lru_cache
from importlib.resources import files
from pathlib import Path
from typing import Any, Literal

import yaml
from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

StoreySelector = Literal["any", "ground", "typical", "roof", "other"]
UnitsOn = Literal["totals", "rows", "heading"]


class _Strict(BaseModel):
    model_config = ConfigDict(extra="forbid")


class Select(_Strict):
    """Which items a group takes. Openings: ``kind`` door/window; railings: ``railing`` (its kind)."""

    kind: Literal["door", "window"] | None = None
    railing: str | None = None
    marks: list[str] | None = None
    material: str | None = None
    zone: str = Field(default="any", description='"any", "none" (no zone) or a zone name such as terrace')
    storeys: StoreySelector = "any"

    @model_validator(mode="after")
    def _one_kind(self) -> "Select":
        if (self.kind is None) == (self.railing is None):
            raise ValueError("a selection names exactly one of kind (door/window) or railing")
        if self.railing is not None and (self.material or self.marks):
            raise ValueError("railings are selected by railing kind and storeys only")
        return self


Number = str | int | float | list[str | int | float]


class RowSpec(_Strict):
    """One measured row, emitted once per mark (or railing subkind) the group selected."""

    id: str | None = Field(default=None, description="Name used by a total's ``sum``")
    label: str
    number: Number = "n"
    length: str | float | None = None
    width: str | float | None = None
    height: str | float | None = None
    remark: str | None = None


class MarkHeading(_Strict):
    """A row above each mark's rows (e.g. ``i)W`` with the window count), not summed."""

    label: str
    number: Number | None = None
    remark: str | None = None


class TotalSpec(_Strict):
    """A total row: label in the HEIGHT column, formula in VOLUME/AREA, unit in UNITS when ``unit`` is true.

    Without ``of``: the sum of the scope's measured rows (only rows with this ``sum`` id when given).
    With ``of: previous``: the previous total of the same scope times ``times`` (an expression, e.g. storeys).
    """

    label: str
    sum: str | None = None
    of: Literal["previous"] | None = None
    times: str | float | None = None
    unit: bool = True

    @model_validator(mode="after")
    def _shape(self) -> "TotalSpec":
        if self.of and self.sum:
            raise ValueError("a total either sums rows or multiplies the previous total, not both")
        if self.of and self.times is None:
            raise ValueError("'of: previous' needs 'times'")
        return self


class GroupSpec(_Strict):
    heading: str | None = None
    select: Select
    count: Literal["per_storey", "total"] = "total"
    mark_heading: MarkHeading | None = None
    rows: list[RowSpec] = Field(min_length=1)
    totals: list[TotalSpec] = Field(default_factory=list)
    units_on: UnitsOn | None = Field(default=None, description="Overrides the section's units_on")

    @model_validator(mode="after")
    def _per_storey(self) -> "GroupSpec":
        if self.count == "per_storey" and self.select.storeys == "any":
            raise ValueError("count: per_storey needs a storeys selector (ground, typical, roof, other)")
        return self


class SectionSpec(_Strict):
    sl: int | None = None
    heading: str | None = None
    heading_on_previous_row: bool = Field(
        default=False, description="Write the heading in the ITEM NAME cell of the previous section's last row")
    units_on: UnitsOn = "totals"
    space_after: int = Field(default=0, ge=0)
    groups: list[GroupSpec] = Field(min_length=1)
    totals: list[TotalSpec] = Field(default_factory=list)


class Layout(_Strict):
    title_merge: str = "A1:I3"
    header_row: int = 4
    headers: list[str] = Field(min_length=9, max_length=9)
    column_widths: dict[str, float] = Field(default_factory=dict)
    header_row_height: float | None = None
    font: str = "Times New Roman"
    font_size: float = 11
    title_font_size: float = 24
    number_format: str = "General"
    quantity_formula: str = Field(description="Formula for a row with two or three of LENGTH/WIDTH/HEIGHT; {r} = row")
    quantity_formula_one_dimension: str = Field(description="Formula for a row with one dimension; {r} row, {col} column")


class Rules(_Strict):
    name: str
    title: str
    units: dict[Literal["length", "area", "volume"], str]
    order: list[str] = Field(default_factory=list)
    constants: dict[str, float] = Field(default_factory=dict)
    templates: dict[str, Any] = Field(default_factory=dict, description="YAML anchors only; not read by the engine")
    layout: Layout
    sections: list[SectionSpec] = Field(min_length=1)

    @field_validator("constants")
    @classmethod
    def _no_reserved(cls, v: dict[str, float]) -> dict[str, float]:
        clash = set(v) & RESERVED
        if clash:
            raise ValueError(f"constants may not be named {sorted(clash)}")
        return v


RESERVED = {"n", "storeys", "W", "H", "L"}


def load_rules(name_or_path: str | Path = "office-default") -> Rules:
    """A rules file by name from this package (``office-default``) or by path."""
    p = Path(name_or_path)
    if p.suffix.lower() in (".yaml", ".yml"):
        return Rules.model_validate(yaml.safe_load(p.read_text(encoding="utf-8")))
    return _packaged(str(name_or_path))


@lru_cache(maxsize=None)
def _packaged(name: str) -> Rules:
    text = files("b2m.estimate").joinpath("rules", f"{name}.yaml").read_text(encoding="utf-8")
    return Rules.model_validate(yaml.safe_load(text))


# ---- expressions -------------------------------------------------------------------------------------------------

_OPS = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv}


class ExpressionError(ValueError):
    pass


def evaluate(expr: str | int | float, names: dict[str, float]) -> float:
    """Value of a rules expression: numbers, names, + - * / and parentheses only."""
    if isinstance(expr, (int, float)):
        return expr
    try:
        tree = ast.parse(str(expr), mode="eval")
    except SyntaxError as e:
        raise ExpressionError(f"cannot read expression {expr!r}: {e.msg}") from None
    return _eval(tree.body, names, str(expr))


def _eval(node: ast.AST, names: dict[str, float], src: str) -> float:
    if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)) and not isinstance(node.value, bool):
        return node.value
    if isinstance(node, ast.Name):
        if node.id not in names:
            raise ExpressionError(f"unknown name {node.id!r} in {src!r} (known: {sorted(names)})")
        return names[node.id]
    if isinstance(node, ast.BinOp) and type(node.op) in _OPS:
        return _OPS[type(node.op)](_eval(node.left, names, src), _eval(node.right, names, src))
    if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub):
        return -_eval(node.operand, names, src)
    raise ExpressionError(f"only numbers, names and + - * / are allowed in {src!r}")


def expression_names(expr: str | int | float) -> set[str]:
    if isinstance(expr, (int, float)):
        return set()
    return {n.id for n in ast.walk(ast.parse(str(expr), mode="eval")) if isinstance(n, ast.Name)}
