"""scripts/fitout_build.py: a building's run -> families (measured, or measurements given) -> layout -> plan, dry by
default; with --build, built as its own run on top of the building's run."""

import json

from plinth.revit.types import TypeMap
from plinth.runs import RunStore
from scripts import fitout_build
from tests.fitout.test_measured import ANSWERS, building, revit


def building_run(store: RunStore, doc) -> str:
    plan = building()
    created = {}
    for i, op in enumerate(plan.ops):
        created[op.key] = 70000 + i
        doc.elements[70000 + i] = {"category": "OST_Walls" if op.op == "wall" else "OST_Generic", "level": None}
    run = store.create()
    run.save("plan", plan.model_dump(mode="json"))
    run.update(status="built", created=created, document_title=doc.title)
    return run.id


def test_a_dry_plan_then_a_build_as_its_own_run(tmp_path, capsys):
    doc = revit()
    store = RunStore(tmp_path / "runs")
    run_id = building_run(store, doc)
    before = dict(doc.elements)
    answers = tmp_path / "answers.json"
    answers.write_text(json.dumps(ANSWERS), encoding="utf-8")
    measurements = tmp_path / "measured.json"  # the sofa measured earlier on L1 (3000): its box starts at +3000
    measurements.write_text(json.dumps({"types": [{"family": "Bella sofa", "type": "Bella sofa", "placement": "OneLevelBased",
                                                   "min": [-1000, -450, 3000], "max": [1000, 450, 3800]}]}),
                            encoding="utf-8")
    argv = ["--run", run_id, "--title", doc.title, "--rules", "west-bengal", "--answers", str(answers),
            "--measurements", str(measurements), "--measured-level", "L1", "--out", str(tmp_path / "out")]
    assert fitout_build.main(argv, connect=lambda: doc, store=store) == 0
    printed = capsys.readouterr().out
    assert "1 measured types" in printed and "sofa: 1 laid out, 1 in the plan  [2000 x 900 x1]" in printed
    assert "fitout:family-measured:sofa" in printed and "READY" in printed
    report = json.loads((tmp_path / "out" / "report.json").read_text(encoding="utf-8"))
    assert report["items"]["side_table"]["not_placed"] == {
        "left_out: included in the bed family (Bed Set: Queen set)": 1}
    assert doc.elements == before  # dry: nothing placed
    assert len(list((tmp_path / "runs").iterdir())) == 1

    assert fitout_build.main(argv + ["--build"], connect=lambda: doc, store=store, types=TypeMap()) == 0
    runs = [r for r in store.list() if r["run_id"] != run_id]
    assert len(runs) == 1 and runs[0]["status"] == "built"
    saved = store.get(runs[0]["run_id"]).load("fitout_report")
    assert saved["building_run"] == run_id and saved["ready"]
    added = [e for i, e in doc.elements.items() if i not in before and not e.get("is_type") and not e.get("is_family")]
    assert len(added) == sum(x["in_plan"] for x in saved["items"].values())


def test_a_wrong_run_or_level_stops_before_revit(tmp_path, capsys):
    doc = revit()
    store = RunStore(tmp_path / "runs")
    run_id = building_run(store, doc)
    base = ["--title", doc.title, "--rules", "west-bengal"]
    assert fitout_build.main(["--run", "nope", *base], connect=lambda: doc, store=store) == 2
    assert fitout_build.main(["--run", run_id, *base, "--measured-level", "Floor 9"], connect=lambda: doc, store=store) == 2
    assert not doc.calls
