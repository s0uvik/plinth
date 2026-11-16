"""Location profiles and the "assume" step (PRD Step 9, T5).

When the user says "assume", B2M first needs the project location (``LocationRequired`` otherwise), then picks the
location's profile (``profiles/*.yaml``: the state's profile over the national one; ``ProfileRequired`` when no
profile covers the location, so the user is asked which profile or values to use), answers each open question the
profile has a value for, records every assumption with its basis, and lists them back together with the questions
it could not assume and why.

Profile format (YAML, one file per profile)::

    id, name, extends (another profile's id, whose rule sets, values and not_assumable it inherits)
    country, states, state_aliases          # which locations it covers
    rule_sets: [{id, name, data, source, basis}]   # data: the b2m.rules file, or null when B2M has no data
    governing: {places: [{places: [...], use: [ids]}], default: [ids]}  # rule sets for a location
    values: {key: {value, source, basis, note}}  # source null -> basis must say "office practice, confirm"
    not_assumable: {topic: reason}
    places: [{name, aliases}]                # town names, only to suggest a location from drawing text

A value without a published source is "office practice, confirm": the assumption's text says so, and the
readiness report asks the user to confirm it.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from functools import lru_cache
from importlib.resources import files
from typing import Any, Iterable

import yaml

from b2m.cad.project import Assumption, ImportProject, Location, LocationRequired

OFFICE = "office practice, confirm"


class ProfileRequired(ValueError):
    """No profile covers the project's location: ask the user which profile (or which values) to assume from."""


class ProfileError(ValueError):
    """A profile file breaks the format (e.g. a value without its basis)."""


@dataclass
class RuleSetRef:
    id: str
    name: str
    data: str | None
    source: str | None
    basis: str


@dataclass
class Value:
    key: str
    value: Any
    source: str | None
    basis: str
    note: str | None = None

    @property
    def confirm(self) -> bool:
        return self.source is None


@dataclass
class Place:
    name: str
    aliases: list[str] = field(default_factory=list)


@dataclass
class Profile:
    id: str
    name: str
    country: str | None
    states: list[str]
    state_aliases: list[str]
    rule_sets: dict[str, RuleSetRef]
    governing: dict
    values: dict[str, Value]
    not_assumable: dict[str, str]
    places: list[Place]
    extends: str | None = None


def _norm(s: str | None) -> str:
    return re.sub(r"[^a-z0-9]+", " ", (s or "").lower()).strip()


def _load_raw() -> dict[str, dict]:
    out = {}
    for f in files(__name__).joinpath("profiles").iterdir():
        if f.name.endswith((".yaml", ".yml")):
            raw = yaml.safe_load(f.read_text(encoding="utf-8"))
            out[raw["id"]] = raw
    return out


def _value(key: str, raw: dict, profile: str) -> Value:
    if not isinstance(raw, dict) or "value" not in raw:
        raise ProfileError(f"{profile}: value {key} needs value, source and basis")
    basis = (raw.get("basis") or "").strip()
    source = raw.get("source")
    if not basis:
        raise ProfileError(f"{profile}: value {key} has no basis")
    if not source and not basis.lower().startswith(OFFICE):
        raise ProfileError(f"{profile}: value {key} has no source, so its basis must say \"{OFFICE}\"")
    return Value(key, raw["value"], source or None, basis, raw.get("note"))


@lru_cache(maxsize=None)
def profiles() -> dict[str, Profile]:
    raws = _load_raw()

    def build(pid: str, seen: tuple = ()) -> Profile:
        if pid in seen:
            raise ProfileError(f"profile {pid} extends itself")
        raw = raws[pid]
        base = build(raw["extends"], seen + (pid,)) if raw.get("extends") else None
        rule_sets = dict(base.rule_sets) if base else {}
        for r in raw.get("rule_sets") or []:
            if not r.get("basis"):
                raise ProfileError(f"{pid}: rule set {r.get('id')} has no basis")
            rule_sets[r["id"]] = RuleSetRef(r["id"], r["name"], r.get("data"), r.get("source"), r["basis"])
        values = dict(base.values) if base else {}
        for k, v in (raw.get("values") or {}).items():
            values[k] = _value(k, v, pid)
        governing = raw.get("governing") or (base.governing if base else {})
        for ids in [governing.get("default", [])] + [g.get("use", []) for g in governing.get("places", [])]:
            for i in ids:
                if i not in rule_sets:
                    raise ProfileError(f"{pid}: governing names unknown rule set {i}")
        return Profile(
            id=pid, name=raw["name"], country=raw.get("country") or (base.country if base else None),
            states=list(raw.get("states") or []), state_aliases=list(raw.get("state_aliases") or []),
            rule_sets=rule_sets, governing=governing, values=values,
            not_assumable={**(base.not_assumable if base else {}), **(raw.get("not_assumable") or {})},
            places=[Place(p["name"], list(p.get("aliases") or [])) for p in raw.get("places") or []],
            extends=raw.get("extends"))

    return {pid: build(pid) for pid in raws}


def profile_for(location: Location | None, country: str | None = None) -> Profile | None:
    """The most specific profile covering the location: by state (fewest states wins), else by a known town; a
    country, when given, must be the profile's."""
    if location is None:
        return None
    state = _norm(location.state)
    town = {_norm(location.city), _norm(location.municipality)} - {""}
    cands = []
    for p in profiles().values():
        if country and p.country and _norm(country) != _norm(p.country):
            continue
        names = {_norm(s) for s in p.states + p.state_aliases}
        if state and state in names:
            cands.append(p)
        elif not state and any(_norm(n) in town for pl in p.places for n in [pl.name, *pl.aliases]):
            cands.append(p)
    return min(cands, key=lambda p: len(p.states)) if cands else None


def governing(location: Location | None, country: str | None = None) -> tuple[Profile, list[RuleSetRef]] | None:
    """The rule sets that govern the location, by its profile."""
    p = profile_for(location, country)
    if p is None:
        return None
    town = {_norm(location.city), _norm(location.municipality)} - {""}
    known = {_norm(n): pl.name for pl in p.places for n in [pl.name, *pl.aliases]}
    town_names = {_norm(known.get(t, t)) for t in town}
    for g in p.governing.get("places", []):
        if town_names & {_norm(n) for n in g.get("places", [])}:
            return p, [p.rule_sets[i] for i in g["use"]]
    return p, [p.rule_sets[i] for i in p.governing.get("default", [])]


# ---------------------------------------------------------------- location suggestions

@dataclass
class Suggestion:
    place: str
    state: str | None
    profile: str
    mentions: list[tuple[str, str, str]]  # (sheet, handle, text as written)

    @property
    def label(self) -> str:
        return f"{self.place}, {self.state}" if self.state else self.place


def suggest_locations(texts: Iterable[tuple[str, str, str]]) -> list[Suggestion]:
    """Towns the profiles know, named in the given drawing texts (sheet, handle, text): suggestions only."""
    texts = list(texts)
    found: dict[str, Suggestion] = {}
    for p in profiles().values():
        state = p.states[0] if len(p.states) == 1 else None
        for pl in p.places:
            for n in [pl.name, *pl.aliases]:
                pat = re.compile(r"(?<![A-Z0-9])" + re.escape(n.upper()) + r"(?![A-Z0-9])")
                for sheet, handle, text in texts:
                    if pat.search(" ".join((text or "").upper().split())):
                        s = found.setdefault(f"{p.id}:{pl.name}", Suggestion(pl.name, state, p.id, []))
                        if (sheet, handle, text) not in s.mentions:
                            s.mentions.append((sheet, handle, text))
    return sorted(found.values(), key=lambda s: (-len(s.mentions), s.place))


# ---------------------------------------------------------------- assume

@dataclass
class AssumeResult:
    profile: str
    assumed: list[Assumption] = field(default_factory=list)
    not_assumed: list[tuple[str, str]] = field(default_factory=list)  # (question id, why)

    def lines(self) -> list[str]:
        out = [f"Assumed from the {self.profile} profile:"]
        out += [f"  {a.id} ({a.question}): {a.text}  [basis: {a.basis}]" for a in self.assumed]
        if self.not_assumed:
            out.append("Not assumed (your answer is needed):")
            out += [f"  {q}: {why}" for q, why in self.not_assumed]
        return out


def assume(project: ImportProject, question_ids: Iterable[str] | str | None = "all") -> AssumeResult:
    """Answer open questions from the location's profile, recording each assumption with its basis."""
    from b2m.cad import answers  # answers does not import this module

    if project.inputs.location is None:
        raise LocationRequired("Cannot assume anything yet: ask the user for the project location first "
                               "(city or municipality, and state); assumptions come from that location's profile.")
    prof = profile_for(project.inputs.location, project.inputs.values.get("country"))
    if prof is None:
        loc = project.inputs.location
        raise ProfileRequired(
            f"No location profile covers {', '.join(x for x in (loc.city, loc.municipality, loc.state) if x)}. "
            f"Which profile should B2M assume from ({', '.join(sorted(profiles()))}), or which values?")
    from b2m.cad.questions import sync_inputs

    sync_inputs(project)  # the rule-set question follows the location
    wanted = None if question_ids in ("all", None) else set(question_ids)
    result = AssumeResult(profile=prof.name)
    for q in list(project.questions):
        if not q.open or (wanted is not None and q.id not in wanted):
            continue
        if wanted is None and q.severity == "info":
            continue
        how = q.key.get("assume")
        made = _assumption(prof, q, how) if how else None
        if made is None:
            topic = q.key.get("not_assumable") or q.topic
            result.not_assumed.append((q.id, prof.not_assumable.get(topic)
                                       or "not a location default: it needs your answer"))
            continue
        value, text, basis = made
        answers.answer(project, q.id, value, by="assumed", basis=basis, assumption=text)
        result.assumed.append(project.assumptions[-1])
    return result


def _basis(prof: Profile, *vals: Value) -> str:
    parts = []
    for v in vals:
        part = v.basis if not v.source else f"{v.source}; {v.basis}"
        parts.append(f"{v.key}: {part}" + (f" ({v.note})" if v.note else ""))
    return f"{prof.name} profile - " + "; ".join(parts)


def _say(v: Value) -> str:
    return f" ({OFFICE})" if v.confirm else f" ({v.source})"


def _assumption(prof: Profile, q, how: str):
    """(answer, assumption text, basis) for the question from the profile, or None."""
    vals = prof.values
    if how == "door_height_mm" and "door_height_mm" in vals:
        v = vals["door_height_mm"]
        return ({"height_mm": float(v.value)},
                f"Doors marked {q.key.get('mark')} are {v.value:g} mm high{_say(v)}.", _basis(prof, v))
    if how == "window" and {"window_sill_mm", "window_head_mm"} <= set(vals):
        s, h = vals["window_sill_mm"], vals["window_head_mm"]
        return ({"sill_mm": float(s.value), "head_mm": float(h.value)},
                f"Windows marked {q.key.get('mark')}: sill {s.value:g} mm, head {h.value:g} mm above the floor"
                f"{_say(s) if s.confirm or h.confirm else _say(h)}.", _basis(prof, s, h))
    if how in ("wall_height", "lintel") and how in vals:
        v = vals[how]
        return v.value, f"{q.key.get('what', how.replace('_', ' ')).capitalize()}: {v.value}{_say(v)}.", \
            _basis(prof, v)
    if how == "take_drawn_value" and "take_drawn_value" in vals and vals["take_drawn_value"].value \
            and q.key.get("drawn_option"):
        v = vals["take_drawn_value"]
        return q.key["drawn_option"], f"{q.key.get('what', q.topic)}: {q.key['drawn_option']}{_say(v)}.", \
            _basis(prof, v)
    if how == "rule_set":
        g = q.key.get("derived")
        if not g:
            return None
        refs = [prof.rule_sets[i] for i in g if i in prof.rule_sets]
        if len(refs) != len(g):
            return None
        names = " and ".join(r.name for r in refs)
        missing = [r.name for r in refs if r.data is None]
        text = f"The governing rules are {names}" + (
            f" ({', '.join(missing)} not in B2M's rule data: compliance checks cannot use it)" if missing else "") + "."
        basis = f"{prof.name} profile - " + "; ".join(f"{r.name}: {r.source + '; ' if r.source else ''}{r.basis}"
                                                      for r in refs)
        return {"rule_sets": list(g)}, text, basis
    return None
