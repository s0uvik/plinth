"""Fit-out rules per location profile (PRD Step 10, F2): room classes, furniture sets, MEP rule tables.

    from b2m.fitout import rules
    ruleset = rules.for_location(Location(city="Kolkata", state="West Bengal"))   # or rules.load("india-nbc2016")
    ruleset.classify("LIVING / DINING / KITCHEN")   # -> ["living", "dining", "kitchen"]
    ruleset.listing()                                # every rule value with its source, for the review page

Why rules, and why per location: the office has no furniture or MEP drawings, so the layout comes from the codes
(NBC 2016 Parts 4, 8 and 9, IS 732, IS 1172) and typical Indian residential practice for the project's location. The
location is asked first (as in the drawing import's "assume" step); its profile (``b2m.cad.assumptions``) picks the
fit-out rule set here, and every value used is listed back with its source for review.

Rule set layout (``rulesets/<id>/``, one directory per profile)::

    profile.yaml      id, name, extends (another rule set's id), profiles (b2m.cad.assumptions profile ids it serves)
    rooms.yaml        rooms: {class id: {label, names, patterns, flags..., source, verified}}
    furniture.yaml    furniture: {layout: {param: {value, source}}, items: {item id: {...}}, sets: {class id: {...}}}
    electrical.yaml   electrical: points per class, switchboard, mounting heights, distribution board, circuits, loads
    plumbing.yaml     plumbing: fixtures (fixture units, outlets), fixtures per class, stacks, water demand, tanks
    hvac.yaml         hvac: split AC per class and sizing, exhaust per class
    fire.yaml         fire: occupancy, height bands with provisions (NBC Part 4), extinguishers

A rule set that ``extends`` another is merged over it: mappings merge key by key, anything else is replaced, and a
key set to null removes the inherited entry. Every record (a room class, a furniture item, a table row) and every
scalar parameter carries ``source`` and ``verified``; ``verified`` stays false until someone checks the value against
the official text (PRD risk: rule values from secondary sources). Values here are placeholders as B2M's authors
recall them, so all are unverified.
"""

from __future__ import annotations

import math
import re
from functools import lru_cache
from importlib.resources import files
from typing import Any, Iterator, Literal

import yaml
from pydantic import BaseModel, ConfigDict, Field, model_validator

from b2m.cad.project import Location, LocationRequired

FILES = ("profile", "rooms", "furniture", "electrical", "plumbing", "hvac", "fire")


class RulesRequired(ValueError):
    """No fit-out rule set covers the project's location: ask the user which rule set to use."""


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class Sourced(_Model):
    """A rule record: its values come from ``source`` and stay unverified placeholders until ``verified``."""

    source: str = Field(min_length=1)
    verified: bool = False
    note: str | None = None


class SourcedValue(Sourced):
    value: Any


# ------------------------------------------------------------------------------------------------ rooms

class RoomClass(Sourced):
    """A kind of room, recognised from its name. ``names`` are phrases matched as whole words in the normalised name
    (the longest match wins, so "lift room" beats "lift"); ``patterns`` are regular expressions over it."""

    label: str
    names: list[str] = Field(min_length=1)
    patterns: list[str] = []
    habitable: bool = False
    wet: bool = False
    outdoor: bool = False
    in_flat: bool = True  # may belong to a flat (a stair, lift or terrace does not)
    flat_boundary: bool = False  # separates flats: rooms are in one flat when connected without passing it
    boundary_near_core: bool = False  # a lobby: common when it holds or opens onto a stair or lift, or into 2+ flats
    furnish: bool = True


# ------------------------------------------------------------------------------------------------ furniture

Placement = Literal["against_wall", "head_against_wall", "corner", "beside", "opposite", "free", "counter", "on_counter"]
Preference = Literal["window_wall", "no_window_wall", "not_door_wall", "longest_wall", "corner", "centre",
                     "far_from_door", "near_door", "exterior_wall", "under_window", "not_under_window"]


class Size(_Model):
    """Nominal size: width along the wall it backs onto, depth away from it, height."""

    width_mm: float = Field(gt=0)
    depth_mm: float = Field(gt=0)
    height_mm: float = Field(gt=0)


class Clearance(_Model):
    """Space kept free of other items in front of an item and beside it (circulation and use)."""

    front_mm: float = Field(default=0, ge=0)
    sides_mm: float = Field(default=0, ge=0)
    sides_required: int = Field(default=0, ge=0, le=2)  # how many side zones must be free (0: none needed)
    around_mm: float = Field(default=0, ge=0)  # free-standing items: all round


class Stretch(_Model):
    """A run along walls (a kitchen counter): total length between min and max, each leg of an L at least leg_min."""

    min_mm: float = Field(gt=0)
    max_mm: float = Field(gt=0)
    leg_min_mm: float = Field(gt=0)
    shapes: list[Literal["l_shape", "straight"]] = ["l_shape", "straight"]


class OnCounter(_Model):
    min_from_end_mm: float = 0  # from either end of its counter leg (and from the inner corner of an L)
    min_from_mm: dict[str, float] = {}  # item id -> least clear distance, edge to edge along the run


class Opposite(_Model):
    min_mm: float = Field(ge=0)  # clear distance from the anchor's front to this item's front
    max_mm: float = Field(gt=0)


class FurnitureItem(Sourced):
    label: str
    category: str  # Revit category for family matching (F1/F5)
    keywords: list[str] = Field(min_length=1)
    sizes: list[Size] = Field(min_length=1)  # preferred first; smaller standard sizes after it
    placement: Placement
    priority: int  # lower is placed first
    clearance: Clearance = Clearance()
    prefer: list[Preference] = []
    anchor: str | None = None  # beside / opposite: the item it is placed against
    opposite: Opposite | None = None
    stretch: Stretch | None = None
    fixtures: list[str] = []  # counter: items set on it, in order (sink before hob)
    on_counter: OnCounter | None = None
    tall: bool | None = None  # None: tall when higher than a window's sill (then kept clear of windows)
    under_door_swing: bool = False  # may lie under a door leaf's swing (an open wet area, nothing standing up)

    @model_validator(mode="after")
    def _shape(self) -> "FurnitureItem":
        if self.placement in ("beside", "opposite") and not self.anchor:
            raise ValueError(f"{self.label}: a {self.placement} item needs an anchor")
        if self.placement == "opposite" and self.opposite is None:
            raise ValueError(f"{self.label}: an opposite item needs its distance range")
        if self.placement == "counter" and self.stretch is None:
            raise ValueError(f"{self.label}: a counter needs its stretch")
        return self


class SetEntry(_Model):
    item: str
    count: int = Field(default=1, ge=1)
    optional: bool = False  # placed only if it fits ("study table if it fits"); still listed when it does not
    unless_flat_has: list[str] = []  # room classes: skip when another room of the flat has one


class FurnitureSet(Sourced):
    items: list[SetEntry] = []


class LayoutRules(_Model):
    door_swing_clearance_mm: SourcedValue
    door_approach_mm: SourcedValue
    window_zone_mm: SourcedValue
    circulation_mm: SourcedValue
    grid_mm: SourcedValue
    unknown_door_width_mm: SourcedValue


class Furniture(_Model):
    layout: LayoutRules
    items: dict[str, FurnitureItem]
    sets: dict[str, FurnitureSet]


# ------------------------------------------------------------------------------------------------ electrical

class PointRule(_Model):
    """How many points: at least ``min``, at least ``count``, and one per 1/``per_m2`` m² (rounded up)."""

    count: int = Field(default=0, ge=0)
    per_m2: float = Field(default=0, ge=0)
    min: int = Field(default=0, ge=0)

    def number(self, area_m2: float) -> int:
        return max(self.min, self.count, math.ceil(round(area_m2 * self.per_m2, 6)) if self.per_m2 else 0)


class ElectricalPoints(Sourced):
    lights: PointRule = PointRule()
    fans: PointRule = PointRule()
    sockets_6a: PointRule = PointRule()
    sockets_16a: PointRule = PointRule()
    tv: PointRule = PointRule()
    data: PointRule = PointRule()


class Switchboard(Sourced):
    side: Literal["latch"]  # beside the door on its latch (lock) side, inside the room
    height_mm: float = Field(gt=0)  # to the board's centre above finished floor
    from_jamb_mm: float = Field(ge=0)


class DistributionBoard(Sourced):
    per_flat: int = Field(ge=1)
    location: str
    height_mm: float = Field(gt=0)
    common_board: bool = True  # one more board for the common areas (stair, lobby, lift, pumps)


class Circuits(Sourced):
    lighting_max_points: int = Field(gt=0)
    lighting_max_w: float = Field(gt=0)
    power_16a_max_outlets: int = Field(gt=0)


class Electrical(_Model):
    points: dict[str, ElectricalPoints]
    switchboard: Switchboard
    mounting_mm: dict[str, SourcedValue]  # device -> height above finished floor
    distribution_board: DistributionBoard
    circuits: Circuits
    loads_w: dict[str, SourcedValue]  # device -> load assumed per point
    meter_room: SourcedValue
    supply_authority: SourcedValue | None = None


# ------------------------------------------------------------------------------------------------ plumbing

class Fixture(Sourced):
    label: str
    drainage_fu: float = Field(ge=0)
    supply_fu: float = Field(ge=0)
    outlet_mm: float = Field(gt=0)
    drains_to: Literal["soil", "waste"]
    cold: bool = True
    hot: bool = False
    furniture_item: str | None = None  # the furniture item that places it (its position comes from the layout)


class PlumbingClass(Sourced):
    fixtures: list[str]


class Stacks(Sourced):
    per_duct: list[Literal["soil", "waste", "vent", "rainwater"]]
    sizes_mm: dict[str, float]
    rainwater_roof_m2_per_pipe: float = Field(gt=0)


class WaterDemand(Sourced):
    lpcd: float = Field(gt=0)
    flushing_lpcd: float = Field(ge=0)
    persons_per_flat: float = Field(gt=0)


class Tanks(Sourced):
    overhead_days: float = Field(gt=0)
    underground_days: float = Field(gt=0)


class Plumbing(_Model):
    fixtures: dict[str, Fixture]
    classes: dict[str, PlumbingClass]
    stacks: Stacks
    water: WaterDemand
    tanks: Tanks


# ------------------------------------------------------------------------------------------------ HVAC

class AcStep(_Model):
    max_area_m2: float = Field(gt=0)
    tr: float = Field(gt=0)


class SplitAc(Sourced):
    classes: list[str]
    sizing: list[AcStep] = Field(min_length=1)  # by room area, smallest first; beyond the last: ask
    indoor: Size
    indoor_bottom_mm: float = Field(gt=0)
    outdoor: Size
    max_pipe_run_m: float = Field(gt=0)

    def tr_for(self, area_m2: float) -> float | None:
        return next((s.tr for s in self.sizing if area_m2 <= s.max_area_m2), None)


class Exhaust(Sourced):
    air_changes_per_hour: float = Field(gt=0)
    fan_dia_mm: float = Field(gt=0)
    height_mm: float = Field(gt=0)


class Hvac(_Model):
    split_ac: SplitAc
    exhaust: dict[str, Exhaust]


# ------------------------------------------------------------------------------------------------ fire

class Provision(Sourced):
    label: str


class FireBand(Sourced):
    max_height_m: float | None  # None: no upper limit
    provisions: list[str]


class Extinguishers(Sourced):
    kind: str
    per_area_m2: float = Field(gt=0)
    min_per_floor: int = Field(ge=1)


class Fire(_Model):
    occupancy: SourcedValue
    height_measure: SourcedValue
    high_rise_m: SourcedValue
    provisions: dict[str, Provision]
    bands: list[FireBand] = Field(min_length=1)
    extinguishers: Extinguishers
    authority: SourcedValue | None = None

    def band(self, height_m: float) -> FireBand:
        return next(b for b in self.bands if b.max_height_m is None or height_m <= b.max_height_m)


# ------------------------------------------------------------------------------------------------ the rule set

class RuleRow(_Model):
    path: str
    value: Any
    source: str
    verified: bool


class FitoutRules(_Model):
    id: str
    name: str
    extends: str | None = None
    profiles: list[str] = []
    rooms: dict[str, RoomClass]
    furniture: Furniture
    electrical: Electrical
    plumbing: Plumbing
    hvac: Hvac
    fire: Fire

    @model_validator(mode="after")
    def _references(self) -> "FitoutRules":
        classes, items = set(self.rooms), set(self.furniture.items)
        for cls, s in self.furniture.sets.items():
            if cls not in classes:
                raise ValueError(f"furniture set for unknown room class {cls!r}")
            for e in s.items:
                if e.item not in items:
                    raise ValueError(f"furniture set {cls}: unknown item {e.item!r}")
                unknown = set(e.unless_flat_has) - classes
                if unknown:
                    raise ValueError(f"furniture set {cls}: unknown room classes {sorted(unknown)}")
        for iid, it in self.furniture.items.items():
            for ref in [it.anchor, *it.fixtures, *(it.on_counter.min_from_mm if it.on_counter else {})]:
                if ref is not None and ref not in items:
                    raise ValueError(f"furniture item {iid}: unknown item {ref!r}")
        for cls in [*self.electrical.points, *self.plumbing.classes, *self.hvac.split_ac.classes, *self.hvac.exhaust]:
            if cls not in classes:
                raise ValueError(f"MEP rule for unknown room class {cls!r}")
        for cls, pc in self.plumbing.classes.items():
            for f in pc.fixtures:
                if f not in self.plumbing.fixtures:
                    raise ValueError(f"plumbing class {cls}: unknown fixture {f!r}")
        for fid, f in self.plumbing.fixtures.items():
            if f.furniture_item and f.furniture_item not in items:
                raise ValueError(f"fixture {fid}: unknown furniture item {f.furniture_item!r}")
        for b in self.fire.bands:
            for p in b.provisions:
                if p not in self.fire.provisions:
                    raise ValueError(f"fire band: unknown provision {p!r}")
        for pattern in (p for c in self.rooms.values() for p in c.patterns):
            re.compile(pattern)
        return self

    # ---------------------------------------------------------------- room classes from names

    def classify(self, name: str) -> list[str]:
        """The room classes a room name names, in the order written ("Stair & Lobby" -> stair, lobby); [] when no
        class matches. Each part of a combined name ("/", "&", "+", ",", "and", "cum") is matched on its own."""
        out: list[str] = []
        for part in split_name(name):
            cls = self._classify_part(part)
            if cls and cls not in out:
                out.append(cls)
        return out

    def _classify_part(self, part: str) -> str | None:
        best: tuple[int, str] | None = None
        padded = f" {part} "
        for cid, c in self.rooms.items():
            for phrase in c.names:
                p = normalise(phrase)
                if p and f" {p} " in padded and (best is None or len(p) > best[0]):
                    best = (len(p), cid)
            for pattern in c.patterns:
                m = re.search(pattern, part)
                if m and (best is None or len(m.group(0)) > best[0]):
                    best = (len(m.group(0)), cid)
        return best[1] if best else None

    def set_for(self, classes: list[str]) -> list[SetEntry]:
        """The furniture entries for a room of these classes: the classes' sets merged (an item named by several
        classes once: required if any class requires it, conditional only if every class makes it so)."""
        merged: dict[str, SetEntry] = {}
        for cls in classes:
            s = self.furniture.sets.get(cls)
            for e in (s.items if s else []):
                old = merged.get(e.item)
                if old is None:
                    merged[e.item] = e
                else:
                    merged[e.item] = SetEntry(item=e.item, count=max(old.count, e.count),
                                              optional=old.optional and e.optional,
                                              unless_flat_has=sorted(set(old.unless_flat_has) & set(e.unless_flat_has)))
        return sorted(merged.values(), key=lambda e: self.furniture.items[e.item].priority)

    # ---------------------------------------------------------------- listing

    def listing(self) -> list[RuleRow]:
        """Every rule value with its source and whether it is verified: the list read back for review."""
        return list(_rows(self.model_dump(exclude={"id", "name", "extends", "profiles"}, exclude_none=True), ""))


def _rows(data: Any, path: str, source: tuple[str, bool] | None = None) -> Iterator[RuleRow]:
    if isinstance(data, dict):
        if "source" in data:
            source = (data["source"], bool(data.get("verified")))
        for k, v in data.items():
            if k in ("source", "verified", "note"):
                continue
            yield from _rows(v, f"{path}.{k}" if path else k, source)
    elif isinstance(data, list) and data and all(isinstance(v, dict) for v in data):
        for i, v in enumerate(data):  # a list of records (fire bands): each its own source
            yield from _rows(v, f"{path}[{i}]", source)
    elif source is not None:
        yield RuleRow(path=path, value=data, source=source[0], verified=source[1])
    else:
        raise ValueError(f"rule value {path} has no source")


def normalise(name: str) -> str:
    """Lower case, words only, numbers dropped ("Bedroom 2" -> "bedroom", "W.C." -> "w c")."""
    words = re.sub(r"[^a-z0-9]+", " ", (name or "").lower()).split()
    return " ".join(w for w in words if not w.isdigit())


def split_name(name: str) -> list[str]:
    parts = re.split(r"[/&+,]|\band\b|\bcum\b", (name or "").lower())
    return [p for p in (normalise(x) for x in parts) if p]


# ------------------------------------------------------------------------------------------------ loading

def _merge(base: Any, over: Any) -> Any:
    if isinstance(base, dict) and isinstance(over, dict):
        out = dict(base)
        for k, v in over.items():
            if v is None:
                out.pop(k, None)
            else:
                out[k] = _merge(base.get(k), v)
        return out
    return over


def _read_dir(root, rid: str) -> dict:
    folder = root.joinpath(rid)
    data: dict = {}
    for name in FILES:
        f = folder.joinpath(f"{name}.yaml")
        if f.is_file():
            part = yaml.safe_load(f.read_text(encoding="utf-8")) or {}
            data = _merge(data, part)
    return data


def _raw(root) -> dict[str, dict]:
    out = {}
    for d in root.iterdir():
        if d.is_dir() and d.joinpath("profile.yaml").is_file():
            raw = _read_dir(root, d.name)
            if raw.get("id") != d.name:
                raise ValueError(f"rule set directory {d.name} has id {raw.get('id')!r}")
            out[d.name] = raw
    return out


def load_all(root=None) -> dict[str, FitoutRules]:
    """Every rule set under ``root`` (default: this package's rulesets), inheritance applied and validated."""
    if root is None:
        return dict(_package_rules())
    return _build_all(_raw(root))


@lru_cache(maxsize=1)
def _package_rules() -> tuple[tuple[str, FitoutRules], ...]:
    return tuple(_build_all(_raw(files(__package__).joinpath("rulesets"))).items())


def _build_all(raws: dict[str, dict]) -> dict[str, FitoutRules]:
    def merged(rid: str, seen: tuple = ()) -> dict:
        if rid in seen:
            raise ValueError(f"rule set {rid} extends itself")
        if rid not in raws:
            raise ValueError(f"unknown rule set {rid!r}")
        raw = raws[rid]
        if not raw.get("extends"):
            return raw
        base = merged(raw["extends"], seen + (rid,))
        out = _merge(base, raw)
        out["profiles"] = list(raw.get("profiles") or [])
        return out

    return {rid: FitoutRules.model_validate(merged(rid)) for rid in sorted(raws)}


def load(rule_set_id: str) -> FitoutRules:
    rules = load_all()
    if rule_set_id not in rules:
        raise RulesRequired(f"No fit-out rule set {rule_set_id!r}; known: {', '.join(sorted(rules))}.")
    return rules[rule_set_id]


def for_profile(profile_id: str) -> FitoutRules | None:
    """The rule set serving a location profile, else the one serving the profile it extends, and so on."""
    from b2m.cad.assumptions import profiles

    known, rules = profiles(), load_all()
    pid: str | None = profile_id
    while pid is not None:
        hit = [r for r in rules.values() if pid in r.profiles]
        if hit:
            return hit[0]
        pid = known[pid].extends if pid in known else None
    return None


def for_location(location: Location | None, country: str | None = None) -> FitoutRules:
    """The fit-out rules for the project's location: the location is asked first (LocationRequired), then its
    profile picks the rule set (RulesRequired when none covers it)."""
    from b2m.cad.assumptions import profile_for

    if location is None:
        raise LocationRequired("Fit-out rules come from the project location: ask the user for it first "
                               "(city or municipality, and state).")
    prof = profile_for(location, country)
    rules = for_profile(prof.id) if prof else None
    if rules is None:
        where = ", ".join(x for x in (location.city, location.municipality, location.state) if x)
        raise RulesRequired(f"No fit-out rule set covers {where}. Which rule set should B2M use "
                            f"({', '.join(sorted(load_all()))})?")
    return rules
