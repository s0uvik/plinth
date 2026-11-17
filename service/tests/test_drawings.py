import itertools
import json
from pathlib import Path

from pypdf import PdfReader
from reportlab.pdfgen import canvas

from plinth.drawings import build_drawing_set
from plinth.engine import generate
from plinth.spec import BuildingSpec

SPEC = Path(__file__).parent / "golden" / "specs" / "01-t2-2bhk-stilt-g3.json"
FRAME = {"min_x": 0, "min_y": 0, "max_x": 840, "max_y": 594, "width": 840, "height": 594, "center_x": 420, "center_y": 297}


def one_page_pdf(path: Path, pages: int = 1) -> None:
    c = canvas.Canvas(str(path))
    for _ in range(pages):
        c.drawString(100, 100, "page")
        c.showPage()
    c.save()


class FakeRevit:
    def __init__(self):
        self.calls = []
        self.ids = itertools.count(5000)

    def call(self, command, params=None, **_):
        self.calls.append((command, params))
        if command == "create_view":
            return {"viewId": next(self.ids), "viewName": params["name"]}
        if command == "tag_all_rooms":
            return {"tagIds": [next(self.ids), next(self.ids)], "tagged": 2}
        if command == "create_schedule":
            return {"scheduleId": next(self.ids)}
        if command == "create_sheet":
            return {"sheet_id": next(self.ids), "frame_sheet_mm": FRAME}
        if command == "place_view_on_sheet":
            return {"viewport_id": next(self.ids)}
        if command == "place_schedule_on_sheet":
            return {"schedule_instance_id": next(self.ids)}
        if command == "export_pdf":
            path = Path(params["output_folder"]) / params["file_name"]
            one_page_pdf(path, len(params["view_ids"]))
            return {"path": str(path)}
        if command == "capture_view_image":
            path = Path(params["output_folder"]) / params["file_name"]
            path.write_bytes(b"\x89PNG\r\n")
            return {"path": str(path)}
        raise AssertionError(command)


def test_drawing_set_for_a_g3_block(tmp_path):
    spec = BuildingSpec.model_validate(json.loads(SPEC.read_text(encoding="utf-8")))
    plan = generate(spec).plan
    report = tmp_path / "report.pdf"
    one_page_pdf(report, 2)
    revit, recorded = FakeRevit(), {}
    result = build_drawing_set(revit, plan, tmp_path / "out", report, record=lambda k, i: recorded.__setitem__(k, i))

    assert result["sheets"] == ["A101", "A102", "A103", "A104", "A201", "A301", "A401", "A402"]  # eight sheets for G+3
    assert len(PdfReader(result["set_pdf"]).pages) == 8 + 2  # drawings then the compliance report
    commands = [c for c, _ in revit.calls]
    assert commands.count("create_view") == 4 + 3 and commands.count("tag_all_rooms") == 4
    sections = [p for c, p in revit.calls if c == "create_view" and p["viewType"] == "section"]
    assert sections[0]["startY"] == sections[0]["endY"] and sections[0]["startX"] < sections[0]["endX"]  # looks north
    exported = [p for c, p in revit.calls if c == "export_pdf"][0]["view_ids"]
    assert exported == [recorded[f"DWG-SHEET-{n}"] for n in result["sheets"]]
    assert all(k.startswith("DWG-") for k in recorded) and len(recorded) > 30
    area = [p for c, p in revit.calls if c == "create_schedule" and p["category"] == "OST_Rooms"][0]
    assert area["sortGroup"][0]["fieldName"] == "ROOM_DEPARTMENT" and area["showGrandTotals"]


def test_drawings_endpoint_needs_a_built_run(tmp_path, monkeypatch):
    from fastapi.testclient import TestClient

    from plinth.api import create_app
    from tests.test_service import FakeClient, make_service
    monkeypatch.setenv("PLINTH_HOME", str(tmp_path))
    service = make_service(tmp_path, FakeClient())
    api = TestClient(create_app(service, brief_client=lambda: None))
    run = service.store.create()
    run.update(status="failed")
    assert api.post(f"/runs/{run.id}/drawings").status_code == 409
    assert api.post("/runs/nope/drawings").status_code == 404
    assert api.get(f"/runs/{run.id}/3d.png").status_code == 404
