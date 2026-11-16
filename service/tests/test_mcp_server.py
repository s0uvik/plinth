import asyncio

from b2m.mcp_server import build_server

COMMANDS = [
    {"name": "create_level", "toolset": "create", "description": "Create a level", "read_only": False,
     "schema": {"type": "object", "properties": {"elevation": {"type": "number"}, "name": {"type": "string"}}, "required": ["elevation"]}},
    {"name": "list_levels", "toolset": "query", "description": "List levels", "read_only": True,
     "schema": {"type": "object", "properties": {}}},
    {"name": "create_duct", "toolset": "mep", "description": "Create a duct", "read_only": False,
     "schema": {"type": "object", "properties": {"from": {"type": "string"}}}},
]

B2M_TOOLS = {"b2m_plan_from_brief", "b2m_preview", "b2m_check", "b2m_build", "b2m_run_status", "b2m_drawing_set", "b2m_remove",
             "b2m_import_start", "b2m_import_status", "b2m_import_questions", "b2m_import_answer",
             "b2m_import_set_location", "b2m_import_assume", "b2m_import_approve", "b2m_import_build"}
GATEWAY_READS = {"revit_list_available_targets", "revit_get_current_target", "revit_health", "revit_get_operation_status",
                 "revit_lookup_message", "revit_analyze_usage_patterns", "revit_get_failure_records",
                 "revit_get_change_records", "revit_list_code_modules", "revit_list_bake_suggestions"}
GATEWAY_WRITES = {"revit_switch_target", "revit_record_change", "revit_resolve_history_identity", "revit_save_code_module",
                  "revit_delete_code_module", "revit_accept_bake_suggestion", "revit_dismiss_bake_suggestion"}


class FakeApi:
    def __init__(self, commands=COMMANDS):
        self.calls = []
        self.commands = commands

    def call(self, method, path, body=None):
        if path == "/revit/commands":
            if self.commands is None:
                raise RuntimeError("service not running")
            return self.commands
        self.calls.append((method, path, body))
        return {"ok": True, "path": path}


def run(coro):
    return asyncio.run(coro)


def names(server):
    return {t.name for t in run(server.list_tools())}


def test_default_config_lists_b2m_gateway_and_default_toolsets():
    tools = names(build_server(FakeApi(), {"toolsets": ["query", "create", "view", "meta"], "read_only": False}))
    assert tools == B2M_TOOLS | GATEWAY_READS | GATEWAY_WRITES | {"revit_create_level", "revit_list_levels"}


def test_all_toolsets_and_read_only_mode():
    assert "revit_create_duct" in names(build_server(FakeApi(), {"toolsets": "all", "read_only": False}))
    read_only = names(build_server(FakeApi(), {"toolsets": "all", "read_only": True}))
    assert "revit_list_levels" in read_only and "revit_create_level" not in read_only
    assert not (read_only & GATEWAY_WRITES)


def test_without_revit_only_b2m_and_gateway_tools_load():
    tools = names(build_server(FakeApi(commands=None), {"toolsets": "all", "read_only": False}))
    assert tools == B2M_TOOLS | GATEWAY_READS | GATEWAY_WRITES


def test_catalog_tools_take_their_schema_fields_and_forward():
    api = FakeApi()
    server = build_server(api, {"toolsets": "all", "read_only": False})
    run(server.call_tool("revit_create_level", {"elevation": 3000}))
    run(server.call_tool("revit_create_duct", {"params": {"from": "x"}}))  # unusable field names fall back to params
    assert api.calls[-2] == ("POST", "/revit/call/create_level", {"params": {"elevation": 3000}})
    assert api.calls[-1] == ("POST", "/revit/call/create_duct", {"params": {"from": "x"}})


def test_b2m_and_gateway_tools_forward_to_the_local_service():
    api = FakeApi()
    server = build_server(api, {"toolsets": [], "read_only": False})
    run(server.call_tool("b2m_plan_from_brief", {"brief": "G+3 on 15 x 25 m"}))
    run(server.call_tool("b2m_plan_from_brief", {"brief_id": "b1", "answers": {"plot_depth_mm": "25 m"}}))
    run(server.call_tool("b2m_build", {"spec": {"x": 1}, "document_title": "Scratch"}))
    run(server.call_tool("b2m_run_status", {"run_id": "r1"}))
    run(server.call_tool("b2m_remove", {"run_id": "r1"}))
    run(server.call_tool("revit_switch_target", {"target": "2026"}))
    run(server.call_tool("revit_lookup_message", {"text": "DOCUMENT_CHANGED"}))
    assert api.calls == [
        ("POST", "/briefs", {"brief": "G+3 on 15 x 25 m"}),
        ("POST", "/briefs/b1/answers", {"answers": {"plot_depth_mm": "25 m"}}),
        ("POST", "/builds/from-spec", {"spec": {"x": 1}, "document_title": "Scratch", "brief_id": None, "override": False,
                                       "allow_family_create": False, "family_folders": []}),
        ("GET", "/runs/r1", None),
        ("POST", "/runs/r1/remove", {}),
        ("POST", "/revit/target", {"target": "2026", "verify": True}),
        ("GET", "/revit/lookup?text=DOCUMENT_CHANGED", None),
    ]


def test_import_tools_forward_to_the_local_service():
    api = FakeApi()
    server = build_server(api, {"toolsets": [], "read_only": False})
    run(server.call_tool("b2m_import_start", {"folder": "D:/Jobs/G4/dwg"}))
    run(server.call_tool("b2m_import_status", {"import_id": "i1"}))
    run(server.call_tool("b2m_import_questions", {"import_id": "i1"}))
    run(server.call_tool("b2m_import_answer", {"import_id": "i1", "question_id": "Q2", "answer": "3000"}))
    run(server.call_tool("b2m_import_set_location", {"import_id": "i1", "city": "Kolkata", "state": "West Bengal",
                                                     "country": "India"}))
    run(server.call_tool("b2m_import_assume", {"import_id": "i1", "question_ids": ["Q3", "Q4"]}))
    run(server.call_tool("b2m_import_assume", {"import_id": "i1", "question_ids": "all"}))
    run(server.call_tool("b2m_import_approve", {"import_id": "i1"}))
    run(server.call_tool("b2m_import_build", {"import_id": "i1", "document_title": "Scratch"}))
    assert api.calls == [
        ("POST", "/imports", {"folder": "D:/Jobs/G4/dwg"}),
        ("GET", "/imports/i1", None),
        ("GET", "/imports/i1/questions", None),
        ("POST", "/imports/i1/answers", {"question_id": "Q2", "answer": "3000"}),
        ("POST", "/imports/i1/location", {"city": "Kolkata", "municipality": None, "state": "West Bengal",
                                          "country": "India"}),
        ("POST", "/imports/i1/assume", {"question_ids": ["Q3", "Q4"]}),
        ("POST", "/imports/i1/assume", {"question_ids": "all"}),
        ("POST", "/imports/i1/approve", {"note": None}),
        ("POST", "/imports/i1/build", {"document_title": "Scratch"}),
    ]


def test_assume_tool_says_when_it_may_be_called():
    tools = {t.name: t for t in run(build_server(FakeApi(), {"toolsets": [], "read_only": False}).list_tools())}
    description = tools["b2m_import_assume"].description
    assert "NEVER call this unless the user explicitly said \"assume\"" in description
    assert "location" in description
