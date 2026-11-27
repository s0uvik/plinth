"""MCP tools for Claude Desktop (PRD F11.1). A thin stdio server that forwards to the local Plinth service,
so the service stays the only process that talks to Revit.

Claude Desktop config (claude_desktop_config.json):

    {"mcpServers": {"plinth": {"command": "<path>\\service\\.venv\\Scripts\\python.exe", "args": ["-m", "plinth.mcp_server"]}}}

The service must be running: python -m plinth.api
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

from plinth import paths
from plinth.api import HOST, PORT

# Toolsets exposed when no config says otherwise: the same small default rvt-mcp starts with.
DEFAULT_TOOLSETS = ["query", "create", "view", "meta"]


def load_config() -> dict[str, Any]:
    """%LOCALAPPDATA%\\Plinth\\mcp.json: {"toolsets": ["query", ...] or "all", "read_only": false}."""
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
            raise RuntimeError(f"Plinth service: {detail}") from e
        except urllib.error.URLError as e:
            raise RuntimeError(f"The Plinth service is not running at {self.base} (start it with: python -m plinth.api).") from e


def build_server(api: Api | None = None, config: dict[str, Any] | None = None) -> MCPServer:
    api = api or LocalApi()
    config = config or load_config()
    server = MCPServer(
        name="plinth",
        instructions=(
            "Plinth turns a written brief for a G+3/G+4 Kolkata apartment block into a native Revit model. "
            "Call plinth_plan_from_brief with the brief; if it returns questions, ask the user and call it again with "
            "brief_id and answers. Show the user the defaults and layout before calling plinth_build, and build only "
            "into the Revit document title the user confirms. "
            "To model from the office's CAD drawings, use the plinth_import_ tools: start, follow the status, put every "
            "question to the user (never answer one yourself), and point them to the review page "
            f"(http://{HOST}:{PORT}/import) to check the overlay before they approve. "
            "On a built import, plinth_plan_fitout plans furniture and MEP (nothing built; its MEP is an indicative "
            "coordination model an MEP engineer must design and check); build it with plinth_build_fitout only when "
            "the user says so."),
    )

    @server.tool()
    def plinth_plan_from_brief(brief: str = "", brief_id: str = "", answers: dict[str, str] | None = None) -> dict[str, Any]:
        """Read a brief and return questions for missing essentials, or the building spec with every default
        applied and a preview of the layout. To answer questions, pass the brief_id and answers keyed by field."""
        if brief_id:
            return api.call("POST", f"/briefs/{brief_id}/answers", {"answers": answers or {}})
        return api.call("POST", "/briefs", {"brief": brief})

    @server.tool()
    def plinth_preview(spec: dict[str, Any]) -> dict[str, Any]:
        """Check an edited building spec and preview its layout without building."""
        return api.call("POST", "/preview", {"spec": spec})

    @server.tool()
    def plinth_check(spec: dict[str, Any]) -> dict[str, Any]:
        """Check a building spec against the NBC 2016 and Kolkata rules (advisory). Lists every failure with the
        measured value, the requirement and the clause; rule values marked provisional are not yet verified."""
        return api.call("POST", "/check", {"spec": spec})

    @server.tool()
    def plinth_build(spec: dict[str, Any], document_title: str, brief_id: str = "", override: bool = False,
                  allow_family_create: bool = False, family_folders: list[str] | None = None) -> dict[str, Any]:
        """Build the spec in the open Revit document with this title. Hard compliance failures block the build unless
        the user explicitly chooses override. Returns a run_id; follow it with plinth_run_status. If the run ends with
        status needs_input, a column or beam family was not found: show the user its question and candidates, then
        build again with allow_family_create=true only if the user agrees, or with family_folders naming where their
        family files are. Never set allow_family_create without asking."""
        return api.call("POST", "/builds/from-spec", {"spec": spec, "document_title": document_title,
                                                       "brief_id": brief_id or None, "override": override,
                                                       "allow_family_create": allow_family_create,
                                                       "family_folders": family_folders or []})

    @server.tool()
    def plinth_run_status(run_id: str) -> dict[str, Any]:
        """Progress and result of a build: batches done, created elements, read-back counts and Revit warnings."""
        return api.call("GET", f"/runs/{run_id}")

    @server.tool()
    def plinth_drawing_set(run_id: str) -> dict[str, Any]:
        """Make the drawing set for a built run: plans, elevation, section, 3D, door/window schedules and the per-flat
        area statement on sheets, exported to one PDF with the compliance report merged in."""
        return api.call("POST", f"/runs/{run_id}/drawings", {})

    @server.tool()
    def plinth_remove(run_id: str) -> dict[str, Any]:
        """Delete everything a build run created, as one Revit undo step. Only that run's elements are touched."""
        return api.call("POST", f"/runs/{run_id}/remove", {})

    add_import_tools(server, api)
    add_gateway_tools(server, api, config["read_only"])
    add_catalog_tools(server, api, config)
    return server


def add_import_tools(server: MCPServer, api: Api) -> None:
    """Drawing import (PRD Step 9): DWG set -> review -> approved build. Nothing is guessed: what the drawings do not
    settle is a question for the user."""

    @server.tool()
    def plinth_import_start(folder: str) -> dict[str, Any]:
        """Start importing the DWG/DXF drawing set in this folder (on this PC). Returns an import_id; follow it with
        plinth_import_status until status is ready (or failed, with the error and a hint)."""
        return api.call("POST", "/imports", {"folder": folder})

    @server.tool()
    def plinth_import_status(import_id: str) -> dict[str, Any]:
        """Import progress and what was understood: each sheet's role, storeys, title, scale and page, recognition
        counts per sheet, cross-sheet registrations, the location, approval state and the readiness report."""
        return api.call("GET", f"/imports/{import_id}")

    @server.tool()
    def plinth_import_questions(import_id: str) -> dict[str, Any]:
        """The open and answered questions grouped by severity (blocking first), each with its options and evidence
        (sheet and drawing entity handles), plus the location, every assumption made so far and readiness. Put the
        questions to the user as written; never answer them yourself."""
        return api.call("GET", f"/imports/{import_id}/questions")

    @server.tool()
    def plinth_import_answer(import_id: str, question_id: str,
                          answer: str | int | float | dict[str, Any] | list[Any]) -> dict[str, Any]:
        """Record the user's own answer to one question (the value they gave, e.g. "3000", one of the options, or the
        structured value the question asks for, such as {"wood": 12, "pvc": 8}). Only pass what the user said. An
        answer after approval withdraws the approval, except answers to drawing-set and estimate questions."""
        return api.call("POST", f"/imports/{import_id}/answers", {"question_id": question_id, "answer": answer})

    @server.tool()
    def plinth_import_set_location(import_id: str, city: str, state: str, country: str,
                                municipality: str = "") -> dict[str, Any]:
        """Set the project location as the user gave it (city, state, country, and the municipality if they know
        it). Assumptions come from that location's rules, so it is needed before anything can be assumed."""
        return api.call("POST", f"/imports/{import_id}/location", {"city": city, "municipality": municipality or None,
                                                                   "state": state, "country": country})

    @server.tool()
    def plinth_import_assume(import_id: str, question_ids: list[str] | str) -> dict[str, Any]:
        """Assume answers for these questions (a list of ids, or "all" for every open one) from the location's rules
        and typical practice. NEVER call this unless the user explicitly said "assume", and only after the location
        is set (ask the user for it first and call plinth_import_set_location; without it this fails with 409). Read
        every assumption in the response back to the user."""
        return api.call("POST", f"/imports/{import_id}/assume", {"question_ids": question_ids})

    @server.tool()
    def plinth_import_approve(import_id: str, note: str = "") -> dict[str, Any]:
        """Record the user's approval of the review (what Plinth understood from the drawings, the answers and the
        assumptions). Call it only when the user says they approve; it is refused while blocking questions remain."""
        return api.call("POST", f"/imports/{import_id}/approve", {"note": note or None})

    @server.tool()
    def plinth_import_build(import_id: str, document_title: str) -> dict[str, Any]:
        """Build the approved import in the open Revit document with this title (confirm the title with the user).
        If inputs are still missing it fails and lists them: ask the user. Returns a run_id for plinth_run_status."""
        return api.call("POST", f"/imports/{import_id}/build", {"document_title": document_title})

    @server.tool()
    def plinth_import_selfcheck(run_id: str) -> dict[str, Any]:
        """The self-check of a build from drawings: the model read back and compared with the plan and the drawings.
        Returns the run with its selfcheck summary (unexplained and explained differences, accounting of every
        recognised element) and the report's address (selfcheck.html). "Done" means zero unexplained."""
        record = api.call("GET", f"/runs/{run_id}")
        return {"run_id": run_id, "status": record.get("status"), "selfcheck": record.get("selfcheck"),
                "checks": record.get("checks"), "report": f"/runs/{run_id}/selfcheck.html"}

    @server.tool()
    def plinth_import_drawings(import_id: str, run_id: str, remake: bool = False) -> dict[str, Any]:
        """Reproduce the source sheet set in Revit for a run built from this import: sheets in source order, views on
        the source cut lines, dimensions and tags from the source, one PDF and a comparison with the source. If it
        needs answers (e.g. which way the elevation looks, which title block) it fails with 409 and the questions,
        which are import questions now: ask the user, answer with plinth_import_answer, then call this again. The title
        block fields (project name, drawn by ...) are asked too but never stop the set. A run that already has a set
        answers 409; remake=true (only when the user asks to redo it) deletes that set's own sheets and views and
        makes it again."""
        return api.call("POST", f"/imports/{import_id}/drawings", {"run_id": run_id, "remake": remake})

    @server.tool()
    def plinth_import_estimate(import_id: str, run_id: str = "", reference_xlsx: str = "", rules: str = "") -> dict[str, Any]:
        """The door/window estimate in the office's Excel layout (live formulas), measured from the built model of
        run_id (or, without one, from the drawings as answered), with the office's rules (rules: a rules file path;
        default the project's, else office-default). It is cross-checked against the drawings and, given the
        office's own estimate workbook (reference_xlsx), compared with it cell by cell. Materials and railing
        lengths are on no drawing: they come back as questions (import questions); ask the user, answer, run again."""
        body = {"run_id": run_id or None, "reference_xlsx": reference_xlsx or None, "rules": rules or None}
        return api.call("POST", f"/imports/{import_id}/estimate", body)

    add_fitout_tools(server, api)


FITOUT_NOTICE = ("The MEP is an indicative coordination model laid out from rules, not an engineer's design: an MEP "
                 "engineer must design and check the real systems. Always tell the user so.")


def _fitout_summary(view: dict[str, Any]) -> dict[str, Any]:
    """A plan as Claude reports it: what will be built, what waits on which question, what is left out and why
    (lists cut to their first lines; the review page shows them all)."""
    r = view["report"]
    mep = r.get("mep") or {}

    def first(rows: list, n: int = 25) -> dict[str, Any]:
        return {"total": len(rows), "first": rows[:n]}

    return {
        **{k: view.get(k) for k in ("run_id", "document_title", "rules", "rules_from", "planned_utc", "ready",
                                     "buildable", "ops", "built_as", "stale")},
        "notice": r.get("notice"), "systems": r.get("systems"), "flat": r.get("flat"), "level": r.get("level"),
        "furniture_run": r.get("furniture_run"), "op_counts": r.get("ops"),
        "furniture": {item: x["in_plan"] for item, x in (r.get("items") or {}).items()},
        "mep_counts": mep.get("counts"), "water": mep.get("water"),
        "unplaced": first([f"{u['what']}: {u['reason']}" for u in mep.get("unplaced") or []]),
        "unrouted": first([f"{u['what']}: {u['reason']}" for u in mep.get("unrouted") or []]),
        "audit_problems": first([str(p) for p in mep.get("problems") or []]),
        "assumptions": [f"{a['id']}: {a['text']}" for a in r.get("assumptions") or []],
        "left_out_of_plan": r.get("left_out_of_plan"), "not_built_now": r.get("dropped"),
        "furniture_check": r.get("furniture_check"),
        "questions": view.get("questions") or r.get("questions"),
    }


def add_fitout_tools(server: MCPServer, api: Api) -> None:
    """Fit-out (PRD Step 10): furniture and MEP on a built run of an import. Planning builds nothing; its questions are
    import questions (answered with plinth_import_answer); building is a separate tool the user must ask for."""

    @server.tool(description=(
        "Plan the fit-out (furniture, plumbing and drainage, electrical, HVAC, fire) on a built run of this import, in "
        "the open Revit document the building was built in (confirm the title with the user). It reads the document "
        "(family types and their sizes) and builds NOTHING. systems: any of furniture, plumbing, electrical, hvac, fire "
        "(default all); furniture_run: an earlier fit-out run whose furniture is built (the MEP joins it, never places "
        "it again; build furniture first, then the MEP on top); flat and level: a pilot of one flat (suggest piloting "
        "one flat first); exclude_runs: pilot runs whose elements are joined, not built again; rules: the fit-out rule "
        "set, default the project location's. Returns what would be built, the assumptions, what is left out and why, "
        "and the open questions, which are import questions now: put each to the user as written (never answer one "
        "yourself), record the user's answers with plinth_import_answer (a family as {\"family\": ..., \"type\": ...}, "
        "\"skip\" to leave the item out, {\"create\": \"box\"} only if the user agrees Plinth creates a plain box "
        "family), then plan again. " + FITOUT_NOTICE + " If the furniture of furniture_run moved, it fails with the "
        "items that differ and what to do."))
    def plinth_plan_fitout(import_id: str, run_id: str, document_title: str, systems: list[str] | None = None,
                           furniture_run: str = "", flat: str = "", level: str = "",
                           exclude_runs: list[str] | None = None, rules: str = "",
                           answers: dict[str, Any] | None = None) -> dict[str, Any]:
        body = {"run_id": run_id, "document_title": document_title, "systems": systems or None,
                "furniture_run": furniture_run or None, "flat": flat or None, "level": level or None,
                "exclude_runs": exclude_runs or [], "rules": rules or None, "answers": answers or {}}
        return _fitout_summary(api.call("POST", f"/imports/{import_id}/fitout/plan", body))

    @server.tool(description=(
        "Build the reviewed fit-out plan of this building run (the last plinth_plan_fitout) as its own run, removable "
        "on its own. Calling this IS the user's go-ahead: show the user the plan summary (what is built, assumptions, "
        "what is left out, the MEP notice) and call it ONLY when the user explicitly says to build. It is refused "
        "without a plan, in another document, or with open questions unless partial=true (build what is ready; ask "
        "the user before choosing partial). Returns a run_id: follow it with plinth_run_status until it is built or "
        "built_with_issues. Issues (joins and circuits Revit refused on families without connectors) are expected on "
        "real families and are not failures: report them. To undo it, plinth_remove with that run_id (only its "
        "elements are deleted; remove the MEP run before the furniture run it was built on). " + FITOUT_NOTICE))
    def plinth_build_fitout(import_id: str, run_id: str, document_title: str, partial: bool = False) -> dict[str, Any]:
        out = api.call("POST", f"/imports/{import_id}/fitout/build",
                       {"run_id": run_id, "document_title": document_title, "partial": partial})
        return {**out, "follow_with": "plinth_run_status", "status_url": f"/runs/{out.get('run_id')}"}

    @server.tool(description=(
        "The fit-out of this import: the built runs it can go on, the latest plan's summary (with whether answers were "
        "given since: then plan again) and every fit-out run with its status, elements and issues."))
    def plinth_fitout_status(import_id: str) -> dict[str, Any]:
        state = api.call("GET", f"/imports/{import_id}/fitout")
        plan = state.get("plan")
        return {**state, "plan": _fitout_summary(plan) if plan else None}


def add_gateway_tools(server: MCPServer, api: Api, read_only: bool) -> None:
    """The tools rvt-mcp ran in its own gateway, now served by the Plinth service (F11.3)."""
    reads = ToolAnnotations(readOnlyHint=True)
    writes = ToolAnnotations(readOnlyHint=False)

    def revit_list_available_targets() -> list[dict[str, Any]]:
        """Running Revit processes with the Plinth add-in: pid, Revit year, add-in version, which one is pinned."""
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
        """Explain a Plinth error code or message in plain words."""
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
    list always matches what is installed. Without Revit running, only the plinth_ and gateway tools load."""
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
