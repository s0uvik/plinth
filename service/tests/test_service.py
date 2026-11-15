from fastapi.testclient import TestClient

from b2m.api import create_app
from b2m.builds import BuildService
from b2m.examples import two_storey_house
from b2m.runs import RunStore
from tests.test_plan_and_runner import HOUSE, FakeRevit, _stats_for


class FakeClient(FakeRevit):
    """FakeRevit plus the RevitClient surface BuildService uses."""

    def __init__(self, title="Scratch", **kw):
        super().__init__(**kw)
        self.title = title
        self.health = {"addin_version": "0.1.0", "revit_year": 2026}
        self.observer = None
        self.confirmed_with = []

    def confirm_document(self, expected_title=None):
        self.confirmed_with.append(expected_title)
        if expected_title is not None and expected_title != self.title:
            raise ConnectionError("wrong document")
        return self.title

    def call(self, command, params=None, *, timeout_seconds=None):
        if command == "analyze_model_statistics":
            self.stats = _stats_for({k: v for k, v in self._created.items()})
        result = super().call(command, params, timeout_seconds=timeout_seconds)
        if self.observer:
            self.observer({"command": command, "params": params}, {"success": True})
        return result

    _created = {}

    def __enter__(self):
        return self

    def __exit__(self, *a):
        pass


def make_service(tmp_path, client):
    def connect():
        return client
    return BuildService(RunStore(tmp_path), connect)


def test_build_records_run_versions_tool_calls_and_readback(tmp_path):
    client = FakeClient()
    FakeClient._created = {op.key: 0 for op in HOUSE.ops}
    service = make_service(tmp_path, client)
    record = service.build_now(HOUSE, "Scratch")
    assert record["status"] == "built", record
    assert record["batches"] == {"total": 7, "succeeded": 7}
    assert record["versions"]["addin"] == "0.1.0" and record["versions"]["revit"] == 2026
    assert record["readback"]["ok"]
    run = service.store.get(record["run_id"])
    assert run.load("plan")["ops"][0]["key"] == "L0"
    assert (run.dir / "tool_calls.jsonl").read_text().count("\n") >= 9


def test_wrong_document_fails_the_run_without_building(tmp_path):
    client = FakeClient(title="Live Project")
    record = make_service(tmp_path, client).build_now(HOUSE, "Scratch")
    assert record["status"] == "failed"
    assert "wrong document" in record["error"]
    assert client.batches == 0


def test_remove_deletes_only_recorded_ids_on_the_same_document(tmp_path):
    client = FakeClient()
    FakeClient._created = {op.key: 0 for op in HOUSE.ops}
    service = make_service(tmp_path, client)
    record = service.build_now(HOUSE, None)
    removed = service.remove(record["run_id"])
    assert removed["status"] == "removed"
    assert client.confirmed_with[-1] == "Scratch"
    deleted = [p for c, p in client.calls if c == "delete_element"][0]["elementIds"]
    assert sorted(deleted) == sorted(record["created"].values())


def test_api_starts_builds_and_reports_runs(tmp_path):
    client = FakeClient()
    FakeClient._created = {op.key: 0 for op in HOUSE.ops}
    service = make_service(tmp_path, client)
    api = TestClient(create_app(service))
    plan = two_storey_house("Door 900", "Window 1200").model_dump(mode="json")
    response = api.post("/builds", json={"plan": plan})
    assert response.status_code == 202
    run_id = response.json()["run_id"]
    with service._lock:
        pass
    assert api.get(f"/runs/{run_id}").json()["status"] == "built"
    assert api.get("/runs").json()[0]["run_id"] == run_id
    assert api.get("/runs/nope").status_code == 404
    assert api.post("/builds", json={"plan": {"ops": []}}).status_code == 422
