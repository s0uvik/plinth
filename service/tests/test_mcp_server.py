import asyncio

from b2m.mcp_server import build_server


class FakeApi:
    def __init__(self):
        self.calls = []

    def call(self, method, path, body=None):
        self.calls.append((method, path, body))
        return {"ok": True, "path": path}


def run(coro):
    return asyncio.run(coro)


def test_tools_are_listed():
    names = {t.name for t in run(build_server(FakeApi()).list_tools())}
    assert names == {"b2m_plan_from_brief", "b2m_preview", "b2m_check", "b2m_build", "b2m_run_status", "b2m_drawing_set", "b2m_remove"}


def test_tools_forward_to_the_local_service():
    api = FakeApi()
    server = build_server(api)
    run(server.call_tool("b2m_plan_from_brief", {"brief": "G+3 on 15 x 25 m"}))
    run(server.call_tool("b2m_plan_from_brief", {"brief_id": "b1", "answers": {"plot_depth_mm": "25 m"}}))
    run(server.call_tool("b2m_build", {"spec": {"x": 1}, "document_title": "Scratch"}))
    run(server.call_tool("b2m_run_status", {"run_id": "r1"}))
    run(server.call_tool("b2m_remove", {"run_id": "r1"}))
    assert api.calls == [
        ("POST", "/briefs", {"brief": "G+3 on 15 x 25 m"}),
        ("POST", "/briefs/b1/answers", {"answers": {"plot_depth_mm": "25 m"}}),
        ("POST", "/builds/from-spec", {"spec": {"x": 1}, "document_title": "Scratch", "brief_id": None, "override": False}),
        ("GET", "/runs/r1", None),
        ("POST", "/runs/r1/remove", {}),
    ]
