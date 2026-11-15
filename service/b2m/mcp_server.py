"""MCP tools for Claude Desktop (PRD F11.1). A thin stdio server that forwards to the local B2M service,
so the service stays the only process that talks to Revit.

Claude Desktop config (claude_desktop_config.json):

    {"mcpServers": {"b2m": {"command": "<path>\\service\\.venv\\Scripts\\python.exe", "args": ["-m", "b2m.mcp_server"]}}}

The service must be running: python -m b2m.api
"""

from __future__ import annotations

import inspect
import json
import keyword
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Protocol

from mcp.server.mcpserver import MCPServer
from mcp.types import ToolAnnotations

from b2m import paths
from b2m.api import HOST, PORT

# Toolsets exposed when no config says otherwise: the same small default rvt-mcp starts with.
DEFAULT_TOOLSETS = ["query", "create", "view", "meta"]


def load_config() -> dict[str, Any]:
    """%LOCALAPPDATA%\\B2M\\mcp.json: {"toolsets": ["query", ...] or "all", "read_only": false}."""
    try:
        data = json.loads((paths.root() / "mcp.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        data = {}
    return {"toolsets": data.get("toolsets", DEFAULT_TOOLSETS), "read_only": bool(data.get("read_only", False))}


class Api(Protocol):
    def call(self, method: str, path: str, body: dict[str, Any] | None = None) -> Any: ...


class LocalApi:
    def __init__(self, base: str = f"http://{HOST}:{PORT}"):
        self.base = base

    def call(self, method: str, path: str, body: dict[str, Any] | None = None) -> Any:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(self.base + path, data=data, method=method,
                                         headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=600) as response:
                return json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            detail = json.loads(e.read().decode("utf-8") or "{}").get("detail")
            raise RuntimeError(f"B2M service: {detail}") from e
        except urllib.error.URLError as e:
            raise RuntimeError(f"The B2M service is not running at {self.base} (start it with: python -m b2m.api).") from e


def build_server(api: Api | None = None, config: dict[str, Any] | None = None) -> MCPServer:
    api = api or LocalApi()
    config = config or load_config()
    server = MCPServer(
        name="b2m",
        instructions=(
            "B2M turns a written brief for a G+3/G+4 Kolkata apartment block into a native Revit model. "
            "Call b2m_plan_from_brief with the brief; if it returns questions, ask the user and call it again with "
            "brief_id and answers. Show the user the defaults and layout before calling b2m_build, and build only "
            "into the Revit document title the user confirms."),
    )

    @server.tool()
    def b2m_plan_from_brief(brief: str = "", brief_id: str = "", answers: dict[str, str] | None = None) -> dict[str, Any]:
        """Read a brief and return questions for missing essentials, or the building spec with every default
        applied and a preview of the layout. To answer questions, pass the brief_id and answers keyed by field."""
        if brief_id:
            return api.call("POST", f"/briefs/{brief_id}/answers", {"answers": answers or {}})
        return api.call("POST", "/briefs", {"brief": brief})

    @server.tool()
    def b2m_preview(spec: dict[str, Any]) -> dict[str, Any]:
        """Check an edited building spec and preview its layout without building."""
        return api.call("POST", "/preview", {"spec": spec})

    @server.tool()
    def b2m_check(spec: dict[str, Any]) -> dict[str, Any]:
        """Check a building spec against the NBC 2016 and Kolkata rules (advisory). Lists every failure with the
        measured value, the requirement and the clause; rule values marked provisional are not yet verified."""
        return api.call("POST", "/check", {"spec": spec})

    @server.tool()
    def b2m_build(spec: dict[str, Any], document_title: str, brief_id: str = "", override: bool = False) -> dict[str, Any]:
        """Build the spec in the open Revit document with this title. Hard compliance failures block the build unless
        the user explicitly chooses override. Returns a run_id; follow it with b2m_run_status."""
        return api.call("POST", "/builds/from-spec", {"spec": spec, "document_title": document_title,
                                                       "brief_id": brief_id or None, "override": override})

    @server.tool()
    def b2m_run_status(run_id: str) -> dict[str, Any]:
        """Progress and result of a build: batches done, created elements, read-back counts and Revit warnings."""
        return api.call("GET", f"/runs/{run_id}")

    @server.tool()
    def b2m_drawing_set(run_id: str) -> dict[str, Any]:
        """Make the drawing set for a built run: plans, elevation, section, 3D, door/window schedules and the per-flat
        area statement on sheets, exported to one PDF with the compliance report merged in."""
        return api.call("POST", f"/runs/{run_id}/drawings", {})

    @server.tool()
    def b2m_remove(run_id: str) -> dict[str, Any]:
        """Delete everything a build run created, as one Revit undo step. Only that run's elements are touched."""
        return api.call("POST", f"/runs/{run_id}/remove", {})

    add_gateway_tools(server, api, config["read_only"])
    add_catalog_tools(server, api, config)
    return server


def add_gateway_tools(server: MCPServer, api: Api, read_only: bool) -> None:
    """The tools rvt-mcp ran in its own gateway, now served by the B2M service (F11.3)."""
    reads = ToolAnnotations(readOnlyHint=True)
    writes = ToolAnnotations(readOnlyHint=False)

    def revit_list_available_targets() -> list[dict[str, Any]]:
        """Running Revit processes with the B2M add-in: pid, Revit year, add-in version, which one is pinned."""
        return api.call("GET", "/revit/targets")

    def revit_get_current_target() -> dict[str, Any]:
        """The pinned Revit process and whether its document is confirmed for writes."""
        return api.call("GET", "/revit/target")

    def revit_switch_target(target: str = "auto", verify: bool = True) -> dict[str, Any]:
        """Pin a Revit process ("auto", "pid:<n>" or a year like "2026"). verify confirms the active document, which
        every model-changing revit_ tool needs; check with the user that it is the right document."""
        return api.call("POST", "/revit/target", {"target": target, "verify": verify})

    def revit_health() -> dict[str, Any]:
        """Service version and the add-in's health: Revit year, process, open document, commands."""
        return api.call("GET", "/health")

    def revit_get_operation_status(call_id: str) -> dict[str, Any]:
        """What happened to an earlier revit_ call (by its call_id), including calls that timed out."""
        return api.call("GET", f"/revit/operations/{call_id}")

    def revit_lookup_message(text: str) -> dict[str, Any]:
        """Explain a B2M error code or message in plain words."""
        return api.call("GET", "/revit/lookup?" + urllib.parse.urlencode({"text": text}))

    def revit_analyze_usage_patterns(last: int = 1000) -> dict[str, Any]:
        """Which revit_ tools were used, how often they failed and how long they took."""
        return api.call("GET", f"/revit/usage?last={int(last)}")

    def revit_get_failure_records(last: int = 100) -> list[dict[str, Any]]:
        """Recent calls that failed or where Revit reported warnings or errors."""
        return api.call("GET", f"/revit/failures?last={int(last)}")

    def revit_get_change_records(document_title: str = "", since: str = "") -> list[dict[str, Any]]:
        """Recorded reasons for model changes, optionally for one document (following Save As aliases) and since a UTC time."""
        query = {k: v for k, v in (("document_title", document_title), ("since", since)) if v}
        return api.call("GET", "/revit/changes" + ("?" + urllib.parse.urlencode(query) if query else ""))

    def revit_record_change(call_ids: list[str], reason: str) -> dict[str, Any]:
        """Attach the user's reason to earlier revit_ calls (by call_id). Never invent a reason."""
        return api.call("POST", "/revit/changes", {"call_ids": call_ids, "reason": reason})

    def revit_resolve_history_identity(old_title: str, new_title: str) -> dict[str, Any]:
        """After Save As or a copy, record that new_title continues old_title's change history."""
        return api.call("POST", "/revit/history-identity", {"old_title": old_title, "new_title": new_title})

    def revit_list_code_modules() -> list[dict[str, Any]]:
        """Stored C# helper modules for send_code (developer mode)."""
        return api.call("GET", "/revit/code-modules")

    def revit_save_code_module(name: str, code: str, overwrite: bool = False) -> dict[str, Any]:
        """Save a C# helper module (developer mode only)."""
        return api.call("POST", "/revit/code-modules", {"name": name, "code": code, "overwrite": overwrite})

    def revit_delete_code_module(name: str) -> dict[str, Any]:
        """Delete a stored C# helper module (developer mode only)."""
        return api.call("DELETE", f"/revit/code-modules/{urllib.parse.quote(name)}")

    def revit_list_bake_suggestions() -> list[dict[str, Any]]:
        """send_code snippets used often enough to be worth saving as a baked tool."""
        return api.call("GET", "/revit/bake-suggestions")

    def revit_accept_bake_suggestion(suggestion_id: str, name: str, description: str) -> dict[str, Any]:
        """Save a suggested snippet as a baked tool (developer mode only)."""
        return api.call("POST", f"/revit/bake-suggestions/{suggestion_id}/accept", {"name": name, "description": description})

    def revit_dismiss_bake_suggestion(suggestion_id: str) -> dict[str, Any]:
        """Stop suggesting a snippet."""
        return api.call("POST", f"/revit/bake-suggestions/{suggestion_id}/dismiss", {})

    for fn in (revit_list_available_targets, revit_get_current_target, revit_health, revit_get_operation_status,
               revit_lookup_message, revit_analyze_usage_patterns, revit_get_failure_records, revit_get_change_records,
               revit_list_code_modules, revit_list_bake_suggestions):
        server.add_tool(fn, name=fn.__name__, description=fn.__doc__, annotations=reads)
    if read_only:
        return
    for fn in (revit_switch_target, revit_record_change, revit_resolve_history_identity, revit_save_code_module,
               revit_delete_code_module, revit_accept_bake_suggestion, revit_dismiss_bake_suggestion):
        server.add_tool(fn, name=fn.__name__, description=fn.__doc__, annotations=writes)


def add_catalog_tools(server: MCPServer, api: Api, config: dict[str, Any]) -> list[str]:
    """One revit_<command> tool per add-in command in the enabled toolsets (F11.2), read from the running add-in so the
    list always matches what is installed. Without Revit running, only the b2m_ and gateway tools load."""
    try:
        commands = api.call("GET", "/revit/commands")
    except RuntimeError:
        return []
    wanted = config["toolsets"]
    added = []
    for cmd in commands:
        if wanted != "all" and cmd["toolset"] not in wanted:
            continue
        if config["read_only"] and not cmd["read_only"]:
            continue
        server.add_tool(_catalog_function(api, cmd), name=f"revit_{cmd['name']}",
                        description=f"{cmd['description']}\n\nParameters (JSON Schema): {json.dumps(cmd['schema'])}",
                        annotations=ToolAnnotations(readOnlyHint=cmd["read_only"], destructiveHint=not cmd["read_only"]))
        added.append(cmd["name"])
    return added


def _catalog_function(api: Api, cmd: dict[str, Any]):
    """A function whose keyword parameters mirror the command's schema; the add-in validates every call strictly."""
    schema = cmd.get("schema") or {}
    names = list((schema.get("properties") or {}).keys())
    required = set(schema.get("required") or [])

    if all(n.isidentifier() and not keyword.iskeyword(n) for n in names):
        def call(**kwargs: Any) -> dict[str, Any]:
            params = {k: v for k, v in kwargs.items() if v is not None}
            return api.call("POST", f"/revit/call/{cmd['name']}", {"params": params})

        call.__signature__ = inspect.Signature([
            inspect.Parameter(n, inspect.Parameter.KEYWORD_ONLY, annotation=Any,
                              default=inspect.Parameter.empty if n in required else None) for n in names])
    else:
        def call(params: dict[str, Any] | None = None) -> dict[str, Any]:
            return api.call("POST", f"/revit/call/{cmd['name']}", {"params": params or {}})
    call.__name__ = f"revit_{cmd['name']}"
    return call


def main() -> None:
    build_server().run("stdio")


if __name__ == "__main__":
    main()
