"""Fit-out of a built model, from the command line (PRD Step 10): families first, then the furniture layout, then the
plan. Dry by default: it reads the building's run, matches and measures the families in the open document, lays the
furniture out with their real footprints and prints the questions, assumptions, what is left out and the plan's op
counts. With --build it builds the plan as its own run on top of the building's run (removable on its own).

    cd service
    .venv\\Scripts\\python -m scripts.fitout_build --run 2026-11-16T16-19-54Z-7d66 --title collage_project_structure ^
        --rules west-bengal --answers fitout_answers.json [--measurements family_measurements.json] ^
        [--measured-level "Floor 1"] [--out report_dir] [--build]

--answers: a JSON file (or inline JSON) of fit-out answers, e.g. {"fitout:family:sofa": {"family": "...", "type":
"..."}, "fitout:family:side_table": "skip"}; an answer may add "types" (preferred first), "front" (-y, +y, -x, +x:
which way the family's front faces in its own coordinates) and "includes" (items the family already has).
--measurements: boxes measured earlier (measure_family_types' shape), used instead of measuring now (an add-in
without measure_family_types then needs no redeploy); --measured-level: the level they were measured on (else a box
starting at one of the building's level elevations is taken as measured there with the point's z taken as offset).
The document must be the one the building run was built in (its title is checked). Building a family that takes the
point's z as its offset (base offset in its measurement) on an upper level needs an add-in with FamilyPlacer's
put-back (2026-11-17); an older one stops that op with its 1 mm position check and rolls its batch back.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Any, Callable

from b2m import logs
from b2m.builds import BuildService
from b2m.fitout import families, rooms, rules, to_plan
from b2m.plan import BuildPlan, LevelOp
from b2m.revit import RevitClient
from b2m.revit.commands import REQUIRED_COMMANDS
from b2m.revit.types import TypeMap
from b2m.runs import RunStore

# What planning reads in the document (measure_family_types is used when the add-in has it).
PLAN_COMMANDS = ["get_available_family_types", "get_type_parameters", "list_template_files", "list_types_in_file"]
# What building needs on top of a building's commands (types from templates, size copies).
BUILD_COMMANDS = REQUIRED_COMMANDS + ["copy_types_from_file", "duplicate_family_type", "get_available_family_types"]


def load_answers(text: str | None) -> dict[str, Any]:
    if not text:
        return {}
    data = json.loads(text) if text.lstrip().startswith("{") else json.loads(Path(text).read_text(encoding="utf-8"))
    return {k: v for k, v in data.items() if not k.startswith("_")}


def load_rules(name: str) -> rules.FitoutRules:
    try:
        return rules.load(name)
    except (KeyError, ValueError, FileNotFoundError):
        found = rules.for_profile(name)
        if found is None:
            raise SystemExit(f"no fit-out rule set or location profile named {name!r}") from None
        return found


def level_elevations(plan: BuildPlan) -> dict[str, float]:
    out = {op.name: op.elevation_mm for op in plan.ops if isinstance(op, LevelOp)}
    out.update(plan.existing_levels)
    return out


def report(found: to_plan.FitoutPlan) -> dict[str, Any]:
    """What the plan holds and why the rest is not in it, item by item."""
    layout = found.layout
    placed = Counter(p.item for p in layout.placed) if layout else Counter()
    in_plan = Counter()
    by_key = found.plan.by_key() if found.plan else {}
    for p in layout.placed if layout else []:
        if p.key in by_key:
            in_plan[p.item] += 1
    unplaced: dict[str, Counter] = {}
    for u in layout.unplaced if layout else []:
        unplaced.setdefault(u.item, Counter())[f"{u.status}: {u.reason}"] += 1
    sizes: dict[str, Counter] = {}
    for p in layout.placed if layout else []:
        sizes.setdefault(p.item, Counter())[f"{p.width_mm:g} x {p.depth_mm:g}"] += 1
    items = {}
    for item in sorted(set(placed) | set(unplaced)):
        items[item] = {"laid_out": placed[item], "in_plan": in_plan[item], "sizes": dict(sizes.get(item, {})),
                       "not_placed": dict(unplaced.get(item, {}))}
    ops = Counter(op.op for op in found.plan.ops) if found.plan else Counter()
    types = Counter(f"{op.family}: {op.type}" for op in found.plan.ops if op.op == "family") if found.plan else Counter()
    left = Counter(f"{x.item}: {x.reason}" for x in found.left_out)
    return {
        "ready": found.ready,
        "items": items,
        "questions": [{"id": q.id, "text": q.text, "instances": len(q.instances), "options": q.options[:12]}
                      for q in found.questions],
        "assumptions": [{"id": a.id, "text": a.text, "source": a.source} for a in found.assumptions],
        "left_out_of_plan": dict(left),
        "ops": dict(ops),
        "family_types": dict(types),
        "skipped_rooms": [{"room": s.room_key, "name": s.name, "reason": s.reason} for s in layout.skipped] if layout else [],
    }


def print_report(r: dict[str, Any]) -> None:
    print("\nITEMS (laid out / in the plan, sizes; not placed with why)")
    for item, x in r["items"].items():
        sizes = ", ".join(f"{s} x{n}" for s, n in x["sizes"].items())
        print(f"  {item}: {x['laid_out']} laid out, {x['in_plan']} in the plan" + (f"  [{sizes}]" if sizes else ""))
        for why, n in x["not_placed"].items():
            print(f"      not placed x{n}: {why}")
    print(f"\nQUESTIONS ({len(r['questions'])})")
    for q in r["questions"]:
        print(f"  {q['id']} ({q['instances']} items): {q['text']}")
    print(f"\nASSUMPTIONS ({len(r['assumptions'])})")
    for a in r["assumptions"]:
        print(f"  {a['id']}: {a['text']}")
    print(f"\nLEFT OUT OF THE PLAN ({sum(r['left_out_of_plan'].values())})")
    for why, n in r["left_out_of_plan"].items():
        print(f"  x{n} {why}")
    print("\nOPS", json.dumps(r["ops"]))
    for t, n in r["family_types"].items():
        print(f"  {n:4d}  {t}")
    if r["skipped_rooms"]:
        print(f"\nROOMS NOT FURNISHED ({len(r['skipped_rooms'])})")
        for s in r["skipped_rooms"]:
            print(f"  {s['room']} {s['name']}: {s['reason']}")
    print("\nREADY" if r["ready"] else "\nNOT READY: answer the questions above (or leave the items out) and plan again")


def main(argv: list[str] | None = None, *, connect: Callable[[], Any] | None = None, store: RunStore | None = None,
         types: TypeMap | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", required=True, help="the building's run id (built), e.g. 2026-11-16T16-19-54Z-7d66")
    ap.add_argument("--title", required=True, help="the open Revit document's title (checked before anything is read)")
    ap.add_argument("--rules", required=True, help="fit-out rule set id (west-bengal, india-nbc2016) or location profile")
    ap.add_argument("--answers", help="fit-out answers: a JSON file or inline JSON")
    ap.add_argument("--measurements", type=Path, help="measured family boxes (measure_family_types' shape)")
    ap.add_argument("--measured-level", help="the level the measurements (or measure_family_types) use")
    ap.add_argument("--types", type=Path, help="office standards (TypeMap) for the build")
    ap.add_argument("--out", type=Path, help="folder to write report.json and plan.json to")
    ap.add_argument("--build", action="store_true", help="build the plan as its own run")
    ap.add_argument("--partial", action="store_true", help="with --build: build even with open questions (their items wait)")
    args = ap.parse_args(argv)
    logs.configure()

    store = store or RunStore()
    run = store.get(args.run)
    if run is None:
        print(f"no run {args.run} in {store.root}")
        return 2
    record = run.load("run")
    if record.get("status") not in ("built", "built_with_issues"):
        print(f"run {args.run} is {record.get('status')}: the fit-out goes on a built model")
        return 2
    building = BuildPlan.model_validate(run.load("plan"))
    built: dict[str, int] = record.get("created") or {}
    ruleset = load_rules(args.rules)
    answers = load_answers(args.answers)
    elevations = level_elevations(building)
    if args.measured_level and args.measured_level not in elevations:
        print(f"--measured-level {args.measured_level!r} is not a level of the building ({', '.join(elevations)})")
        return 2
    measurements = None
    if args.measurements:
        at = [elevations[args.measured_level]] if args.measured_level else list(elevations.values())
        measurements = families.load_measurements(args.measurements, level_elevations=at)
        print(f"{len(measurements)} measured types from {args.measurements}")
    model = rooms.build(building, ruleset, answers)
    if model.questions:
        print(f"{len(model.questions)} room questions (answer them in --answers): "
              + "; ".join(f"{q.id}: {q.text}" for q in model.questions[:10]))

    plan_connect = connect or (lambda: RevitClient.connect(required_commands=PLAN_COMMANDS))
    with plan_connect() as revit:
        title = revit.confirm_document(args.title)
        found = to_plan.plan_furniture(revit, building, built, model, ruleset, answers=answers,
                                       measurements=measurements, document=title, measure_level=args.measured_level)
    result = report(found)
    print_report(result)
    if args.out:
        args.out.mkdir(parents=True, exist_ok=True)
        (args.out / "report.json").write_text(json.dumps(result, indent=1, ensure_ascii=False), encoding="utf-8")
        if found.plan is not None:
            (args.out / "plan.json").write_text(found.plan.model_dump_json(indent=1), encoding="utf-8")
        print(f"\nwritten to {args.out}")

    if not args.build:
        return 0
    if found.plan is None:
        print("nothing to build")
        return 1
    if not found.ready and not args.partial:
        print("not built: there are open questions (answer them, or pass --partial to build what is planned)")
        return 1
    build_connect = connect or (lambda: RevitClient.connect(required_commands=BUILD_COMMANDS))
    service = BuildService(store, connect=build_connect, types=types if types is not None else TypeMap.load(args.types))
    built_run = service.build_now(found.plan, args.title)
    fitout_run = store.get(built_run["run_id"])
    fitout_run.save("fitout_report", {**result, "building_run": args.run, "answers": answers})
    print(f"\nbuild {built_run['run_id']}: {built_run['status']}"
          + (f" ({built_run.get('error')})" if built_run.get("error") else "")
          + f"; {len(built_run.get('created') or {})} elements; read-back "
          + ("ok" if (built_run.get("readback") or {}).get("ok") else json.dumps(built_run.get("readback"))[:300]))
    return 0 if built_run["status"] == "built" else 1


if __name__ == "__main__":
    sys.exit(main())
