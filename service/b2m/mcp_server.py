"""MCP tools for Claude Desktop (PRD F11.1). A thin stdio server that forwards to the local B2M service,
so the service stays the only process that talks to Revit.

Claude Desktop config (claude_desktop_config.json):

    {"mcpServers": {"b2m": {"command": "<path>\\service\\.venv\\Scripts\\python.exe", "args": ["-m", "b2m.mcp_server"]}}}

The service must be running: python -m b2m.api
b2m_check and b2m_drawing_set join when compliance checks (Step 4) and drawings (Step 5) exist.
"""

from __future__ import annotations

import json
import urllib.error
import urllib.request
from typing import Any, Protocol

from mcp.server.mcpserver import MCPServer

from b2m.api import HOST, PORT


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


def build_server(api: Api | None = None) -> MCPServer:
    api = api or LocalApi()
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
    def b2m_build(spec: dict[str, Any], document_title: str, brief_id: str = "") -> dict[str, Any]:
        """Build the spec in the open Revit document with this title. Returns a run_id; follow it with b2m_run_status."""
        return api.call("POST", "/builds/from-spec", {"spec": spec, "document_title": document_title, "brief_id": brief_id or None})

    @server.tool()
    def b2m_run_status(run_id: str) -> dict[str, Any]:
        """Progress and result of a build: batches done, created elements, read-back counts and Revit warnings."""
        return api.call("GET", f"/runs/{run_id}")

    @server.tool()
    def b2m_remove(run_id: str) -> dict[str, Any]:
        """Delete everything a build run created, as one Revit undo step. Only that run's elements are touched."""
        return api.call("POST", f"/runs/{run_id}/remove", {})

    return server


def main() -> None:
    build_server().run("stdio")


if __name__ == "__main__":
    main()
