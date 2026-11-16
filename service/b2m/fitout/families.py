"""Family matching for the fit-out (PRD Step 10, stage 2 "Families"; F1/F5): each item the layout needs is matched
to a family type at planning time, or asked about. Families: find, adapt a copy, ask; never create a family, never
edit an existing type.

    needs = families.needs_from(layout.placed)            # one need per item kind and size (+ MEP devices)
    found = families.resolve(needs, revit, answers)       # revit: anything with .call(command, params)
    found.choices[need.id]   # the family type to place (and where it comes from), or
    found.questions          # what nothing fits, asked
    found.assumptions        # template copies, size copies and size differences, listed back for review

Where it looks, in order: the open project's loaded types (get_available_family_types per category, sizes from
get_type_parameters), then Revit's installed templates for the running year (list_template_files, then
list_types_in_file per file; Revit 2026 installs no family library). A template type is only copied in when the
fit-out is built (a types_from_file op), as a new type.

How it decides, per need:
- candidates are types of the need's category whose family and type names (and description) contain one of the
  item's keywords (every word of it, any order: "kitchen sink" matches "M_Sink Kitchen-Double"); more and longer
  keywords score higher, the item's first keyword counts extra. A type with no keyword in it is never chosen on its
  own: the user may name it.
- sizes: the type's Width and Depth (or Length) parameters, else a "W x D" in its name, against the item's width
  (along its back) and depth. Within ``SIZE_TOL_MM`` it fits.
- the best keyword match wins; among equals a type that fits (the project's before a template's), then one that can
  be copied to the size, then any, the closest first. Templates are only searched when the project has no type that
  fits.
- adapt a copy: when the chosen type's size differs and the family has the size parameters (writable Width, Depth or
  Length), a copy of the type is made at the size (a family_type_copy op; the type itself is never changed);
  otherwise the closest type is used as it is and the difference is listed.
- nothing with a keyword in the category: a question (``fitout:family:<need>``) with the category's other types as
  options; the answer {"family", "type"} (any type of the category, project or template) or "skip".

Category names in the rules are Revit's English display names ("Plumbing Fixtures"); ``CATEGORIES`` maps them to
built-in categories, so matching does not depend on Revit's language. A type the user names is looked for in the
rule's category, then in any category of the project (a TV unit family drawn as Furniture), then in the templates.

Families first (``resolve_items``, used by ``to_plan.plan_furniture``): before the layout, a family type is chosen per
rule item and each project type is measured (``Measurer``: measurements given, e.g. a saved file, else the add-in's
measure_family_types, which places one unrotated instance per type at a level's origin, reads its box and rolls back;
cached per document and type). The layout then uses the family's real footprint (its smaller types when it does not
fit), and the plan inserts it from its measured box. A measurement's ``base_offset_mm`` makes a Revit quirk explicit:
some level-based families take the point's z (the level's elevation) as an offset above the level, so their box
starts that far up; the family's own base is the box less it.
"""

from __future__ import annotations

import json
import math
import re
from pathlib import Path
from typing import Any, Iterable, Literal, Protocol

from pydantic import BaseModel, ConfigDict

from b2m.fitout.rooms import Assumption, Question

SIZE_TOL_MM = 50.0  # a type within this of the item's width and depth fits it
TEMPLATE_PREFERENCE = ("residential", "default", "systems", "plumbing", "electrical", "mechanical", "multi-discipline",
                       "architectural", "construction", "structural")
LIST_TIMEOUT_SECONDS = 600  # opening a template for the first time takes a while (the add-in caches the listing)
MAX_OPTIONS = 12  # other types offered in a question
COPY_SUFFIX = "(B2M)"

# Revit category display names (English, as the rule sets write them) -> built-in categories.
CATEGORIES = {
    "furniture": "OST_Furniture", "furniture systems": "OST_FurnitureSystems", "casework": "OST_Casework",
    "plumbing fixtures": "OST_PlumbingFixtures", "specialty equipment": "OST_SpecialityEquipment",
    "speciality equipment": "OST_SpecialityEquipment", "lighting fixtures": "OST_LightingFixtures",
    "lighting devices": "OST_LightingDevices", "electrical fixtures": "OST_ElectricalFixtures",
    "electrical equipment": "OST_ElectricalEquipment", "mechanical equipment": "OST_MechanicalEquipment",
    "air terminals": "OST_DuctTerminal", "duct accessories": "OST_DuctAccessory", "pipe accessories": "OST_PipeAccessory",
    "fire alarm devices": "OST_FireAlarmDevices", "sprinklers": "OST_Sprinklers", "communication devices":
    "OST_CommunicationDevices", "data devices": "OST_DataDevices", "security devices": "OST_SecurityDevices",
    "telephone devices": "OST_TelephoneDevices", "nurse call devices": "OST_NurseCallDevices",
    "generic models": "OST_GenericModel", "plumbing equipment": "OST_PlumbingEquipment",
}
# MEP type kinds (list_types_in_file / copy_types_from_file) -> the project category get_available_family_types lists.
MEP_KIND_CATEGORY = {"pipe_type": "OST_PipeCurves", "pipe_system_type": "OST_PipingSystem",
                     "duct_type": "OST_DuctCurves", "duct_system_type": "OST_DuctSystem",
                     "conduit_type": "OST_Conduit", "cable_tray_type": "OST_CableTray"}
SIZE_PARAMS = {"width": "Width", "depth": "Depth", "length": "Length"}


class Revit(Protocol):
    def call(self, command: str, params: dict[str, Any] | None = None, *, timeout_seconds: int | None = None) -> Any: ...


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class Need(_Model):
    """One kind of family the fit-out places: a rule item at one size (or an MEP type for runs)."""

    id: str  # stable: "<item>" or "<item>-<w>x<d>"
    item: str
    label: str
    category: str  # built-in, e.g. OST_Furniture; for MEP types the kind (pipe_type, pipe_system_type...)
    keywords: list[str]
    width_mm: float | None = None
    depth_mm: float | None = None
    classification: str | None = None  # MEP system types: Sanitary, DomesticColdWater, SupplyAir...
    keys: list[str] = []  # the layout records (or runs) that need it

    @property
    def mep(self) -> bool:
        return self.category in MEP_KIND_CATEGORY


class Candidate(_Model):
    source: Literal["project", "template"]
    path: str | None = None  # template file
    type_id: int | None = None  # project type id, or the template's own id
    category: str
    family: str | None
    type: str
    placement: str | None = None  # OneLevelBased, OneLevelBasedHosted, WorkPlaneBased... (None: not known)
    width_mm: float | None = None
    depth_mm: float | None = None
    depth_param: str | None = None  # "Depth" or "Length": the parameter that holds the depth
    size_params: list[str] = []  # writable size parameters (project types); template types: the ones listed
    score: float = 0


Front = Literal["-y", "+y", "-x", "+x"]
# The turn (degrees, anticlockwise) that brings a family's front to -Y, the way the layout's rotation assumes.
FRONT_TURN: dict[str, float] = {"-y": 0.0, "+y": 180.0, "+x": 270.0, "-x": 90.0}
BASE_TOL_MM = 50.0  # a box this close to a level's elevation was measured with the point's z taken as an offset


def turn(p: tuple[float, float], deg: float) -> tuple[float, float]:
    """``p`` turned anticlockwise by ``deg`` about the origin."""
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return (p[0] * c - p[1] * s, p[0] * s + p[1] * c)


class Measurement(_Model):
    """A family type's box, measured on one instance placed unrotated at a level's origin (add-in command
    measure_family_types, or given): ``min``/``max`` in mm relative to the point asked for, as Revit placed it.
    ``base_offset_mm``: how far above that point Revit put the family's own origin (some level-based families take
    the point's z, the level's elevation, as an offset above the level); the family's own box is the box less it."""

    type_id: int | None = None
    family: str | None = None
    type: str
    placement: str | None = None
    min: tuple[float, float, float]
    max: tuple[float, float, float]
    base_offset_mm: float = 0.0
    facing: tuple[float, float, float] | None = None
    hand: tuple[float, float, float] | None = None
    level: str | None = None
    source: str = "measure_family_types"

    @property
    def base_mm(self) -> float:
        """The lowest point of the family below (negative) or above its own origin."""
        return self.min[2] - self.base_offset_mm

    @property
    def height_mm(self) -> float:
        return self.max[2] - self.min[2]

    def item_box(self, front: str = "-y") -> tuple[float, float, float, float]:
        """The family's plan box (x0, y0, x1, y1) turned so its front faces -Y: width along X, its back at y1."""
        xs, ys = [], []
        for x in (self.min[0], self.max[0]):
            for y in (self.min[1], self.max[1]):
                tx, ty = turn((x, y), FRONT_TURN[front])
                xs.append(tx)
                ys.append(ty)
        return (min(xs), min(ys), max(xs), max(ys))

    def size(self, front: str = "-y") -> tuple[float, float, float]:
        """Width (along its back), depth and height, mm."""
        x0, y0, x1, y1 = self.item_box(front)
        return (round(x1 - x0, 1), round(y1 - y0, 1), round(self.height_mm, 1))

    def describe(self) -> str:
        lo, hi = self.min, self.max
        text = (f"box x {lo[0]:g}..{hi[0]:g}, y {lo[1]:g}..{hi[1]:g}, z {lo[2] - self.base_offset_mm:g}.."
                f"{hi[2] - self.base_offset_mm:g} mm about its origin")
        if self.base_offset_mm:
            text += f" (measured {self.base_offset_mm:g} mm up: the family took the point's z as its offset)"
        return text


class Choice(_Model):
    """The family type a need is placed with. ``type`` is the type to place: the size copy's name when one is made
    (from ``base_type``, setting ``values_mm``). ``measured``: the type's measured box, when known (the layout then
    used its footprint, and the insertion point is worked out from it); ``front``: which way the family's front faces
    in its own coordinates (-Y, the Revit way, unless the user says otherwise)."""

    need: str
    category: str
    family: str | None
    type: str
    source: Literal["project", "template"]
    path: str | None = None  # template: copied in at build (types_from_file)
    placement: str | None = None
    kind: str = "family_type"
    base_type: str | None = None
    values_mm: dict[str, float] = {}
    width_mm: float | None = None  # of the type placed
    depth_mm: float | None = None
    differences: list[str] = []
    answered: bool = False
    type_id: int | None = None
    measured: Measurement | None = None
    front: Front = "-y"


class Resolution(_Model):
    choices: dict[str, Choice] = {}
    questions: list[Question] = []
    assumptions: list[Assumption] = []
    skipped: list[str] = []  # needs the user said to leave out
    searched: list[str] = []  # where it looked: "project", template paths

    def for_key(self, key: str, needs: list[Need]) -> Choice | None:
        for need in needs:
            if key in need.keys:
                return self.choices.get(need.id)
        return None


# ------------------------------------------------------------------------------------------------ needs

def builtin_category(name: str) -> str | None:
    """"Plumbing Fixtures" -> OST_PlumbingFixtures; an OST_ name passes through."""
    if name.startswith("OST_"):
        return name
    return CATEGORIES.get(" ".join(name.lower().split()))


def _slug(text: str) -> str:
    return re.sub(r"[^A-Za-z0-9]+", "-", text).strip("-").lower()


def _get(record: Any, name: str, default: Any = None) -> Any:
    return record.get(name, default) if isinstance(record, dict) else getattr(record, name, default)


def need_id(item: str, width: float | None, depth: float | None) -> str:
    return _slug(item) + (f"-{round(width)}x{round(depth)}" if width and depth else "")


def needs_from(records: Iterable[Any]) -> list[Need]:
    """One need per item and size, from layout records (furniture ``Placed`` or MEP device records alike: key, item,
    label, category, keywords, width_mm, depth_mm). Sized items group by their rounded size."""
    grouped: dict[str, dict[str, Any]] = {}
    for record in records:
        category = builtin_category(_get(record, "category") or "")
        if category is None:
            raise ValueError(f"{_get(record, 'key')}: unknown Revit category {_get(record, 'category')!r}")
        item = _get(record, "item") or _get(record, "label") or category
        width, depth = _get(record, "width_mm"), _get(record, "depth_mm")
        nid = need_id(item, width, depth)
        entry = grouped.setdefault(nid, {"id": nid, "item": item, "label": _get(record, "label") or item,
                                         "category": category, "keywords": list(_get(record, "keywords") or [item]),
                                         "width_mm": width, "depth_mm": depth, "keys": []})
        entry["keys"].append(_get(record, "key"))
    return [Need(**v) for _, v in sorted(grouped.items())]


# Run types matched when a run names no keywords of its own (Revit's metric templates: "PVC - DWV", "Default"...).
RUN_TYPE_KEYWORDS = {"pipe": ["pvc", "upvc", "pipe", "default", "standard"],
                     "duct": ["rectangular", "round", "radius elbow", "default"],
                     "conduit": ["rnc", "pvc", "conduit", "default"],
                     "cable_tray": ["cable tray", "ladder", "solid bottom", "default"]}


def run_needs(runs: Iterable[Any]) -> list[Need]:
    """The MEP types runs need (records with key, kind, system (a system classification such as Sanitary,
    DomesticColdWater or SupplyAir; pipes and ducts), optional type_keywords): one need per run type and per system."""
    grouped: dict[str, dict[str, Any]] = {}
    for run in runs:
        kind, key = _get(run, "kind"), _get(run, "key")
        keywords = list(_get(run, "type_keywords") or RUN_TYPE_KEYWORDS[kind])
        nid = _slug(f"{kind}-type-{'-'.join(keywords)}")
        grouped.setdefault(nid, {"id": nid, "item": f"{kind} type", "label": f"{kind.replace('_', ' ')} type",
                                 "category": f"{kind}_type", "keywords": keywords, "keys": []})["keys"].append(key)
        system = _get(run, "system")
        if kind in ("pipe", "duct"):
            if not system:
                raise ValueError(f"{key}: a {kind} run needs its system (a system classification)")
            words = re.sub(r"([a-z])([A-Z])", r"\1 \2", system)
            nid = _slug(f"{kind}-system-{system}")
            grouped.setdefault(nid, {"id": nid, "item": f"{kind} system", "label": f"{words} system",
                                     "category": f"{kind}_system_type", "keywords": [words], "classification": system,
                                     "keys": []})["keys"].append(key)
    return [Need(**v) for _, v in sorted(grouped.items())]


# ------------------------------------------------------------------------------------------------ matching

def _words(text: str) -> set[str]:
    spaced = re.sub(r"([a-z])([A-Z])", r"\1 \2", text or "")
    out = set()
    for word in re.split(r"[^a-z0-9]+", spaced.lower()):
        if not word:
            continue
        out.add(word[:-1] if len(word) > 3 and word.endswith("s") and not word.endswith("ss") else word)
    return out


def keyword_score(keywords: list[str], *texts: str) -> float:
    """How well a type's names match an item's keywords: each keyword whose words all appear counts its word count,
    the item's first keyword half a point more. 0: no keyword in it."""
    words: set[str] = set()
    for text in texts:
        words |= _words(text or "")
    score = 0.0
    for i, keyword in enumerate(keywords):
        kw = _words(keyword)
        if kw and kw <= words:
            score += len(kw) + (0.5 if i == 0 else 0)
    return score


SIZE_IN_NAME = re.compile(r"(\d{3,4}(?:\.\d+)?)\s*(?:mm)?\s*[xX×]\s*(\d{3,4}(?:\.\d+)?)")


def _size_from_name(name: str) -> tuple[float | None, float | None]:
    m = SIZE_IN_NAME.search(name or "")
    return (float(m.group(1)), float(m.group(2))) if m else (None, None)


def _miss(need: Need, c: Candidate) -> float | None:
    """How far the type's size is from the item's (the larger of the width and depth differences), None when either
    is not known (an item without a size fits any)."""
    if need.width_mm is None or need.depth_mm is None:
        return 0.0
    if c.width_mm is None or c.depth_mm is None:
        return None
    return max(abs(c.width_mm - need.width_mm), abs(c.depth_mm - need.depth_mm))


def _sizable(need: Need, c: Candidate) -> dict[str, float] | None:
    """The size parameters to set on a copy of the type to make it the item's size, when the family has them."""
    if need.width_mm is None or need.depth_mm is None or c.width_mm is None or c.depth_mm is None:
        return None
    values: dict[str, float] = {}
    if abs(c.width_mm - need.width_mm) > SIZE_TOL_MM:
        if "Width" not in c.size_params:
            return None
        values["Width"] = float(round(need.width_mm))
    if abs(c.depth_mm - need.depth_mm) > SIZE_TOL_MM:
        if not c.depth_param or c.depth_param not in c.size_params:
            return None
        values[c.depth_param] = float(round(need.depth_mm))
    return values or None


def _rank(need: Need, c: Candidate) -> tuple:
    miss = _miss(need, c)
    fit = 0 if miss is not None and miss <= SIZE_TOL_MM else 1 if _sizable(need, c) else 2
    # A type that fits: the project's first. One to adapt or use as it is: the closest first (the least changed).
    closeness = 0 if fit == 0 else (miss if miss is not None else 1e9)
    return (-c.score, fit, closeness, 0 if c.source == "project" else 1, c.family or "", c.type)


def _dims(dims: dict[str, Any], name: str) -> tuple[float | None, float | None, str | None]:
    """Width, depth and the parameter holding the depth (Depth, else Length) of a type; a size missing from its
    parameters is read from its name ("1525x2032mm"), which no parameter then holds."""
    width = dims.get("width")
    if dims.get("depth") is not None:
        depth, param = dims["depth"], "Depth"
    elif dims.get("length") is not None:
        depth, param = dims["length"], "Length"
    else:
        depth, param = None, None
    if width is None or depth is None:
        w, d = _size_from_name(name)
        width = w if width is None else width
        depth = d if depth is None else depth
    return width, depth, param


class _Search:
    """One resolve pass: project and template listings fetched once per category and file."""

    def __init__(self, revit: Revit, year: int | str | None, language: str | None, templates: list[str] | None):
        self.revit = revit
        self.year, self.language = year, language
        self._templates = templates
        self._project: dict[str, list[dict[str, Any]]] = {}
        self._params: dict[int, dict[str, Any]] = {}
        self._files: dict[str, dict[str, Any]] = {}
        self.searched: list[str] = []

    # -- project

    def project_rows(self, category: str) -> list[dict[str, Any]]:
        if category not in self._project:
            bic = MEP_KIND_CATEGORY.get(category, category)
            listing = self.revit.call("get_available_family_types", {"builtInCategory": bic})
            self._project[category] = [t for g in listing.get("categories") or [] for t in g.get("types") or []]
            if "project" not in self.searched:
                self.searched.append("project")
        return self._project[category]

    def project_all(self) -> list[dict[str, Any]]:
        """Every loadable family type in the project, each row with its builtInCategory (a type the user names may
        be in another category than the rule's: a TV unit drawn as Furniture, not Casework)."""
        if "*" not in self._project:
            listing = self.revit.call("get_available_family_types", {})
            self._project["*"] = [{**t, "builtInCategory": g.get("builtInCategory")}
                                  for g in listing.get("categories") or [] for t in g.get("types") or []]
        return self._project["*"]

    def project_candidates(self, need: Need, rows: list[dict[str, Any]] | None = None, scored: bool = True) -> list[Candidate]:
        rows = self.project_rows(need.category) if rows is None else rows
        if need.mep:
            return [Candidate(source="project", type_id=r["typeId"], category=need.category, family=None,
                              type=r["typeName"], score=self._mep_score(need, r["typeName"], None))
                    for r in rows if not scored or self._mep_score(need, r["typeName"], None) > 0]
        picked = [(r, keyword_score(need.keywords, r.get("familyName", ""), r.get("typeName", ""))) for r in rows]
        picked = [(r, s) for r, s in picked if s > 0 or not scored]
        self._load_params([r["typeId"] for r, _ in picked])
        out = []
        for r, s in picked:
            params = {p["name"].lower(): p for p in self._params.get(r["typeId"], {}).get("parameters") or []
                      if p.get("storageType") == "Double" and p.get("name", "").lower() in SIZE_PARAMS}
            dims = {k: v["value"] for k, v in params.items() if isinstance(v.get("value"), (int, float))}
            width, depth, param = _dims(dims, r["typeName"])
            writable = [SIZE_PARAMS[k] for k, v in params.items() if not v.get("isReadOnly")]
            out.append(Candidate(source="project", type_id=r["typeId"], category=need.category, family=r.get("familyName"),
                                 type=r["typeName"], width_mm=width, depth_mm=depth, depth_param=param,
                                 size_params=writable, score=s))
        return out

    def _load_params(self, type_ids: list[int]) -> None:
        wanted = [i for i in dict.fromkeys(type_ids) if i not in self._params]
        for start in range(0, len(wanted), 200):
            result = self.revit.call("get_type_parameters", {"typeIds": wanted[start:start + 200]})
            for row in result.get("types") or []:
                self._params[row["typeId"]] = row
        for i in wanted:
            self._params.setdefault(i, {})

    # -- templates

    def template_paths(self) -> list[str]:
        if self._templates is None:
            params: dict[str, Any] = {}
            if self.year is not None:
                params["year"] = int(self.year)
            listing = self.revit.call("list_template_files", params)
            files = listing.get("files") or []
            languages = [f.get("language") for f in files]
            language = self.language or ("English" if "English" in languages else (languages[0] if languages else None))
            files = [f for f in files if f.get("language") == language]

            def rank(f: dict[str, Any]) -> tuple:
                name = f["name"].lower()
                return (next((i for i, p in enumerate(TEMPLATE_PREFERENCE) if name.startswith(p)), len(TEMPLATE_PREFERENCE)),
                        0 if "metric" in name else 1, name)
            self._templates = [f["path"] for f in sorted(files, key=rank)]
        return self._templates

    def template_listing(self, path: str) -> dict[str, Any]:
        if path not in self._files:
            self._files[path] = self.revit.call("list_types_in_file", {"path": path}, timeout_seconds=LIST_TIMEOUT_SECONDS)
            self.searched.append(path)
        return self._files[path]

    def template_candidates(self, need: Need, scored: bool = True) -> list[Candidate]:
        out = []
        for path in self.template_paths():
            listing = self.template_listing(path)
            if need.mep:
                for r in (listing.get("mep_types") or {}).get(need.category) or []:
                    score = self._mep_score(need, r["name"], r.get("classification"))
                    if score > 0 or not scored:
                        out.append(Candidate(source="template", path=path, type_id=r.get("id"), category=need.category,
                                             family=None, type=r["name"], score=score))
                continue
            for r in listing.get("family_types") or []:
                if r.get("category") != need.category:
                    continue
                score = keyword_score(need.keywords, r.get("family", ""), r.get("type", ""), r.get("description", ""))
                if score <= 0 and scored:
                    continue
                width, depth, param = _dims(r.get("dims_mm") or {}, r.get("type", ""))
                listed = [SIZE_PARAMS[k] for k in (r.get("dims_mm") or {}) if k in SIZE_PARAMS]
                out.append(Candidate(source="template", path=path, type_id=r.get("id"), category=need.category,
                                     family=r.get("family"), type=r["type"], placement=r.get("placement"),
                                     width_mm=width, depth_mm=depth, depth_param=param, size_params=listed, score=score))
        return out

    @staticmethod
    def _mep_score(need: Need, name: str, classification: str | None) -> float:
        score = keyword_score(need.keywords, name)
        if need.classification:
            if classification is not None:
                return score + 10 if classification.lower() == need.classification.lower() else 0
            score += keyword_score([re.sub(r"([a-z])([A-Z])", r"\1 \2", need.classification)], name) * 2
        return score


# ------------------------------------------------------------------------------------------------ resolve

def resolve(needs: list[Need], revit: Revit, answers: dict[str, Any] | None = None, *, year: int | str | None = None,
            language: str | None = None, templates: list[str] | None = None, search: "_Search | None" = None) -> Resolution:
    """Match every need to a family type (or MEP type), adapt a copy for a size, or ask. ``templates``: the template
    files to search instead of the installed ones for the running Revit year (``year`` for another installed year,
    ``language`` for another language folder). An answer is looked up by the need's question id, then by its item
    (``fitout:family:<item>``, so one answer serves every size of an item)."""
    answers = answers or {}
    search = search or _Search(revit, year, language, templates)
    choices: dict[str, Choice] = {}
    questions: list[Question] = []
    assumptions: list[Assumption] = []
    skipped: list[str] = []
    for need in needs:
        answer = answer_for(need, answers)
        if answer == "skip":
            skipped.append(need.id)
            continue
        if isinstance(answer, dict):
            chosen = _answered(need, answer, search)
            if chosen is None:
                questions.append(_question(need, search, f"The family type given for {need.label} "
                                                         f"({answer.get('family')}: {answer.get('type')}) is not in the "
                                                         f"project nor in the installed templates."))
                continue
            candidates = [chosen]
        else:
            candidates = search.project_candidates(need)
            if not any(_fits(need, c) for c in candidates):
                candidates += search.template_candidates(need)
        if not candidates:
            questions.append(_question(need, search))
            continue
        best = min(candidates, key=lambda c: _rank(need, c))
        choice, notes = _choose(need, best, answered=isinstance(answer, dict))
        choices[need.id] = choice
        assumptions.extend(notes)
    return Resolution(choices=choices, questions=questions, assumptions=assumptions, skipped=skipped,
                      searched=search.searched)


def question_id(need: Need) -> str:
    return f"fitout:family:{need.id}"


def answer_for(need: Need, answers: dict[str, Any]) -> Any:
    """The user's answer for a need: by its question id, else by its item ("fitout:family:bed" answers every bed
    size; the item's id as the rules write it, "side_table", or slugged, "side-table")."""
    for key in (question_id(need), f"fitout:family:{need.item}", f"fitout:family:{_slug(need.item)}"):
        if key in answers:
            return answers[key]
    return None


def _fits(need: Need, c: Candidate) -> bool:
    miss = _miss(need, c)
    return miss is not None and miss <= SIZE_TOL_MM


def _answered(need: Need, answer: dict[str, Any], search: _Search) -> Candidate | None:
    family, type_ = answer.get("family"), answer.get("type") or next(iter(answer.get("types") or []), None)
    if not type_:
        return None
    pool = search.project_candidates(need, scored=False)
    if not any(_same(c, family, type_) for c in pool) and not need.mep:
        # the type named may be in another category (a TV unit family drawn as Furniture): the project's, any category
        rows = [r for r in search.project_all() if r.get("typeName", "").casefold() == type_.casefold()
                and (family is None or (r.get("familyName") or "").casefold() == family.casefold())]
        for r in rows[:1]:
            if r.get("builtInCategory"):
                other = need.model_copy(update={"category": r["builtInCategory"]})
                pool += search.project_candidates(other, rows=[r], scored=False)
    if not any(_same(c, family, type_) for c in pool):
        pool += search.template_candidates(need, scored=False)
    if answer.get("path"):
        pool = [c for c in pool if c.source == "project" or c.path == answer["path"]]
    return next((c for c in pool if _same(c, family, type_)), None)


def _same(c: Candidate, family: str | None, type_: str) -> bool:
    return c.type.casefold() == type_.casefold() and (family is None or (c.family or "").casefold() == family.casefold())


def _size(w: float | None, d: float | None) -> str:
    return f"{round(w)} x {round(d)}" if w is not None and d is not None else "unknown size"


def _choose(need: Need, c: Candidate, answered: bool) -> tuple[Choice, list[Assumption]]:
    kind = "family_type" if not need.mep else need.category
    base = dict(need=need.id, category=c.category if not need.mep else MEP_KIND_CATEGORY.get(need.category, need.category),
                family=c.family, source=c.source, path=c.path, placement=c.placement, kind=kind, answered=answered,
                type_id=c.type_id if c.source == "project" else None)
    notes: list[Assumption] = []
    where = "the project" if c.source == "project" else (c.path or "").rsplit("\\", 1)[-1]
    name = f"{c.family}: {c.type}" if c.family else c.type
    if c.source == "template":
        notes.append(Assumption(id=f"fitout:family-source:{need.id}", text=f"{need.label}: {name} from {where}, copied "
                                f"into the project as a new type", value={"family": c.family, "type": c.type, "path": c.path},
                                source="installed template", instances=need.keys))
    miss = _miss(need, c)
    if miss is not None and miss <= SIZE_TOL_MM:
        return Choice(type=c.type, width_mm=c.width_mm, depth_mm=c.depth_mm, **base), notes
    values = _sizable(need, c)
    if values:
        copy_name = f"{_size(need.width_mm, need.depth_mm).replace(' ', '')}mm {COPY_SUFFIX}"
        notes.append(Assumption(id=f"fitout:family-copy:{need.id}", text=f"{need.label}: a copy of {name} "
                                f"({_size(c.width_mm, c.depth_mm)}) made at {_size(need.width_mm, need.depth_mm)} as "
                                f"{copy_name!r}; {c.type!r} itself is not changed", value=values,
                                source="family match", instances=need.keys))
        return Choice(type=copy_name, base_type=c.type, values_mm=values, width_mm=need.width_mm, depth_mm=need.depth_mm,
                      **base), notes
    differences = []
    if need.width_mm is not None and need.depth_mm is not None:
        if c.width_mm is None or c.depth_mm is None:
            differences.append(f"size of {name} not known; needed {_size(need.width_mm, need.depth_mm)}")
        else:
            for label, have, want in (("width", c.width_mm, need.width_mm), ("depth", c.depth_mm, need.depth_mm)):
                if abs(have - want) > SIZE_TOL_MM:
                    differences.append(f"{label} {round(have)} instead of {round(want)}")
    if differences:
        notes.append(Assumption(id=f"fitout:family-size:{need.id}", text=f"{need.label}: the closest type, {name} in "
                                f"{where}, is used as it is ({'; '.join(differences)}); its family has no size "
                                f"parameters to copy it to the size", value={"family": c.family, "type": c.type,
                                                                              "differences": differences},
                                source="family match", instances=need.keys))
    return Choice(type=c.type, width_mm=c.width_mm, depth_mm=c.depth_mm, differences=differences, **base), notes


def _question(need: Need, search: _Search, why: str | None = None) -> Question:
    """Nothing with the item's keywords: ask, offering the category's other types (project first)."""
    others = search.project_candidates(need, scored=False) + search.template_candidates(need, scored=False)
    options: list[Any] = []
    for c in others[:MAX_OPTIONS]:
        option = {"family": c.family, "type": c.type, "source": c.source}
        if c.path:
            option["path"] = c.path
        options.append(option)
    options.append("skip")
    size = f" ({_size(need.width_mm, need.depth_mm)} mm)" if need.width_mm and need.depth_mm else ""
    text = why or (f"No family type for {need.label}{size} was found in the project or in Revit's installed templates "
                   f"(looked for {', '.join(need.keywords)} in {need.category}). Choose a type to use (it may be "
                   f"copied to the size), load a family into the project and plan again, or leave it out.")
    return Question(id=question_id(need), topic="families", text=text, options=options, instances=need.keys)


# ------------------------------------------------------------------------------------------------ measurements

MEASURE_COMMAND = "measure_family_types"
MEASURE_TIMEOUT_SECONDS = 300
# (document title, type id) -> measurement, for this process: a type is measured once per document.
MEASURED: dict[tuple[str, int], Measurement] = {}


def parse_measurement(row: dict[str, Any], source: str = MEASURE_COMMAND,
                      level_elevations: Iterable[float] = ()) -> Measurement:
    """A measure_family_types row (or one in its shape). A row without ``base_offset_mm`` whose box starts within
    ``BASE_TOL_MM`` of one of ``level_elevations`` (other than 0) was measured on that level with the point's z taken
    as an offset: that elevation is its base offset."""
    lo = tuple(float(v) for v in row["min"])
    hi = tuple(float(v) for v in row["max"])
    base = row.get("base_offset_mm")
    if base is None:
        base = next((e for e in level_elevations if abs(e) > BASE_TOL_MM and abs(lo[2] - e) <= BASE_TOL_MM), 0.0)

    def vec(name: str) -> tuple[float, float, float] | None:
        v = row.get(name)
        return tuple(float(x) for x in v) if v else None

    return Measurement(type_id=row.get("type_id"), family=row.get("family"), type=row["type"],
                       placement=row.get("placement"), min=lo, max=hi, base_offset_mm=float(base), facing=vec("facing"),
                       hand=vec("hand"), level=row.get("level"), source=source)


def load_measurements(data: str | Path | dict[str, Any] | list[dict[str, Any]],
                      level_elevations: Iterable[float] = ()) -> list[Measurement]:
    """Measurements given instead of measured now: a saved measure_family_types answer or a file in its shape
    ({"types": [rows]} or [rows]). A file's own ``level_elevation_mm`` (the level it was measured on) is used for rows
    without ``base_offset_mm``; else ``level_elevations`` (the building's levels) are tried, see parse_measurement."""
    source = "measured, given"
    if isinstance(data, (str, Path)):
        path = Path(data)
        source = f"measured, from {path.name}"
        data = json.loads(path.read_text(encoding="utf-8"))
    rows = data.get("types") if isinstance(data, dict) else data
    elevations = list(level_elevations)
    if isinstance(data, dict) and data.get("level_elevation_mm") is not None:
        elevations = [float(data["level_elevation_mm"])]
    return [parse_measurement(r, source, elevations) for r in rows or []]


class Measurer:
    """Measurements per family type: the given ones first (by type id, then by family and type name), then the
    add-in's measure_family_types (one call for all, rolled back by the add-in: nothing stays in the model), cached
    per document and type. A type that cannot be measured keeps why (face-based, the add-in lacks the command...)."""

    def __init__(self, revit: Revit, given: list[Measurement] | None = None, *, document: str | None = None,
                 level: str | None = None, cache: dict[tuple[str, int], Measurement] | None = None):
        self.revit = revit
        self.given = list(given or [])
        self.document = document or ""
        self.level = level
        # the process-wide cache only for a named document (type ids are only unique within one)
        self.cache = cache if cache is not None else (MEASURED if document else {})
        self.why: dict[int, str] = {}
        self.unavailable: str | None = None

    def _given(self, type_id: int | None, family: str | None, type_: str) -> Measurement | None:
        for m in self.given:
            if type_id is not None and m.type_id == type_id:
                return m
        for m in self.given:
            if m.type.casefold() == type_.casefold() and (family is None or (m.family or "").casefold() == family.casefold()):
                return m.model_copy(update={"type_id": type_id}) if type_id is not None else m
        return None

    def measure(self, wanted: list[tuple[int | None, str | None, str]]) -> dict[tuple, Measurement]:
        """(type id, family, type) -> measurement, for those that are measured."""
        out: dict[tuple, Measurement] = {}
        ask: list[int] = []
        for w in dict.fromkeys(wanted):
            type_id, family, type_ = w
            m = self._given(type_id, family, type_)
            if m is None and type_id is not None:
                m = self.cache.get((self.document, type_id))
            if m is not None:
                out[w] = m
            elif type_id is not None:
                ask.append(type_id)
            else:
                self.why.setdefault(-1, "not in the project (a template's type is measured only once it is copied in)")
        if ask and self.unavailable is None:
            params: dict[str, Any] = {"type_ids": sorted(set(ask))}
            if self.level:
                params["level"] = self.level
            try:
                result = self.revit.call(MEASURE_COMMAND, params, timeout_seconds=MEASURE_TIMEOUT_SECONDS)
            except Exception as e:  # an older add-in without the command, or Revit refusing: lay out by the rules
                self.unavailable = f"{MEASURE_COMMAND} failed: {getattr(e, 'error', None) or e}"
            else:
                for row in result.get("types") or []:
                    m = parse_measurement(row, f"{MEASURE_COMMAND} on {result.get('level') or row.get('level') or 'a level'}")
                    if m.type_id is not None:
                        self.cache[(self.document, m.type_id)] = m
                for row in result.get("skipped") or []:
                    self.why[int(row["type_id"])] = str(row.get("reason") or "not measured")
        for w in wanted:
            if w in out or w[0] is None:
                continue
            m = self.cache.get((self.document, w[0]))
            if m is not None:
                out[w] = m
            else:
                self.why.setdefault(w[0], self.unavailable or "the add-in did not measure it")
        return out

    def reason(self, type_id: int | None) -> str:
        if type_id is None:
            return "not in the project (a template's type is measured only once it is copied in)"
        return self.why.get(type_id) or self.unavailable or "not measured"


# ------------------------------------------------------------------------------------------------ families first

class ItemFamily(_Model):
    """The family type chosen for a rule item before the layout. ``options``: the measured types the layout may use,
    preferred first (the type chosen, then the family's smaller types or the types the answer lists); each is laid
    out at its own footprint. Empty: the item is laid out at the rule's sizes (nothing measured)."""

    item: str
    need: Need
    choice: Choice
    options: list[Choice] = []


class ItemFamilies(_Model):
    items: dict[str, ItemFamily] = {}
    left_out: dict[str, str] = {}  # item -> why it is not placed at all (the user's skip, or included in another)
    questions: list[Question] = []
    asked: dict[str, str] = {}  # item -> its question's id
    no_sides: list[str] = []  # items keeping no side clearance: their family holds what stands beside them
    assumptions: list[Assumption] = []
    searched: list[str] = []

    def sizes(self) -> dict[str, list[Any]]:
        """Item -> its sizes for the layout (rules.Size), from the measured types; items without stay as the rules."""
        from b2m.fitout.rules import Size

        out = {}
        for item, fam in self.items.items():
            sizes = []
            for c in fam.options:
                w, d, h = c.measured.size(c.front)
                sizes.append(Size(width_mm=w, depth_mm=d, height_mm=max(h, 1.0)))
            if sizes:
                out[item] = sizes
        return out

    def question_for(self, item: str) -> Question | None:
        qid = self.asked.get(item)
        return next((q for q in self.questions if q.id == qid), None)


def item_needs(model: Any, rules: Any) -> list[Need]:
    """One need per rule item the model's rooms are furnished with (and the fixtures set on a counter), at the item's
    preferred size; keys: the rooms that have it. A counter is left to the layout (its legs' lengths)."""
    rooms_of: dict[str, set[str]] = {}
    for room in model.rooms:
        if not room.classes:
            continue
        for e in rules.set_for(list(room.classes)):
            item = rules.furniture.items[e.item]
            for item_id in [e.item, *item.fixtures]:
                rooms_of.setdefault(item_id, set()).add(room.key)
    needs = []
    for item_id in sorted(rooms_of):
        item = rules.furniture.items[item_id]
        if item.placement == "counter":
            continue
        category = builtin_category(item.category)
        if category is None:
            raise ValueError(f"{item_id}: unknown Revit category {item.category!r}")
        size = item.sizes[0]
        needs.append(Need(id=_slug(item_id), item=item_id, label=item.label, category=category,
                          keywords=list(item.keywords), width_mm=size.width_mm, depth_mm=size.depth_mm,
                          keys=sorted(rooms_of[item_id])))
    return needs


def resolve_items(model: Any, rules: Any, revit: Revit, answers: dict[str, Any] | None = None, *,
                  measurements: list[Measurement] | None = None, document: str | None = None, level: str | None = None,
                  cache: dict[tuple[str, int], Measurement] | None = None, year: int | str | None = None,
                  language: str | None = None, templates: list[str] | None = None,
                  search: "_Search | None" = None) -> ItemFamilies:
    """Families first: a family type per rule item (the answer ``fitout:family:<item>``, else the best match), each
    project type measured (``measurements`` given, else the add-in's measure_family_types), so the layout uses the
    chosen family's real footprint. An answer may be {"family", "type"} (the family's smaller types are tried when it
    does not fit), {"family", "types": [preferred first]}, either with "front" ("-y" the Revit way, "+y", "-x", "+x":
    which way the family's front faces in its own coordinates) and "includes" (items the family already has, as a
    bed set its side tables: not placed apart, and the side clearance they stand in is not kept beside the set), or
    "skip". Types are used as they are, never edited."""
    answers = answers or {}
    search = search or _Search(revit, year, language, templates)
    needs = item_needs(model, rules)
    found = resolve(needs, revit, answers, search=search)
    left_out: dict[str, str] = {}
    no_sides: set[str] = set()
    by_item = {n.item: n for n in needs}
    for nid in found.skipped:
        need = next(n for n in needs if n.id == nid)
        left_out[need.item] = "left out by the user's answer"
    for need in needs:
        answer = answer_for(need, answers)
        choice = found.choices.get(need.id)
        if isinstance(answer, dict) and choice is not None:
            for inc in answer.get("includes") or []:
                if inc not in rules.furniture.items:
                    raise ValueError(f"fitout:family:{need.item}: includes an unknown item {inc!r}")
                left_out[inc] = f"included in the {need.label.lower()} family ({choice.family}: {choice.type})"
                if rules.furniture.items[inc].anchor == need.item and rules.furniture.items[inc].placement == "beside":
                    no_sides.add(need.item)
    asked ={n.item: question_id(n) for n in needs
             if n.item not in left_out and any(q.id == question_id(n) for q in found.questions)}
    questions = [q for q in found.questions if q.id in asked.values()]

    # what to measure: each chosen project type and the other types of its family (or the types the answer lists)
    measurer = Measurer(revit, measurements, document=document, level=level, cache=cache)
    plans: dict[str, tuple[Choice, list[tuple[int | None, str | None, str]], str, bool]] = {}
    for need in needs:
        choice = found.choices.get(need.id)
        if choice is None or need.item in left_out:
            continue
        answer = answer_for(need, answers)
        answer = answer if isinstance(answer, dict) else {}
        front = str(answer.get("front") or "-y").lower()
        if front not in FRONT_TURN:
            raise ValueError(f"fitout:family:{need.item}: front {front!r} is not one of {', '.join(FRONT_TURN)}")
        name = choice.base_type or choice.type
        wanted = [(choice.type_id, choice.family, name)]
        listed = bool(answer.get("types"))
        if choice.source == "project":
            rows = [r for r in search.project_rows(choice.category)
                    if (r.get("familyName") or "").casefold() == (choice.family or "").casefold()]
            names = [str(t) for t in answer.get("types") or []] or [r["typeName"] for r in rows]
            for t in names:
                row = next((r for r in rows if r["typeName"].casefold() == t.casefold()), None)
                if row is not None and row["typeName"].casefold() != name.casefold():
                    wanted.append((row["typeId"], choice.family, row["typeName"]))
        plans[need.item] = (choice, wanted, front, listed)
    measured = measurer.measure([w for _, ws, _, _ in plans.values() for w in ws])

    items: dict[str, ItemFamily] = {}
    notes: list[Assumption] = []
    for item_id, (choice, wanted, front, listed) in plans.items():
        need = by_item[item_id]
        first = measured.get(wanted[0])
        options: list[Choice] = []
        if first is not None:
            pref = _measured_choice(choice, wanted[0], first, front)
            others = [(w, measured[w]) for w in wanted[1:] if w in measured]
            if not listed:  # the family's smaller types, largest first, when the chosen one does not fit
                area = _area(first, front)
                others = sorted([(w, m) for w, m in others if _area(m, front) < area - 1.0],
                                key=lambda t: (-_area(t[1], front), t[0][2]))
            options = [pref] + [_measured_choice(choice, w, m, front) for w, m in others]
            notes.append(_measured_note(need, rules.furniture.items[item_id], options))
        else:
            name = f"{choice.family}: {choice.base_type or choice.type}" if choice.family else choice.type
            notes.append(Assumption(
                id=f"fitout:family-unmeasured:{need.id}",
                text=f"{need.label}: {name} is not measured ({measurer.reason(choice.type_id if choice.source == 'project' else None)}); "
                     f"laid out at the rule's sizes, its origin taken as the middle of its back with its front towards -Y",
                value={"family": choice.family, "type": choice.base_type or choice.type}, source="family match",
                instances=need.keys))
        items[item_id] = ItemFamily(item=item_id, need=need, choice=choice, options=options)
    for item_id, why in sorted(left_out.items()):
        notes.append(Assumption(id=f"fitout:left-out:{_slug(item_id)}", text=f"{rules.furniture.items[item_id].label}: "
                                f"not placed ({why})", value=why, source="user's answer",
                                instances=sorted(by_item[item_id].keys) if item_id in by_item else []))
    for item_id in sorted(no_sides):
        item = rules.furniture.items[item_id]
        held = sorted(rules.furniture.items[i].label.lower() for i, why in left_out.items()
                      if rules.furniture.items[i].anchor == item_id)
        notes.append(Assumption(id=f"fitout:no-side-clearance:{_slug(item_id)}", text=f"{item.label}: its "
                                f"{item.clearance.sides_mm:g} mm side clearance is not kept: its family holds the "
                                f"{', '.join(held)} that stand in that space, and it is reached from its front",
                                value=0, source="user's answer", instances=by_item[item_id].keys))
    return ItemFamilies(items=items, left_out=left_out, questions=questions, asked=asked, assumptions=notes,
                        no_sides=sorted(no_sides), searched=search.searched)


def _area(m: Measurement, front: str) -> float:
    w, d, _ = m.size(front)
    return w * d


def _measured_choice(choice: Choice, wanted: tuple[int | None, str | None, str], m: Measurement, front: str) -> Choice:
    w, d, _ = m.size(front)
    return choice.model_copy(update={
        "type": wanted[2], "type_id": wanted[0], "base_type": None, "values_mm": {}, "width_mm": w, "depth_mm": d,
        "differences": [], "placement": m.placement or choice.placement, "measured": m, "front": front})


def _measured_note(need: Need, item: Any, options: list[Choice]) -> Assumption:
    first = options[0]
    m = first.measured
    w, d, h = m.size(first.front)
    rule = item.sizes[0]
    name = f"{first.family}: {first.type}" if first.family else first.type
    diff = []
    for label, have, want in (("width", w, rule.width_mm), ("depth", d, rule.depth_mm)):
        if abs(have - want) > SIZE_TOL_MM:
            diff.append(f"{label} {have:g} instead of the rule's {want:g}")
    text = (f"{need.label}: {name} laid out at its own measured size, {w:g} x {d:g} mm, {h:g} high"
            f"{' (' + '; '.join(diff) + ')' if diff else ''}, with the rule's clearances; used as it is (types are "
            f"never edited); {m.describe()}; its front taken as {first.front.upper()}")
    if len(options) > 1:
        text += "; when it does not fit: " + ", ".join(f"{c.type} ({c.width_mm:g} x {c.depth_mm:g})" for c in options[1:])
    return Assumption(id=f"fitout:family-measured:{need.id}", text=text,
                      value={"family": first.family, "types": [c.type for c in options],
                             "measurements": [c.measured.model_dump(mode="json") for c in options]},
                      source=m.source, instances=need.keys)
