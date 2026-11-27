"""MEP runs teed into mains (2026-11-27 review): a run joins the main's segment its join point is on (a main of several
segments, or with points on a straight line merged, has other ids than its first); a piece a tee breaks off a main of
an earlier run is that run's (removed with it, never with the later run); a run another run builds on is not removed
before it; where the add-in joined elsewhere is in the run's record."""

import pytest

from plinth.builds import BuildService, InUse
from plinth.plan import BuildPlan
from plinth.revit import commands
from plinth.revit.types import TypeMap
from plinth.runner import Runner
from plinth.runs import RunStore
from tests.fitout.fake_revit import FitoutRevit

MEP = [{"kind": "pipe_type", "name": "PVC"}, {"kind": "pipe_system_type", "name": "Sanitary"}]


def pipe(key, points, **over):
    return {"op": "mep_run", "key": key, "kind": "pipe", "level": "L1", "type": "PVC", "system_type": "Sanitary",
            "diameter_mm": 100, "points": points, "require_connections": False, **over}


def plan(*ops, existing=None):
    return BuildPlan.model_validate({"ops": list(ops), "existing_levels": {"L1": 0}, "existing": existing or {}})


# a main along x, then up y: two segments; a point on a straight line (2000, 0) merged
MAIN = pipe("M", [(0, 0, 3000), (2000, 0, 3000), (4000, 0, 3000), (4000, 4000, 3000)])


def connect_id(revit, key_point):
    """The element id the create_mep_run call starting at ``key_point`` joined."""
    return next(p["start_connect"]["element_id"] for c, p in revit.calls
                if c == "batch_execute" for sub in p["commands"] for p in [sub["params"]]
                if sub["command"] == "create_mep_run" and tuple(p["points"][0]) == key_point)


def test_a_branch_joins_the_segment_its_point_is_on():
    revit = FitoutRevit(mep=MEP)
    on_second = pipe("B2", [(4000, 2000, 3000), (6000, 2000, 3000)], start_key="M")
    on_first = pipe("B1", [(3000, 0, 3000), (3000, -2000, 3000)], start_key="M")
    result = Runner(revit).build(plan(MAIN, on_second, on_first))
    assert result.status == "built", result.error
    assert [k for k in result.created if k.split("#")[0] == "M"] == ["M", "M#2", "M#f1"]  # (2000, 0) merged
    assert connect_id(revit, (4000, 2000, 3000)) == result.created["M#2"]
    assert connect_id(revit, (3000, 0, 3000)) == result.created["M"]
    # each tee's far piece and its tee are the branch's (the main is of the same run): removed with it
    assert {k for k in result.created if k.startswith("B2#")} == {"B2#f1", "B2#p1"}
    assert result.mep_log == [{"key": "B2", "what": "main_broken", "element_id": result.created["M#2"]},
                              {"key": "B1", "what": "main_broken", "element_id": result.created["M"]}]
    assert Runner(revit).verify(plan(MAIN, on_second, on_first), result.created)["ok"]


def test_the_segment_from_the_bends_the_main_reported():
    main = plan(MAIN).by_key()["M"]
    # extended at its start to what it joins, off the line: one more segment before the first leg
    assert commands.bends_of(main, {"bends": [{"point_index": 0}, {"point_index": 2}]}) == [0, 2]
    ids = {"M": 1, "M#2": 2, "M#3": 3}
    assert commands.join_key("M", (1000, 0, 3000), {"M": main}, ids, {"M": [0, 2]}) == "M#2"
    assert commands.join_key("M", (4000, 3000, 3000), {"M": main}, ids, {"M": [0, 2]}) == "M#3"
    # without the bends from the add-in: the points it keeps (a point on a straight line is merged)
    assert commands.bends_of(main, {}) == [2]
    # an earlier run's main (not in this plan), or a family: the key itself; the add-in walks the main
    assert commands.join_key("X", (0, 0, 0), {}, {"X": 9}, {}) == "X"


def build(service, *ops, existing=None, owners=None):
    return service.build_now(plan(*ops, existing=existing), "Scratch", owners=owners)


@pytest.mark.parametrize("with_owners", [True, False])
def test_a_tee_into_an_earlier_runs_main_leaves_its_piece_to_that_run(tmp_path, with_owners):
    revit = FitoutRevit(mep=MEP)
    before = dict(revit.elements)
    service = BuildService(RunStore(tmp_path), lambda: revit, types=TypeMap())
    pilot = build(service, MAIN)
    assert pilot["status"] == "built"
    # the rest, built later, tees into the pilot's main on its second segment; it knows only the main's first id
    branch = pipe("B", [(4000, 2000, 3000), (6000, 2000, 3000)], start_key="M")
    rest = build(service, branch, existing={"M": pilot["created"]["M"]},
                 owners={"M": pilot["run_id"]} if with_owners else None)
    assert rest["status"] == "built", rest.get("error")
    # the add-in teed into the segment the point is on and said so
    assert rest["mep_log"][0] == {"key": "B", "what": "joined", "at": "start", "requested_id": pilot["created"]["M"],
                                  "element_id": pilot["created"]["M#2"]}
    piece = rest["pieces_of_existing"]["M"]
    assert piece["run"] == pilot["run_id"] and len(piece["ids"]) == 1
    assert not set(piece["ids"]) & set(rest["created"].values())  # not the later run's
    owner = service.store.get(pilot["run_id"]).load("run")
    assert owner["created"]["M#p1"] == piece["ids"][0]
    # the pilot cannot go first: the rest joins its main
    with pytest.raises(InUse, match=f"Remove run {rest['run_id']} first"):
        service.remove(pilot["run_id"])
    assert service.remove(rest["run_id"])["status"] == "removed"
    # what is left of the main: its two pieces end to end at the tee's point, the tee gone with the branch
    left = [e for i, e in revit.elements.items() if i not in before and e["category"] == "OST_PipeCurves"]
    assert len(left) == 3  # the main's two segments, the second broken in two
    assert service.remove(pilot["run_id"])["status"] == "removed"
    assert revit.elements == before


def test_the_building_run_is_not_removed_under_its_fitout(tmp_path):
    revit = FitoutRevit(mep=MEP, types=[{"category": "OST_Furniture", "family": "Chair", "type": "Standard"}],
                        elements={7001: {"category": "OST_Walls", "level": "L1"}})
    store = RunStore(tmp_path)
    building = store.create()
    building.save("plan", {"ops": []})
    building.update(status="built", created={"W1": 7001}, document_title="Scratch")
    service = BuildService(store, lambda: revit, types=TypeMap())
    # a fit-out recorded before existing_runs was kept: found by its plan's existing ids
    chair = {"op": "family", "key": "FIT-CHAIR", "category": "OST_Furniture", "family": "Chair", "type": "Standard",
             "level": "L1", "point": (500, 500)}
    plan_ = BuildPlan.model_validate({"ops": [chair, pipe("P", [(0, 0, 0), (0, 1000, 0)])],
                                      "existing_levels": {"L1": 0}, "existing": {"W1": 7001}})
    fitout = service.build_now(plan_, "Scratch")
    assert fitout["status"] == "built"
    with pytest.raises(InUse, match=f"Remove run {fitout['run_id']} first"):
        service.remove(building.id)
    assert service.remove(fitout["run_id"])["status"] == "removed"
    assert service.dependants(building.id, {"W1": 7001}) == []
