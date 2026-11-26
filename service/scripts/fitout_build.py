"""Fit-out of a built model, from the command line (PRD Step 10): families first, then the furniture layout, the MEP
layout on it (plumbing and drainage, electrical, HVAC, fire), then the plan (``plinth.fitout.session``, shared with
the HTTP endpoint and the MCP tool). Dry by default: it reads the building's run, matches and measures the families in
the open document, lays the furniture out with their real footprints and the MEP on that, and prints the questions,
assumptions, what is left out and the plan's op counts, with the MEP notice, counts per system and audit. With --build
it builds the plan as its own run on top of the building's run (removable on its own).

    cd service
    .venv\\Scripts\\python -m scripts.fitout_build --run 2026-11-16T16-19-54Z-7d66 --title collage_project_structure ^
        --rules west-bengal --answers fitout_answers.json [--measurements family_measurements.json] ^
        [--measured-level "Floor 1"] [--systems plumbing,electrical,hvac,fire] [--furniture-run <run>] ^
        [--flat U1 --level "Floor 1"] [--out report_dir] [--build [--partial]]

--systems: what to lay out and build, of furniture, plumbing, electrical, hvac, fire (default all). The MEP layout is
an indicative coordination model (every report says so): an MEP engineer designs and checks the real systems.
--furniture-run: an earlier fit-out run whose furniture is already built in the model (e.g. --systems
plumbing,electrical,hvac,fire on top of it): its answers are the defaults (--answers win), its furniture is joined by
the runs and circuits, never placed again, and the furniture laid out now must be what it built (same key: level,
family type, point within 1 mm, turn within 0.1 degree), else nothing is planned and the differences are listed.
--answers: a JSON file (or inline JSON) of fit-out answers, e.g. {"fitout:family:sofa": {"family": "...", "type":
"..."}, "fitout:family:side_table": "skip"}; an answer may add "types" (preferred first), "front" (-y, +y, -x, +x:
which way the family's front faces in its own coordinates) and "includes" (items the family already has). The answer
{"create": "box"} (optionally "name", "size_mm": [w, d, h], "material") is the user's OK for Plinth to create a plain box
family for the item (default "Plinth <Label>" at the rule's first size, type "<w> x <d> x <h>mm (Plinth)"): the plan gets a
family_create pre-step (the add-in's create_box_family, with the connectors the plan joins to it: electrical, and a pipe
connector per system and size; a family of that name already in the project is used as it is), the item is laid out at
the box's size with no measuring, and the family is removed with the fit-out run. The MEP layout's own questions
(meter room, tanks, AC of large rooms, shafts...) are answered in the same file (see plinth.fitout.mep).
--measurements: boxes measured earlier (measure_family_types' shape), used instead of measuring now (an add-in
without measure_family_types then needs no redeploy); --measured-level: the level they were measured on (else a box
starting at one of the building's level elevations is taken as measured there with the point's z taken as offset).
--flat (with --level): a pilot of one flat's rooms, with the MEP records of the flat and what they join (the
branches, stacks and mains its pipes tee into); --exclude-run: leave out (and join) what an earlier fit-out run (the
pilot) already built.
The document must be the one the building run was built in (its title is checked). Building a family that takes the
point's z as its offset (base offset in its measurement) on an upper level needs an add-in with FamilyPlacer's
put-back (2026-11-17); an older one stops that op with its 1 mm position check and rolls its batch back. Joins Revit
refuses (a family without a connector of the run's domain) and circuit devices without an electrical connector are
the run's issues (built_with_issues), listed after the build.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path
from typing import Any, Callable

from plinth import logs
from plinth.fitout import session
from plinth.revit import RevitClient
from plinth.revit.types import TypeMap
from plinth.runs import RunStore


def load_answers(text: str | None) -> dict[str, Any]:
    if not text:
        return {}
    data = json.loads(text) if text.lstrip().startswith("{") else json.loads(Path(text).read_text(encoding="utf-8"))
    return {k: v for k, v in data.items() if not k.startswith("_")}


def print_report(r: dict[str, Any]) -> None:
    if r.get("notice"):
        print(f"\nNOTICE: {r['notice']}")
    print(f"\nSYSTEMS: {', '.join(r['systems'])}" + (f"  (pilot: flat {r['flat']}" + (f" on {r['level']}" if r["level"] else "")
                                                       + ")" if r["flat"] else ""))
    print("\nITEMS (laid out / in the plan, sizes; not placed with why)")
    for item, x in r["items"].items():
        sizes = ", ".join(f"{s} x{n}" for s, n in x["sizes"].items())
        print(f"  {item}: {x['laid_out']} laid out, {x['in_plan']} in the plan" + (f"  [{sizes}]" if sizes else "")
              + (f", {x['built_before']} built before" if x["built_before"] else ""))
        for why, n in x["not_placed"].items():
            print(f"      not placed x{n}: {why}")
    if r.get("furniture_check"):
        c = r["furniture_check"]
        print(f"\nFURNITURE RUN {r['furniture_run']}: {c['compared']} items as built"
              + (f"; {len(c['built_not_planned_now'])} built but not planned now (runs to them are left out)"
                 if c["built_not_planned_now"] else ""))
    m = r.get("mep")
    if m:
        print("\nMEP (laid out / in the plan)")
        for system, c in m["counts"].items():
            print(f"  {system}: {c['laid_out']} / {c['in_plan']}  devices {json.dumps(c['devices'])}  "
                  f"runs {json.dumps(c['runs'])}" + (f"  circuits {json.dumps(c['circuits'])}" if "circuits" in c else ""))
        if m["water"]:
            w = m["water"]
            print(f"  water: {w['persons']:g} persons, {w['demand_lpd']:g} L/day; underground {w['underground_l']:g} L, "
                  f"overhead {sum(w['overhead_l'].values()):g} L")
        for label, rows in (("not placed", m["unplaced"]), ("not routed", m["unrouted"])):
            if rows:
                print(f"  {label} ({len(rows)}):")
                for row in rows[:20]:
                    print(f"    {row['what']}: {row['reason']}")
        print(f"  audit: {len(m['problems'])} problems" + "".join(f"\n    {p}" for p in m["problems"][:20]))
    print(f"\nQUESTIONS ({len(r['questions'])})")
    for q in r["questions"]:
        print(f"  {q['id']} ({q['instances']} items): {q['text']}")
    if r["questions_not_needed_now"]:
        print(f"  (and {len(r['questions_not_needed_now'])} about what is not built now)")
    if r["room_questions"]:
        print(f"\nROOM QUESTIONS ({len(r['room_questions'])}, answer them in --answers)")
        for q in r["room_questions"][:10]:
            print(f"  {q['id']}: {q['text']}")
    print(f"\nASSUMPTIONS ({len(r['assumptions'])})")
    for a in r["assumptions"]:
        print(f"  {a['id']}: {a['text']}")
    print(f"\nLEFT OUT OF THE PLAN ({sum(r['left_out_of_plan'].values())})")
    for why, n in r["left_out_of_plan"].items():
        print(f"  x{n} {why}")
    if r["dropped"]:
        print(f"\nNOT BUILT NOW ({sum(r['dropped'].values())}; {r['circuits_trimmed']} circuits without some devices)")
        for why, n in r["dropped"].items():
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
    ap.add_argument("--systems", help="what to build: furniture,plumbing,electrical,hvac,fire (default all)")
    ap.add_argument("--furniture-run", help="an earlier fit-out run whose furniture is built: joined, not placed again")
    ap.add_argument("--types", type=Path, help="office standards (TypeMap) for the build")
    ap.add_argument("--out", type=Path, help="folder to write report.json and plan.json to")
    ap.add_argument("--build", action="store_true", help="build the plan as its own run")
    ap.add_argument("--partial", action="store_true", help="with --build: build even with open questions (their items wait)")
    ap.add_argument("--level", help="with --flat: the level of the flat to build alone (a pilot)")
    ap.add_argument("--exclude-run", action="append", default=[],
                    help="leave out (and join) what an earlier fit-out run already built (e.g. the pilot)")
    ap.add_argument("--flat", help="build only this flat's rooms on --level (a pilot to check placement before the rest)")
    args = ap.parse_args(argv)
    logs.configure()

    store = store or RunStore()
    try:
        inputs = session.prepare(store, args.run, args.rules, answers=load_answers(args.answers),
                                 measurements=args.measurements, measured_level=args.measured_level,
                                 systems=args.systems, furniture_run=args.furniture_run,
                                 exclude_runs=args.exclude_run, flat=args.flat, level=args.level)
    except session.SessionError as e:
        print(e)
        return 2
    if inputs.measurements is not None:
        print(f"{len(inputs.measurements)} measured types from {args.measurements}")

    plan_connect = connect or (lambda: RevitClient.connect(required_commands=session.PLAN_COMMANDS))
    with plan_connect() as revit:
        try:
            planned = session.plan(revit, inputs, args.title)
        except session.SessionError as e:
            print(e)
            return 2
    result = planned.report
    print_report(result)
    if args.out:
        args.out.mkdir(parents=True, exist_ok=True)
        (args.out / "report.json").write_text(json.dumps(result, indent=1, ensure_ascii=False), encoding="utf-8")
        if planned.plan is not None:
            (args.out / "plan.json").write_text(planned.plan.model_dump_json(indent=1), encoding="utf-8")
        print(f"\nwritten to {args.out}")

    if not args.build:
        return 0
    try:
        built_run = session.build(planned, store, args.title, connect=connect,
                                  types=types if types is not None else TypeMap.load(args.types), partial=args.partial)
    except session.SessionError as e:
        print(e)
        return 1
    issues = built_run.get("issues") or []
    print(f"\nbuild {built_run['run_id']}: {built_run['status']}"
          + (f" ({built_run.get('error')})" if built_run.get("error") else "")
          + f"; {len(built_run.get('created') or {})} elements; read-back "
          + ("ok" if (built_run.get("readback") or {}).get("ok") else json.dumps(built_run.get("readback"))[:300]))
    if issues:
        print(f"ISSUES ({len(issues)}: built, but not wholly; all in the run record)")
        for (what, code), n in Counter((i["what"], i["reason"].split(":", 1)[0]) for i in issues).most_common():
            print(f"  x{n} {what}: {code}")
        for i in issues[:20]:
            print(f"    {i['key']} {i['what']}: {i['reason']}")
    if result.get("notice"):
        print(f"NOTICE: {result['notice']}")
    return 0 if built_run["status"] == "built" else 1


if __name__ == "__main__":
    sys.exit(main())
