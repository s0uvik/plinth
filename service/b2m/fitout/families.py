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
built-in categories, so matching does not depend on Revit's language.
"""

from __future__ import annotations

import re
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


class Choice(_Model):
    """The family type a need is placed with. ``type`` is the type to place: the size copy's name when one is made
    (from ``base_type``, setting ``values_mm``)."""

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
            language: str | None = None, templates: list[str] | None = None) -> Resolution:
    """Match every need to a family type (or MEP type), adapt a copy for a size, or ask. ``templates``: the template
    files to search instead of the installed ones for the running Revit year (``year`` for another installed year,
    ``language`` for another language folder)."""
    answers = answers or {}
    search = _Search(revit, year, language, templates)
    choices: dict[str, Choice] = {}
    questions: list[Question] = []
    assumptions: list[Assumption] = []
    skipped: list[str] = []
    for need in needs:
        answer = answers.get(question_id(need))
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


def _fits(need: Need, c: Candidate) -> bool:
    miss = _miss(need, c)
    return miss is not None and miss <= SIZE_TOL_MM


def _answered(need: Need, answer: dict[str, Any], search: _Search) -> Candidate | None:
    family, type_ = answer.get("family"), answer.get("type")
    if not type_:
        return None
    pool = search.project_candidates(need, scored=False)
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
    base = dict(need=need.id, category=need.category if not need.mep else MEP_KIND_CATEGORY.get(need.category, need.category),
                family=c.family, source=c.source, path=c.path, placement=c.placement, kind=kind, answered=answered)
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
