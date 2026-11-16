# PRD: Revit Brief-to-Model

Nov 15, 2026 · @Souvik

## Summary

**Product:** a tool for Revit 2024 through 2028 that turns a written brief for a G+3/G+4 Indian apartment block into a complete native Revit model, an NBC 2016 and Kolkata compliance report, and a PDF drawing set, in minutes rather than days.

**Problem:** a first-draft Revit model of an apartment block takes days of manual modeling. Tools that generate whole buildings work outside Revit, and tools inside Revit create one element or one sheet per request. None check against Indian codes.

**Solution:** Claude turns the brief into a structured building spec. A deterministic Python layout engine turns the spec into exact geometry and a build plan. **Your own Revit add-in**, built from components copied out of the open-source rvt-mcp project, executes the build plan inside Revit. The service then checks the result and produces the drawing set.

**Key decision: copy, don't connect.** The product never installs or calls rvt-mcp. Instead it copies rvt-mcp's source (Apache-2.0) into its own add-in: the named-pipe bridge, request queue, one-undo batches, failure handling and **all 256 Revit tools**, each reviewed and improved where it needs it, plus 6 tools rvt-mcp lacks, for 262 in all (Appendix A). You ship one add-in you fully own, with no outside gateway process and no MCP hop between your service and Revit. The 17 tools that ran in rvt-mcp's .NET gateway move into your Python service. The code-execution tools (`send_code_to_revit`, ToolBaker, code modules) are kept but sit behind a developer mode that is off by default, so live projects stay safe.

**Two tracks.** The brief-to-model flow needs 23 of the ported tools and three of the new ones; those ship first, on the original pilot timeline. The other tools are ported in Step 8, after the pilot.

**Success metrics**

- A brief becomes a built model, report and PDF set in under 5 minutes.
- 18 of 20 test briefs produce a valid model with zero Revit errors.
- The compliance report misses no failures an architect finds on five test projects.
- At least two of three pilot firms say they would pay.

**Timeline:** 35 weeks part-time, 16 Nov 2026 to 18 Jul 2027, in nine steps. Steps 0–7 take the brief-to-model flow from a porting spike to a pilot by 25 Apr 2027; Step 8 ports the rest of the tool catalog by 18 Jul 2027.

## Users and stories

The primary user is an architect at a small or mid-sized Kolkata firm who models apartment blocks in Revit. Secondary users are builders who commission options, and the firm's BIM manager, who sets the tool up.

| # | As a… | I want… | So that… | Priority |
| --- | --- | --- | --- | --- |
| 1 | Architect | to type a brief and get a native Revit model | I keep working in Revit, not in another tool | Must |
| 2 | Architect | to see every assumption before the build starts | wrong defaults are caught before they cost time | Must |
| 3 | Architect | to be asked only for essentials: plot size, floors, unit mix | the brief stays short | Must |
| 4 | Architect | a compliance report with clause references | I fix problems before submission | Must |
| 5 | Architect | to undo a generated model in one step | a bad run costs nothing | Must |
| 6 | Architect | a PDF set with plans and a per-flat area statement | I can send options to the client the same day | Must |
| 7 | Builder | two or three options from one brief | I can compare unit mixes and layouts quickly | Should |
| 8 | Architect | to edit the spec and rebuild | small changes don't mean starting over | Should |
| 9 | BIM manager | the tool to use our wall types, doors and title block | output matches firm standards | Should |
| 10 | BIM manager | no arbitrary code execution and no writes to the wrong model | the tool is safe on live projects | Must |

## Scope

**In scope for v1**

- Building type: G+3/G+4 walk-up apartments on rectangular plots, with stilt parking or flats on the ground floor.
- Unit types: 1BHK, 2BHK and 3BHK, from three layout templates.
- Output: native Revit levels, grids, walls, floors, doors, windows, rooms and a stair core (walls and floor opening).
- Checks: NBC 2016 room sizes, widths and heights; Kolkata setbacks, ground coverage, FAR and height; geometry checks.
- Drawings: plans, one elevation, one section, a 3D view, door and window schedules, per-flat area statement and the compliance report, as one PDF.
- Interfaces: a local web page, and the same functions as MCP tools for Claude Desktop.
- Revit tool catalog: all 256 rvt-mcp tools, improved where needed, plus 6 new tools (262 in all), as add-in commands and as MCP tools for Claude Desktop, grouped into toolsets that can be switched on and off. The full list is in Appendix A.

**Non-goals for v1**

- Irregular plots, basements, other building types, and other cities.
- Revit stair elements, structure, MEP, dimensions and detailed annotation in the generated model. The structural, MEP and annotation tools are still ported and available to users through Claude Desktop; the brief-to-model flow just doesn't use them.
- Conversational design edits; users edit the spec and rebuild instead.
- Submission to authorities. The report is advisory.
- Cloud or multi-user hosting; everything runs on the user's Windows machine.

**Assumptions**

- Revit 2024, 2025, 2026 and 2027 on Windows at launch, and Revit 2028 once it is released and your add-in has a 2028 build; one Revit instance open during a build. Development and the spike run on Revit 2026.
- Code copied from rvt-mcp at one recorded commit, kept under its Apache-2.0 licence with attribution. Users never install rvt-mcp. The tool catalog in Appendix A was counted at commit `b44ee0b` (12 Nov 2026); Step 0 pins the final commit.
- Claude Sonnet 5.5 through the Anthropic API for brief understanding; internet access is required for that step only.
- A project template (.rte) and family pack ship with the product and supply the standard types. Both are saved in Revit 2024, the oldest supported version, because Revit files open only in the same or a newer version.
- One developer working 15–20 hours a week.

## System architecture

Two programs you own run on the user's machine: the Python service, and your add-in inside Revit. They talk directly over a local named pipe (loopback TCP on Revit 2024) with a session token, using the request format copied from rvt-mcp. Only the brief text goes to the cloud.

&#91;embedded content: architecture · Python service and your Revit add-in\]

The orchestrator is plain Python calling components in order; it is not an autonomous agent. Claude is used in exactly one place, turning the brief into a spec, which keeps runs predictable and cheap.

## End-to-end flow

A run has ten steps. Three stop points keep a bad input from reaching Revit, and keep a failed build from leaving a mess.

&#91;embedded content: flow · brief to drawing set, with three stop points\]

Steps 1–5 never touch Revit, so most problems are caught in seconds. Steps 6–10 go through your add-in; the add-in section lists the commands each one uses.

## Functional requirements

Twelve components: eleven in the Python service, plus your Revit add-in (F12). IDs are referenced in the delivery steps and tests.

| ID | Component | Requirement |
| --- | --- | --- |
| F1.1 | Web page | A brief box, a questions panel, an assumptions panel, a build button, and a results panel with summary, warnings, report and 3D image |
| F1.2 | Web page | Shows build progress per batch (“batch 3 of 9”) |
| F2.1 | Brief agent | Claude answers only through `submit_spec` or `ask_user` tools; never free text |
| F2.2 | Brief agent | Asks for plot size, upper floors and unit mix when missing; never invents them |
| F2.3 | Brief agent | Returns the list of defaults it applied, shown before the build |
| F3.1 | Spec validator | Validates every spec against the Pydantic schema; returns errors Claude can fix, up to two retries |
| F4.1 | Layout engine | Computes the buildable envelope, fits a template, scales unit plans and emits wall, opening and room geometry |
| F4.2 | Layout engine | Deterministic: the same spec always yields the same build plan |
| F4.3 | Layout engine | Tries the next template variant when validation fails; reports why if none fit |
| F5.1 | Pre-build checker | Runs NBC and Kolkata rules on engine geometry; hard failures block the build unless overridden |
| F6.1 | Revit client | Reads the add-in's discovery file, connects over the named pipe (loopback TCP on Revit 2024) with the session token, and checks the add-in's version and command list at startup |
| F6.2 | Revit client | Maps each build-plan operation to an add-in command, converting units and type names |
| F6.3 | Revit client | Confirms the target Revit process and document before any write |
| F7.1 | Build runner | Splits the build plan into `batch_execute` commands of at most 20 operations, in dependency order |
| F7.2 | Build runner | Stops at the first failed batch, reports what was built, and offers to undo |
| F7.3 | Build runner | After the build, reads back element counts and compares them with the plan |
| F8.1 | Post-build checker | Re-runs room and opening rules on data read from Revit |
| F9.1 | Drawing builder | Creates views, tags rooms, builds schedules and sheets, and exports one PDF |
| F9.2 | Drawing builder | Merges the compliance report PDF into the set |
| F10.1 | Run store | Saves brief, spec, build plan, tool results, report and versions for every run, so runs can be replayed |
| F11.1 | MCP facade | Exposes `b2m_plan_from_brief`, `b2m_build`, `b2m_check` and `b2m_drawing_set` to Claude Desktop |
| F11.2 | MCP facade | Exposes every catalog tool as a `revit_` MCP tool, grouped by toolset; a small default set is on, the rest are switched on in config; a read-only mode exposes only read tools |
| F11.3 | MCP facade | Implements the 17 tools that ran in rvt-mcp's .NET gateway (targets, health, operation status, failure and change records, message lookup, usage, code modules, bake suggestions) |
| F12.1 | Revit add-in | Loads in Revit 2024–2027 (2028 later), starts the pipe or TCP server on a background thread, and writes a discovery file with pipe name, token and process ID |
| F12.2 | Revit add-in | Queues each request and runs it on Revit's main thread through one ExternalEvent; validates arguments against each command's schema first |
| F12.3 | Revit add-in | batch\_execute runs up to 20 commands in one TransactionGroup; any Revit warning or error rolls the whole batch back |
| F12.4 | Revit add-in | Refuses writes unless the active document matches the one the run confirmed; code-execution commands (`send_code_to_revit`, baked tools, code modules) refuse to run unless developer mode is on, it is off by default, and the build flow never calls them |
| F12.5 | Revit add-in | Carries all 262 catalog commands (Appendix A) in every year's build, each meeting the porting standard |

## Revit add-in, built from rvt-mcp parts

The product ships its own add-in, `B2M.Revit`, with one build per Revit year. Nothing connects to rvt-mcp at runtime: the add-in's core and all 256 of rvt-mcp's tools are copied from its source at one recorded commit, reviewed, improved where needed and renamed. Your Python service talks to the add-in directly, with no MCP server in between.

### What to copy

| rvt-mcp source | Your module | Keep or change |
| --- | --- | --- |
| `src/shared/Transport/` (`ITransportServer`, `PipeTransportServer`, `TcpTransportServer`, `TransportResponseWriter`) | `Bridge/Transport` | Keep; rename namespaces; TCP only in the 2024 build |
| `src/shared/Infrastructure/Execution/` (`McpEventHandler`, `CommandDispatcher`, `IRevitCommand`, `PendingRequestLifecycle`, `RequestWait`, `DocumentTargetGuard`, `CommandOutcome`) | `Bridge/Execution` | Keep; drop the script-execution states |
| `src/shared/Handlers/Runtime/` (`BatchExecuteHandler`, `BatchExecutor`) | `Commands/Runtime` | Keep, including the rule that `send_code` and baked tools can't run inside a batch |
| `src/shared/Handlers/` (all 24 toolset folders, 239 handler files) | `Commands/<Toolset>` | Port every handler to the porting standard below; improve the ones flagged in Appendix A |
| Support folders the handlers use: `Lint/`, `Survey/`, `Documents/`, `Reports/`, `ToolBaker/` | Same names under `B2M.Revit` | Keep what the tools need; Reports keeps a minimal window for `report_task_result`; ToolBaker runs only in developer mode |
| `src/server/Tools/` and `src/server/Handlers/` (the 17 gateway-side tools) | Python service, `mcp_facade/` | Rewrite in Python against the same inputs and outputs; this is the one part that isn't a straight copy |
| `src/shared/Infrastructure/SafeFailuresPreprocessor.cs`, `src/shared/Failures/FailurePolicy.cs` | `Bridge/Failures` | Keep rollback on warnings; replace the SQLite failure ledger with a JSON log |
| `src/shared/Infrastructure/SchemaValidator.cs`, `RevitCompat.cs` | `Bridge` | Keep as is |
| `src/shared/Security/` (`AuthToken`, `DiscoveryPublisher`, `ErrorSanitizer`) | `Bridge/Security` | Keep; write discovery files to your own folder under `%LOCALAPPDATA%` |
| `src/plugin-r24` to `src/plugin-r27` project files | `addin/B2M.Revit.R24` to `R27` | Keep the pattern: one project per year compiling shared source, year flags in `DefineConstants`, Nice3point.Revit.Api packages for references |

### Tool catalog

The add-in carries every rvt-mcp tool: 256 tools in 24 toolsets, plus 6 new ones, 262 in all. Appendix A lists each tool with its access level, whether the flow uses it, and the extra work it needs.

| Toolset | Tools | Covers |
| --- | --- | --- |
| create | 7 | Levels, grids, walls, floors and ceilings, doors and windows, rooms, groups |
| delete | 1 | Delete by ID |
| rooms | 10 | Rooms, areas, spaces, boundaries, openings, finishes, separators |
| view | 11 | Create views, crop, scale, style, camera, capture image |
| sheets | 14 | Sheets, title blocks, revisions, renumbering, viewport layout |
| schedule | 10 | Create and read schedules, fields, filters, formulas |
| export | 16 | PDF, DWG, DGN, DWF, IFC, NWC, FBX, gbXML, images, CSV, sheet sets |
| annotation | 14 | Tags, text, dimensions, detail lines, filled regions, keynotes |
| query | 17 | Selection, element details, parameters, relationships, groups, worksets |
| modify | 9 | Move, rotate, change type, set parameters, worksets, wall tops |
| families | 10 | Load, unload, list, duplicate, rename, replace, audit, export `.rfa` |
| geometry | 13 | Bounding boxes, area and volume, clashes, raycasts, DirectShape meshes |
| graphics | 12 | View filters, overrides, category visibility, phases |
| materials | 10 | Create, edit, assign, takeoff |
| parameters | 8 | Project and shared parameters, bindings |
| organization | 10 | View templates, saved selections |
| links | 12 | Revit and CAD links, shared coordinates, base point |
| documents | 3 | Save and checkpoints |
| lint | 4 | Warnings summary, view naming, firm profiles |
| workflows | 8 | Composite audit, clash, sheet set, takeoff and cleanup flows |
| structural | 12 | Columns, beams, foundations, rebar, loads |
| mep | 16 | Ducts, pipes, conduit, cable tray, fittings, systems, panels |
| meta | 24 | Batch execute, health, targets, document state, project info, history, failure records, `send_code_to_revit`, code modules |
| toolbaker | 5 | Baked tools and bake suggestions |
| **new** | **6** | Tools rvt-mcp lacks; see below |

### Tools the brief-to-model flow uses

The original 22 tools, plus `analyze_model_statistics` for read-back and the new `create_floor_opening`, `list_levels` and `list_grids`. These are ported first (Steps 0, 1 and 5) and carry most of the improvements.

| Command | Used for | Improvement |
| --- | --- | --- |
| `create_level`, `create_grid` | Levels and grids | Porting standard only |
| `create_line_based_element` | Walls | Add location line, base and top offsets and a top level |
| `create_surface_based_element` | Floors and slabs | Porting standard only |
| `create_floor_opening` (new) | Stair-core shaft through each floor | New tool |
| `create_point_based_element` | Doors and windows in host walls | Add sill height and placement by host plus offset along the wall |
| `auto_create_rooms_from_walls`, `create_room` | Rooms | `create_room` takes department (flat ID) and phase; replaces the planned `set_room_data` |
| `batch_execute` | Many writes as one undo step, max 20 | Record the IDs each sub-command creates |
| `delete_element` | Remove a generated model | In build mode, accept only IDs a run created |
| `get_available_family_types` | Check required types before building | Porting standard only |
| `get_model_warnings_summary` | Verify after building | Porting standard only |
| `list_rooms`, `get_room_boundaries` | Post-build checks | Porting standard only |
| `create_view`, `capture_view_image` | Plans, elevation, section, 3D and the result image | `create_view` gains section, elevation and ceiling-plan types; today it makes only floor plans and 3D |
| `tag_all_rooms` | Room tags | Porting standard only |
| `create_sheet`, `place_view_on_sheet`, `place_schedule_on_sheet`, `align_viewports` | Sheets and layout | Porting standard only |
| `create_schedule` | Door, window and area schedules | Split the 994-line handler into smaller units |
| `export_pdf` | The PDF set | Write only inside the run folder |

Two tools the PRD planned to write are covered by improving existing ones: `analyze_model_statistics` gains grouping by level and an exact mode in place of `get_build_counts`, and `create_room` takes the flat ID in place of `set_room_data`. `health` already exists as a gateway tool and is rebuilt in the Python service with an add-in probe.

### New tools (missing in rvt-mcp)

Found by searching the catalog for what an apartment-block workflow needs. All six count toward the 262.

| Command | Access | Why | Step |
| --- | --- | --- | --- |
| `create_floor_opening` | W | Stair-core shaft through each floor slab; rvt-mcp has no opening tool | 1 |
| `list_levels` | R | Levels with elevations and IDs, for read-back and re-confirming after a Revit restart | 1 |
| `list_grids` | R | Grids with end points and IDs, for read-back | 1 |
| `list_views` | R | Views by type, level and sheet placement, for the drawing builder and reruns | 5 |
| `copy_elements` | W | Copy elements by an offset or to other levels; rvt-mcp can move and rotate but not copy | 8 |
| `mirror_elements` | W | Mirror elements about a line, for mirrored flats | 8 |

Backlog, not in v1: stairs, railings, roofs, curtain walls and design options. They match the v1 non-goals.

### Porting standard

Every ported tool gets these, so "improve where needed" has a fixed checklist:

- **Review before copying.** Read the handler, fix bugs found, and record each change in the file's change note.
- **Schema validation first.** Every command has an argument schema; argument errors return as data with the valid options.
- **One unit system.** Millimetres for lengths and square metres for areas in and out, converted in one place.
- **`ElementId.Value` through `RevitCompat`** on every year.
- **Document guard on every write,** and bounded responses under the 1 MiB cap with paging.
- **Correct annotations.** Read-only, write and destructive flags match what the tool really does; Appendix A flags five that look wrong.
- **File output stays in safe folders.** Tools that write files write only inside the run or export folder unless the user gives a path.
- **A command test per tool** against a scratch project, run on all four Revit years.

### Code-execution tools

`send_code_to_revit`, the five ToolBaker tools and the three code-module tools run arbitrary C# inside Revit. They are ported, but:

- They refuse to run unless developer mode is on. It is off by default and switched on per machine by the BIM manager.
- The build flow never calls them, and they can't run inside `batch_execute`.
- Every call is logged with the full code it ran.
- They bring the Roslyn compiler into the add-in.

### What not to copy

Toasts, localization, the update checker, the History window, the .NET MCP server process and the SQLite package. Failure records, change history and report history keep their tools but store data as JSON Lines in the log folder. Runtime dependencies become Newtonsoft.Json plus Roslyn for developer mode.

### Licence duties (Apache-2.0)

- Keep rvt-mcp's `LICENSE` file in your repository and installer, and add a `NOTICE` file crediting rvt-mcp by bimwright with the source commit.
- Keep existing copyright headers, and add a line to each copied file saying you changed it.
- Don't use the rvt-mcp or bimwright names for your product.
- Have a lawyer check the licence setup before commercial release.

### Revit version support (2024–2028)

| Revit | Add-in runtime | Transport | Build |
| --- | --- | --- | --- |
| 2024 | .NET Framework 4.8 | Loopback TCP | `B2M.Revit.R24` |
| 2025 | .NET 8 (Autodesk is moving it to .NET 10 through updates) | Named pipe | `B2M.Revit.R25` |
| 2026 | .NET 8 (same move to .NET 10) | Named pipe | `B2M.Revit.R26`, your development machine |
| 2027 | .NET 10 | Named pipe | `B2M.Revit.R27` |
| 2028 | Not yet announced | — | `B2M.Revit.R28`, added after release |

- **Version-specific code lives only in the add-in,** in one shared source with `#if` year flags; the Python service is the same for every year.
- **Target the lowest runtime per year.** A .NET 8 build for 2025 and 2026 still loads after Autodesk moves them to .NET 10; a .NET 10 build would not load on older updates.
- **Use `ElementId.Value` through `RevitCompat`.** It exists from 2024; `IntegerValue` is gone from 2026.
- **Save the template and families in Revit 2024,** so every later version can open them.
- **Detect the year at runtime** from the `health` command, and log it with every run.
- **Test every year** before each release; your 2026 machine runs the command tests daily. Check that your Autodesk subscription covers the earlier versions you need.

### Techniques carried over from rvt-mcp

- **One undo per batch.** `batch_execute` wraps up to 20 commands in one `TransactionGroup` and assimilates them; a failure rolls the batch back. The runner orders batches levels → grids → walls → floors → openings → doors and windows → rooms → tags, and records the IDs each batch created.
- **Warnings roll back by default.** The layout engine must produce warning-free geometry; treat any warning as an engine bug.
- **One command per ExternalEvent run,** then re-raise if work remains; skip requests that already timed out; 60-second timeout per request.
- **Document guard.** Writes run only when the active document matches the one the run confirmed; after a Revit restart the run must re-confirm.
- **Schema validation before dispatch;** error messages scrubbed of file paths; argument errors returned as data with the valid options.
- **Response cap of 1 MiB;** read back one level at a time.
- **Discovery file per Revit process** with pipe name, token and process ID; stale files deleted on connect.

## Data contracts

Three formats connect the components. The build plan is deliberately independent of the add-in's command names, so the Revit client is the only Python code that knows them; changing or adding commands touches one module.

### Building spec (written by Claude, validated by Pydantic)

```json
{
  "schema_version": "1.0",
  "site": {"plot_mm": [15000, 25000], "road_side": "front", "city": "kolkata",
           "road_width_mm": 9000},
  "building": {"ground": "stilt_parking", "upper_floors": 3,
               "floor_to_floor_mm": 3000, "template": "auto"},
  "units": [{"type": "2BHK", "per_floor": 4, "target_carpet_m2": 60}],
  "core": {"stair": "dog_leg", "stair_width_mm": 1200, "lift": false},
  "standards": {"external_wall": "Brick 230", "internal_wall": "Brick 115",
                "slab": "RCC 150", "door_types": "default", "window_types": "default"},
  "assumptions": ["floor_to_floor_mm defaulted to 3000"]
}
```

Setbacks are not in the spec: the Kolkata rule set derives them from plot size, road width and height.

### Build plan (written by the layout engine)

```json
{
  "plan_version": "1.0",
  "spec_hash": "sha256:…",
  "ops": [
    {"op": "level", "name": "Floor 1", "elevation_mm": 3000},
    {"op": "wall", "level": "Floor 1", "type": "Brick 230",
     "start": [1500, 3000], "end": [13500, 3000], "height_mm": 3000, "key": "F1-W-001"},
    {"op": "door", "host_key": "F1-W-014", "type": "Door 900x2100",
     "offset_mm": 450, "key": "F1-D-003"},
    {"op": "room", "level": "Floor 1", "point": [4200, 5100],
     "name": "Bedroom 1", "flat": "F1-A", "key": "F1-R-002"}
  ]
}
```

Every op carries a stable `key`. Hosted ops point at their host by key; the runner swaps keys for real ElementIds as batches return.

### Run result (returned to the UI and saved in the run store)

```json
{
  "run_id": "2026-11-30T10-14-02Z-7f3a",
  "status": "built",
  "batches": {"total": 9, "succeeded": 9},
  "created": {"F1-W-001": 412345, "F1-D-003": 412901},
  "warnings": [],
  "checks": {"hard_fail": 0, "soft_fail": 2, "report_pdf": "runs/…/report.pdf"},
  "drawing_set_pdf": "runs/…/set.pdf",
  "versions": {"product": "0.3.0", "addin": "0.3.0", "rvt_mcp_commit": "…", "revit": "2026", "rules": "2026-11"}
}
```

## Non-functional requirements

| Area | Requirement |
| --- | --- |
| Speed | Brief to spec under 30 s; layout under 2 s; build under 3 min for a G+4 block; full run with drawings under 5 min |
| Reliability | A failed batch never leaves partial elements from that batch; the user always sees what was built and can remove it |
| Determinism | Same spec, same add-in version, same template: identical model |
| Safety | Code-execution tools run only in developer mode, which is off by default, and the build flow never calls them; writes run only after the document check; in build mode, destructive commands are limited to IDs the product created |
| Privacy | Everything runs locally except the brief text sent to the Anthropic API; no model geometry leaves the machine |
| Security | The local web page binds to `127.0.0.1` only; the add-in accepts only local named-pipe connections (loopback TCP on Revit 2024) carrying the session token |
| Traceability | Every run stores brief, spec, plan, tool results and all versions; any run can be replayed |
| Supportability | One log folder for the service and the add-in; every add-in log line carries the Revit year and process ID |
| Platform | Windows 10/11, Revit 2024–2027 at launch and 2028 after release, Python 3.12, one Revit instance during builds |

## Delivery steps

Nine steps, each ending at a gate. A step starts only when the previous gate passes; if time runs short, scope moves, not gates.

&#91;embedded content: roadmap · eight steps, durations and gates\] (Step 8 is not in the diagram yet.)

### Step 0 · Port the bridge core, and interviews (16–29 Nov)

- Clone rvt-mcp at a fixed commit and record the commit hash in `NOTICE`.
- Create the repository with per-year add-in projects (R24–R27), following rvt-mcp's project pattern.
- Copy Transport, Execution, batch, failure handling, Security and `RevitCompat`; remove toasts, localization, the update checker and SQLite; rename namespaces; mark each copied file as changed.
- Write the porting standard as a checklist and apply it from the first file.
- Build the R26 add-in and load it in your Revit 2026.
- Port `create_level`, `create_line_based_element` (with the location-line and offset improvements) and `batch_execute`; build the `health` probe.
- A small Python script reads the discovery file and sends requests over the named pipe.
- Interview at least five Kolkata architects or builders about first-draft modeling time.
- **Gate:** from Python, one batch creates two levels and four walls in Revit 2026 and undoes in one step; `health` reports the document; five firms confirm the pain.

### Step 1 · Build commands, Revit client and runner (30 Nov–20 Dec)

- Copy the remaining build and verify commands: grids, floors, doors and windows, rooms, delete, family types, warnings, room reads. (F12.1–F12.4)
- Improve `create_point_based_element`, `create_room`, `delete_element` and `analyze_model_statistics` as listed in the add-in section.
- Write the new `create_floor_opening`, `list_levels` and `list_grids`.
- Python Revit client: discovery, connection, version and command check, op-to-command mapping, unit and type-name conversion, document confirmation. (F6.1–F6.3)
- Build runner: dependency ordering, batches of 20, key-to-ID replacement, stop on failure, read-back counts, remove generated model. (F7.1–F7.3)
- FastAPI service, run store and logging. (F10.1)
- Command tests that call every command against a scratch project in Revit 2026.
- **Gate:** a hand-written build plan for the two-storey test house builds with one undo per batch, and “remove generated model” deletes everything it created.

### Step 2 · Spec schema and layout engine (21 Dec–24 Jan)

- Pydantic spec schema v1. (F3.1)
- Buildable envelope, three templates, parametric 1/2/3BHK unit plans, walls and openings, rooms. (F4.1–F4.3)
- Geometry validation that guarantees warning-free output: joins, overlaps, closed rooms.
- 20 golden specs covering every template and unit type.
- **Gate:** all 20 golden specs build in Revit with zero warnings, and the same spec always gives the same plan.

### Step 3 · Brief agent and web page (25 Jan–7 Feb)

- Claude with `submit_spec` and `ask_user` tools; defaults table; validation retries. (F2.1–F2.3)
- Local web page with questions, assumptions, progress and results. (F1.1–F1.2)
- MCP facade tools for Claude Desktop. (F11.1)
- **Gate:** 18 of 20 test briefs give a valid spec first time; all 20 after one round of questions.

### Step 4 · Compliance checks (8–28 Feb)

- YAML rule engine; NBC 2016 rule set; Kolkata rule set transcribed from an official copy of the building rules.
- Pre-build checks on engine geometry and post-build checks on data read from Revit. (F5.1, F8.1)
- Report as JSON and PDF.
- **Gate:** on five test projects, the report misses no failure an architect finds by hand.

### Step 5 · Drawing set (1–14 Mar)

- Port rvt-mcp's view, tag, sheet, schedule and PDF tools, add section and elevation types to `create_view`, write `list_views`, then build views, room tags, door and window schedules, area statement, sheets, viewport alignment, PDF export, report merge. (F9.1–F9.2)
- **Gate:** one click produces the eight-sheet PDF in under 2 minutes.

### Step 6 · All Revit years, packaging and hardening (15–28 Mar)

- Build and test the R24, R25 and R27 add-ins; fix year differences behind `#if` flags.
- One installer: the Python service, the right add-in for each Revit year it finds, the project template and family pack saved in Revit 2024, and the `LICENSE` and `NOTICE` files.
- Error messages, retries and recovery for busy, wrong-document and timeout errors.
- **Gate:** a clean install runs a brief end to end on fresh Windows PCs with Revit 2024, 2025, 2026 and 2027.

### Step 7 · Pilot (29 Mar–25 Apr)

- Three firms run real briefs for four weeks; weekly feedback calls; fix blockers within the week.
- **Gate:** at least two of three firms say they would pay.

### Step 8 · Port the rest of the tool catalog (26 Apr–18 Jul)

- Port the remaining 233 rvt-mcp tools toolset by toolset, to the porting standard, in this order: query, modify, families, rooms, view, sheets, schedule, export, annotation, documents, lint, organization, parameters, graphics, materials, geometry, links, workflows, structural, MEP, meta, ToolBaker.
- Rebuild the 17 gateway-side tools in the Python service. (F11.3)
- Expose every tool to Claude Desktop with toolsets and a read-only mode. (F11.2)
- Put the code-execution tools behind developer mode. (F12.4)
- Write `copy_elements` and `mirror_elements`.
- Pilot fixes take priority: toolsets the pilot firms ask for move to the front of the order.
- **Gate:** all 262 tools pass their command tests on Revit 2024, 2025, 2026 and 2027, and the MCP tool list matches Appendix A.

### Step 9 · Drawing to model (F13)

A second way in, beside the brief: the office's own CAD drawing set (DWG) becomes the Revit model, drawing set and estimate, matching the source 100%, proven by automatic comparison, never assumed. The sample used to build and gate it is a G+4 RCC block (stilt + 4 floors of 4 × 3BHK, 2 lifts): 9 DWGs (ground, typical 1st–4th, roof, site plan, front elevation, sections AA/BB, foundation details, tie beam layout), the same set as PDF, and the office's door/window estimate (Excel). It is a test case for the system, not a one-off: the next similar project must go through the same pipeline unchanged.

**Rules.** No guessing. Every fact comes from the drawings, the project standards or the user. Anything unclear becomes a question. When the user says "assume", B2M first asks the project location (city/municipality), then assumes from that location's rule set and typical practice, and lists every assumption back. Families: find, adapt a copy, ask before creating. Types: duplicate before edit.

**What the sample DWGs are like** (and so what the importer must handle): millimetres, true size; each file is one sheet with its border, title and page number drawn in model space; floors are not on a shared origin; walls are plain line pairs on layer 0 (250 mm outer, 125 mm inner), columns are solid hatches, doors are anonymous dynamic blocks, windows are line patterns in wall gaps with a text mark beside them (w, w1–w4), room names and sizes are text ("3500 X 4000"); heights exist only as dimensions in the sections (2700 floor to floor, 150 slab, 450 beam, 900 parapet, PL/GL, plinth); foundation sizes and rebar are dimensions, MTEXT notes and a Reinforcement layer.

**Pipeline**

| # | Stage | Output | Proof |
| --- | --- | --- | --- |
| 1 | Ingest | DWG → DXF (ODA File Converter, found or installed with the user's OK) → normalised sheets: entities in mm with layer, expanded blocks, text, dimensions; border/title excluded | Entity counts match the DXF |
| 2 | Classify | Each sheet's role and storeys from its title ("FIRST STOREY TO FOURTH STOREY FLOOR PLAN" → storeys 1–4), scale; unknown sheets → question | Every sheet classified or asked |
| 3 | Recognise | Per plan: grids, walls (thickness classes), columns, doors (width, swing, hinge, mark), windows (width, mark), rooms (name, labelled size), stair, lifts, shafts, balconies/verandahs, parapets; per section: levels and heights; foundation: footings, plinth/tie beams, rebar specs | Each element keeps its source entity handles; unclaimed geometry is listed |
| 4 | Register | All storeys and sections on one origin and grid; section lines tied to plans | Grids and columns coincide across sheets within 1 mm |
| 5 | Questions | Everything ambiguous or missing (location/rules, sizes not measurable, heights not dimensioned, unknown blocks/layers), with the evidence | List empty, or answered/assumed and recorded |
| 6 | Review | Web page: source drawing overlaid with what B2M understood, per storey, plus the question list; nothing builds until approved | User approval stored with the run |
| 7 | Build | Building model → build plan → the existing runner (batches, undo, families find/adapt/ask, duplicate-before-edit); door/window types by mark and size | Zero Revit warnings |
| 8 | Self-check | Revit model read back and compared with the recognised building and the DWG: wall axes and thicknesses, opening positions/sizes/marks, room areas vs labels, columns, levels | Difference report; target zero |
| 9 | Drawings | The source sheet set reproduced: plans per storey type with dimensions, room tags "NAME / W X L", door/window marks, roof plan, sections at the source section lines, elevation, foundation plan, tie beam layout, office sheet format and page numbers | Sheet list and dimension values match the source |
| 10 | Estimate | Door/window estimate in the office's Excel layout with live formulas, from the model and written measurement rules | Matches the source Excel cell by cell |

**Tasks**

| Task | Scope | Depends on |
| --- | --- | --- |
| T1 | CAD ingest: ODA discovery/conversion, DXF reader, normalised sheet model, border/title detection | — |
| T2 | Sheet classification and storey mapping; project file (inputs, answers, assumptions, approvals) | T1 |
| T3 | Plan recognition: walls, columns, grids, doors, windows, rooms, stair, lifts, shafts, balconies, parapets | T1 |
| T4 | Section/foundation recognition (levels, heights, footings, tie beams, rebar notes) and cross-sheet registration | T1, T3 |
| T5 | Question and assumption engine (location-based assumptions) | T2–T4 |
| T6 | Building model → build plan (new ops as needed: footing, tie beam, balcony, parapet, shaft, lift) and project standards (wall build-ups, door/window catalogue with marks) | T3, T4 |
| T7 | Add-in: door/window types by mark and size (ensure_family_type for doors/windows), type marks at creation, room tags with size, auto-dimensions, section placement on given lines, office sheet format | — |
| T8 | Review web page (overlay + questions + approval) and API/MCP tools | T2–T5 |
| T9 | Self-check comparison and difference report | T6, T7 |
| T10 | Drawing set from imported projects | T7 |
| T11 | Estimate engine: measurement rules, model quantities, Excel with formulas | T6 |
| T12 | End-to-end gate on the sample, review, fixes, documentation | all |

- **Gate:** the sample goes DWG → approved review → Revit with zero warnings → self-check with zero unexplained differences → sheet set matching the source list and dimensions → estimate matching the source Excel; the pipeline code has no sample-specific branches.

### Step 10 · Fit-out: furniture and MEP (F14)

After a building is modelled (from a brief or from DWGs), B2M furnishes it and lays out its MEP: plumbing and drainage, electrical, HVAC and fire safety. When the office has no MEP or furniture drawings (the G+4 sample has none), the layout comes **from rules**: NBC 2016 and typical Indian residential practice for the project's location. The result is an indicative coordination model, and every output says so: an MEP engineer designs and checks the real systems. Decided with the user on 2026-11-16: all four systems, standard furniture per room, MEP system types from Revit's installed Systems template. Decided on 2026-11-17: a kitchen inside a shared open space (living/dining/kitchen with no wall between) is its own room, closed off by a room separation line, so it gets its own fittings and exhaust; the line is found in the build plan (Step 9 stage 7) where the kitchen's alcove opens (wall ends, column faces), and asked with the candidate lines when no line is clear.

**Rules.** The same as Step 9: no guessing, location first, then assumptions from that location's rule set, listed back for review. Nothing is built before approval. Families: find, adapt a copy, ask. Revit 2026 installs no local family library, so B2M looks in the open project, then in Revit's installed templates (Systems, Plumbing, Electrical, Mechanical, Multi-Discipline, Residential, Default Metric). It matches by category and keywords at planning time, copies in only the types it uses, as new types, and asks when nothing fits. It never edits a type in place. When the user answers that an item gets a plain box (`{"create": "box"}`, decided 2026-11-17 for the bed: the loaded bed set fitted no bedroom), B2M creates that box family as a run pre-step and removes it with the run. Rule values (fixture units, points per room, tank sizes, fire provisions) carry their source and are **unverified placeholders** until checked.

**Pipeline**

| # | Stage | Output | Proof |
| --- | --- | --- | --- |
| 1 | Rooms | Each built room, per storey: class (bedroom, living/dining, kitchen, toilet, balcony, lobby, stair, lift, shaft, utility…) from its name, with its polygon, bounding wall faces, doors (width, hinge, swing) and windows | Every room classed or asked |
| 2 | Families | Each item the rules need is matched to a family type in the project or an installed template (or the one the user names), before the layout; project types are measured (`measure_family_types`, or measurements given) so the layout uses their real footprint and the build inserts them from their box; anything unmatched becomes a question | Every item resolved or asked |
| 3 | Furniture | A standard set per room class, placed against walls, clear of door swings, windows and circulation, with clearances | No overlaps or blocked doors; items that do not fit are listed |
| 4 | Plumbing and drainage | Fixtures per toilet and kitchen; soil, waste, vent and rainwater stacks in the ducts; branches to the stacks; cold and hot water from the overhead tank; the underground tank and pump; tanks sized from occupancy | Every fixture connected; no disconnects |
| 5 | Electrical | Light, fan, switchboard and socket points per room (6 A / 16 A); one distribution board per flat, plus the meter room; circuits; conduit runs | Every device on a circuit |
| 6 | HVAC | Split AC indoor units on outside walls (bedrooms, living) and outdoor units outside; exhaust fans in toilets and kitchens | Each unit placed or listed |
| 7 | Fire safety | Provisions for the building's height and occupancy per NBC Part 4 (extinguishers, detectors, riser/hydrant when required) | Rule → provision list |
| 8 | Review and build | Questions, assumptions, plan preview; approval; build as its own run, so it is removable apart from the building | Zero Revit warnings |
| 9 | Self-check and schedules | Read-back against the plan; clashes with walls/doors; MEP disconnects; furniture and MEP schedules | Difference report; schedules |

**Tasks**

| Task | Scope | Depends on |
| --- | --- | --- |
| F1 | Add-in: list the family types and MEP system types in an installed template or other file; copy chosen types into the project as new types (never overwriting) | — |
| F2 | Rules and room model: room classes from names, furniture sets, fixture/point/provision rules per location profile; rooms with polygon, wall faces, doors, windows from a built run | — |
| F3 | Furniture layout solver | F2 |
| F4 | MEP layout: plumbing/drainage, electrical, HVAC, fire | F2, F3 |
| F5 | Plan ops, command mapping and add-in commands: placed families (free, wall-hosted, level-based), pipe/duct/conduit runs with fittings, connections to fixtures, electrical circuits | F1 |
| F6 | API, review page and MCP: fit-out questions, preview, approval, build, remove | F3–F5 |
| F7 | Self-check, clash and disconnect checks, schedules; live gate on the sample | all |

- **Gate:** the G+4 sample model furnished and fitted out with zero Revit warnings, every fixture connected, every device on a circuit, no furniture clashing or blocking a door, every rule value listed with its source.

## Testing and acceptance

Four test layers, from fast to slow. Only the last two need Revit.

| Layer | What it covers | Runs |
| --- | --- | --- |
| Unit tests (pytest) | Spec validation, layout engine, rule engine, unit conversion, batching and key-to-ID logic | Every commit, no Revit |
| Golden tests | 20 golden specs → build plans compared with stored plans; any change must be reviewed | Every commit, no Revit |
| Command tests | Every add-in command in the catalog (the flow's tools by the pilot, all 262 by Step 8), called from Python against a scratch project in Revit 2024, 2025, 2026 and 2027 | Daily on Revit 2026; all four years before each release |
| End-to-end tests | 20 briefs → model → checks → PDF on Revit 2026 weekly, and on all four supported versions before each release, timed | Weekly, and before the pilot |

**Release acceptance (all must pass before the pilot)**

- 18 of 20 briefs reach a built model with zero Revit warnings or errors.
- Every built model matches its plan's element counts exactly.
- Full run under 5 minutes for a G+4 block on your machine.
- The compliance report misses no failure an architect finds on five test projects.
- “Remove generated model” leaves the project exactly as it was before the run.
- A clean install completes a run without manual fixes on fresh Windows PCs with Revit 2024, 2025, 2026 and 2027.
- Code-execution tools refuse to run with developer mode off, which is the installed default, and the installer ships the `LICENSE` and `NOTICE` files.

## Risks, fallbacks and open questions

| Risk | Likelihood | Fallback or mitigation |
| --- | --- | --- |
| A copied command fails on one Revit year (hosted doors, rooms, openings) | Medium | Fix it in your copy; per-year command tests catch it before release |
| Porting takes longer than planned, because copied files depend on parts you dropped | Medium | Copy file by file and compile after each; stub dropped features; Step 0 measures the real effort before Step 1 starts |
| Fixes made in rvt-mcp no longer reach you automatically | Medium | Record the source commit; review rvt-mcp's changelog monthly and port relevant fixes by hand |
| Warning rollback blocks builds on messy geometry | High at first | Make the engine warning-free; log every rollback with the warning text as a bug |
| Layouts look generic | Medium | Build templates from real plans collected in interviews; let users edit the spec |
| Rule values from secondary sources are wrong | Medium | Check each against official NBC and Kolkata texts before the pilot; label the report advisory |
| Licence duties are missed in the shipped product | Low | Keep LICENSE and NOTICE in the repository and installer, mark changed files, and make it a release checklist item |
| Part-time capacity slips | High | Gates fixed, scope moves: cut the third template, then the elevation and section sheets |
| Porting all 256 tools doesn't fit Step 8's 12 weeks | High | Port by toolset in priority order; MEP, structural and ToolBaker go last and move to a later release if time runs out |
| Code-execution tools run on a live project | Low | Developer mode off by default, per-machine switch, every call logged with its code, never callable from the build flow or a batch |
| The 17 gateway-side tools behave differently once rewritten in Python | Medium | Keep the same inputs and outputs; test each against rvt-mcp's own responses before switching |
| Revit 2028 brings API or runtime changes | Medium | Add an R28 project once the Revit 2028 SDK is out; claim support only after the command tests pass on it |
| Autodesk moves Revit 2025 and 2026 to .NET 10 through updates | High | Keep the 2025 and 2026 builds on .NET 8, which still loads under .NET 10; rerun command tests after each Revit update |

**Open questions**

- Do pilot firms use their own wall, door and title block standards, and how soon must the product adopt them?
- Pricing: per seat, per firm, or per project? The interviews should test at least two.
- Which Revit versions do the pilot firms run? Their answer sets the testing priority across 2024–2027.
- Which plot sizes and road widths are most common in the pilot firms' projects? This decides which templates come first.

## Sources

- [rvt-mcp on GitHub](https://github.com/bimwright/rvt-mcp)
- [rvt-mcp ARCHITECTURE.md](https://github.com/bimwright/rvt-mcp/blob/master/ARCHITECTURE.md)
- [RvtMcp.Server on NuGet](https://nuget.org/packages/rvtmcp.server)
- [Revit API Docs: API changes in 2025](https://www.revitapidocs.com/2025/news)
- [InfraLens: NBC 2016 Part 3 explained](https://infralens.in/knowledge/nbc-2016-part-3-development-control)

## Appendix A · Revit tool catalog (256 ported tools)

Every tool in rvt-mcp at commit `b44ee0b`, grouped by toolset. The add-in command is the name shown; the MCP tool for Claude Desktop adds the `revit_` prefix. **Access:** R reads only, W writes to the model, D overwrites or deletes (model data or files), following rvt-mcp's own annotations. **Flow** marks the 23 ported tools the brief-to-model run uses. Every tool also gets the porting standard in the add-in section; notes list only the extra work known today.

### create (7)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `create_grid` | W | Create a grid line | ✓ |  |
| `create_group_from_elements` | W | Create a Revit group from two or more element ids |  |  |
| `create_level` | W | Create a level at specified elevation | ✓ |  |
| `create_line_based_element` | W | Create a line-based element (wall) | ✓ | Add location line, base and top offsets and a top level, so the engine's centreline walls land exactly. |
| `create_point_based_element` | W | Create a OneLevelBased or OneLevelBasedHosted family (door, window, furniture) | ✓ | Add sill height and placement by host plus offset along the wall, so the client stops computing points. Step 10 (F5): `offset` above the level, `rotation`, `wallSide`/`towardX,towardY` for wall-hosted and face-based families, face-based families on wall, ceiling and floor faces, `faceDown` (no host: on a downward reference plane at the height); returns `placement_mode`, `side`, `created_ids`. A level-based family that takes the point's z as its offset above the level is put back at the height asked for (with a warning). |
| `create_room` | W | Create and place a room | ✓ | Accept department (flat ID) and phase in the same call; replaces the planned `set_room_data`. |
| `create_surface_based_element` | W | Create a surface-based element (floor, ceiling) | ✓ |  |

### delete (1)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `delete_element` | D | Delete elements by ID; restore them with Revit Undo in Revit (no MCP undo tool) | ✓ | In build mode, accept only IDs a run recorded as created. |

### rooms (10)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `auto_create_rooms_from_walls` | W | Automatically generate room elements in all enclosed boundary circuits found on the target level | ✓ |  |
| `compute_room_finishes` | W | Calculate room finishes and boundary materials |  | Marked as a write but reads like a read; confirm and fix the annotation. |
| `create_area` | W | Create an area element at specified coordinates (mm) in an area plan |  |  |
| `create_room_separator` | W | Create room separator lines from an array of {x,y,z} points (mm) |  |  |
| `create_space` | W | Create an MEP space element at specified coordinates (mm) on a target level |  |  |
| `get_room_boundaries` | R | Report room boundary segments (points in mm) and their bounding elements | ✓ |  |
| `get_room_openings` | R | Retrieve doors and windows belonging to a room boundary |  |  |
| `list_areas` | R | List area elements in the model with optional scheme/level filters |  |  |
| `list_rooms` | R | List all rooms in the model with status classification (placed, unplaced, not_enclosed) and optional level/phase filters | ✓ |  |
| `tag_all_areas` | W | Place area tags for a bounded batch of untagged areas |  |  |

### view (11)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `activate_view` | R | Set the active view in Revit UI. UI-only operation |  |  |
| `analyze_sheet_layout` | R | Analyze title block and a bounded viewport page in mm |  |  |
| `capture_view_image` | D | Export the active or specified view as png/jpeg (default 1600 pixels), optionally to an absolute output_path inside %TEMP% or %LOCALAPPDATA%… | ✓ |  |
| `create_camera_view` | D | Create a perspective 3D camera view, or replace the orientation of an existing perspective view with the same name; restore it with Revit Un… |  |  |
| `create_view` | W | Create a view (floorplan or 3d) | ✓ | Add section, elevation and ceiling-plan types; today it makes only floor plans and 3D views. |
| `place_view_on_sheet` | W | Place a view on a sheet | ✓ |  |
| `set_view_crop` | D | Replace saved view crop settings; restore them with Revit Undo |  |  |
| `set_view_orientation` | D | Replace a 3D view's camera orientation; restore it with Revit Undo |  |  |
| `set_view_scale` | D | Replace a saved view scale; restore it with Revit Undo |  |  |
| `set_view_style` | D | Replace a view's display style and detail level; restore them with Revit Undo |  |  |
| `show_element_in_view` | R | Activate view, select elements, zoom to elements |  |  |

### sheets (14)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `align_viewports` | D | Replace viewport positions on a sheet; restore them with Revit Undo | ✓ |  |
| `assign_revision_to_sheet` | D | Replace sheet revision assignments; restore them with Revit Undo |  |  |
| `create_placeholder_sheet` | W | Create a new placeholder sheet |  |  |
| `create_revision` | W | Create a new document revision |  |  |
| `create_sheet` | W | Create a new sheet with a titleblock | ✓ |  |
| `duplicate_sheet` | W | Duplicate an existing sheet with viewport and schedule layout |  |  |
| `get_titleblock_parameters` | R | Get titleblock instance and type parameters for a sheet |  |  |
| `get_viewport_geometry` | R | Read viewport positions on a sheet in sheet-space millimetres: visible crop rectangle, view outline, scale, rotation, pinned and crop flags |  |  |
| `list_revisions` | R | List project revisions and optionally their assigned sheets |  |  |
| `list_sheets` | R | List sheets matching filters, with viewport and schedule counts, title blocks, and revisions |  |  |
| `list_titleblocks` | R | List loaded titleblock family types and count their sheet placements |  |  |
| `place_schedule_on_sheet` | W | Place a schedule on a sheet using sheet paper coordinates in millimeters | ✓ |  |
| `renumber_sheets` | D | Replace sheet numbers or names; restore them with Revit Undo |  |  |
| `set_titleblock_parameters` | D | Replace titleblock parameter values; restore them with Revit Undo |  |  |

### schedule (10)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `add_schedule_field` | W | Add one new field to an existing schedule |  |  |
| `apply_schedule_filter_sort` | D | Replace schedule filters and settings; restore them with Revit Undo |  |  |
| `create_schedule` | W | Create a new schedule from a declarative spec | ✓ | 994-line handler; split it into smaller units while porting. |
| `find_schedule_elements` | R | Find Revit elements aggregated by a schedule (using FilteredElementCollector scoped to the schedule's id) |  |  |
| `get_schedulable_fields` | R | List parameters that CAN be added as fields to a schedule but have not been added yet |  |  |
| `get_schedule_data` | R | Get the rendered tabular content of a schedule (header row + body rows) with pagination |  |  |
| `get_schedule_definition` | R | Get the full structural definition of a schedule: fields (parameter/formula/combined), filters, sort/group, and settings |  |  |
| `get_schedule_formulas` | R | Extract all calculated (formula) and combined-parameter fields from a schedule, with parsed formula dependencies |  |  |
| `list_schedules` | R | List all schedules in the project |  |  |
| `update_schedule_field` | D | Modify existing schedule fields; restore them with Revit Undo |  |  |

### export (16)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `batch_export_sheets` | D | Export many sheets at once to PDF or DWG. outputFolder must be an existing absolute path |  |  |
| `create_view_sheet_set` | W | Create a named ViewSheetSet (a saved set of views/sheets) for batch printing/exporting |  |  |
| `export_dgn` | D | Export sheets or views to MicroStation DGN. outputFolder must be an existing absolute path |  |  |
| `export_dwf` | D | Export sheets or views to Autodesk DWF/DWFx |  |  |
| `export_dwg` | D | Export sheets or views to AutoCAD DWG. outputFolder must be an existing absolute path |  |  |
| `export_elements_data` | D | Export element parameter data for a category to a JSON or CSV file |  |  |
| `export_fbx` | D | Export a 3D view to Autodesk FBX. outputFolder must be an existing absolute path |  |  |
| `export_gbxml` | D | Export the model's energy analytical data to gbXML. outputFolder must be an existing absolute path |  |  |
| `export_ifc` | D | Export the model to IFC. outputFolder must be an existing absolute path |  |  |
| `export_image` | D | Export a view to a raster image |  |  |
| `export_nwc` | D | Export the model to Navisworks NWC. outputFolder must be an existing absolute path |  |  |
| `export_pdf` | D | Export sheets or views to PDF. outputFolder must be an existing absolute path | ✓ | Write only inside the run folder; never overwrite files outside it. |
| `export_room_data` | W | Export all rooms |  | Marked as a write but reads like a read; confirm and fix the annotation. |
| `export_schedule_csv` | D | Export a Revit schedule's data to a delimited text/CSV file |  |  |
| `get_print_settings` | R | Report PrintManager state and bounded setting lists |  |  |
| `list_export_settings` | R | List bounded export settings |  |  |

### annotation (14)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `apply_keynote_to_element` | D | Replace keynote values; restore them with Revit Undo |  |  |
| `create_callout_view` | W | Create a callout view in a parent view |  |  |
| `create_detail_line` | W | Create a detail line element in a view |  |  |
| `create_dimensions` | W | Create a dimension element in a view |  |  |
| `create_filled_region` | W | Create a filled region element in a view |  |  |
| `create_text_note` | W | Create a text note element in a view |  |  |
| `find_undimensioned_elements` | R | Find visible elements of a specific category in a view that do not have dimensions |  |  |
| `find_untagged_elements` | R | Find visible elements of a specific category in a view that do not have tags |  |  |
| `list_keynotes` | R | List all keynotes loaded in the model with prefix and search filters |  |  |
| `tag_all_by_category` | W | Tag all elements of a specific category in a view |  |  |
| `tag_all_rooms` | W | Tag all rooms in current view at location point | ✓ |  |
| `tag_all_walls` | W | Tag all walls in current view at midpoint |  |  |
| `tag_elements` | W | Tag one or more elements in a view |  |  |
| `wipe_empty_tags` | D | Delete empty tags; restore them with Revit Undo |  |  |

### query (17)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `ai_element_filter` | R | Filter elements by category + parameter |  |  |
| `analyze_model_statistics` | R | Count elements grouped by category (Walls, Doors, Pipes, etc.) | ✓ | Add grouping by level and an exact, uncapped mode for read-back; replaces the planned `get_build_counts`. |
| `get_assembly_members` | R | Read an assembly instance and a page of members |  |  |
| `get_available_family_types` | R | List loadable family types | ✓ |  |
| `get_current_view_info` | R | Read the active view's viewName, viewType (FloorPlan/Section/3D/Sheet), level, scale, detailLevel and displayStyle |  |  |
| `get_element_details` | R | Read detailed metadata for one or more elements |  |  |
| `get_element_parameters` | R | Read instance parameters for one or more elements |  |  |
| `get_element_relationships` | R | Read host, group, assembly, owner view, design option, family nesting, and dependent-element relationships for elements |  |  |
| `get_group_members` | R | Read a group instance and a page of member elements |  |  |
| `get_material_quantities` | R | Sum material quantities (area m², volume m³) by category |  |  |
| `get_selected_elements` | R | Get currently selected Revit elements with paging |  |  |
| `get_type_parameters` | R | Read type parameters from explicit type ids or from the types of provided element ids |  |  |
| `list_assemblies` | R | List a page of assembly instances |  |  |
| `list_groups` | R | List model/detail/attached groups with type, owner view, parent, and optional member ids |  |  |
| `list_project_parameters` | R | List project/shared parameter bindings, including instance/type binding kind and bound categories |  |  |
| `list_worksets` | R | List document worksets and active workset |  |  |
| `survey_change_impact` | R | Survey ten relationship groups for explicit active-document targets before proposing a change |  |  |

### modify (9)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `assign_elements_to_workset` | D | Replace element workset assignments; restore them with Revit Undo |  |  |
| `change_element_type` | D | Replace element types; restore them with Revit Undo |  |  |
| `color_elements` | D | Overwrite element graphics; restore them with Revit Undo |  |  |
| `move_elements` | D | Translate existing elements by dx_mm/dy_mm/dz_mm along the model X/Y/Z axes (project north, not true north); every element moves by the same… |  |  |
| `operate_element` | D | Change element visibility or graphics; restore document changes with Revit Undo |  |  |
| `rotate_elements` | D | Rotate existing elements about a vertical axis; restore with Revit Undo |  |  |
| `set_element_parameter_values` | D | Replace instance parameter values; restore them with Revit Undo |  | Fallback for flat data on rooms that already exist. |
| `set_type_parameter_values` | D | Replace type parameter values; restore them with Revit Undo |  |  |
| `set_wall_top` | D | Replace walls' top constraint; restore it with Revit Undo |  |  |

### families (10)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `audit_families` | R | Read-only family audit with bounded sections |  |  |
| `duplicate_family_type` | W | Duplicate a FamilySymbol or system type within its family under newTypeName, optionally setting type parameter overrides (JSON object as str… |  |  |
| `export_family_to_path` | D | Save a loadable family from the current project back to an .rfa file at outputPath |  |  |
| `get_family_instances` | R | List placed instances of a Family (or a specific type within it) with location/host/level DTOs in mm |  |  |
| `list_family_types_in_family` | R | List a bounded page of types in one family |  |  |
| `list_template_files` | R | Revit's installed templates for the running year (.rte), per language folder |  | New in B2M, Step 10 (F1): Revit 2026 installs no family library; fit-out types come from these. |
| `list_types_in_file` | R | Family types and MEP types (pipe, duct, conduit, cable tray, piping/duct/electrical distribution system types) in an .rte/.rvt, opened in the background and closed unsaved |  | New in B2M, Step 10 (F1). Cached per file and year; not in `batch_execute`. |
| `copy_types_from_file` | W | Copy chosen types from an .rte/.rvt into the project as new types (CopyElements, dependents use the project's types) |  | New in B2M, Step 10 (F1). Never overwrites or edits a type: per item copied / already_there / not_found; `created_ids`; not in `batch_execute`. |
| `measure_family_types` | R | Each family type's box about its insertion point: one unrotated instance per type at a level's origin (level-based at the level, work-plane-based on its plane), measured, then rolled back |  | New in B2M, Step 10 (families first, 2026-11-17): the fit-out lays out with the chosen family's real footprint and inserts it from its box. `base_offset_mm` makes explicit a family that takes the point's z as its offset above the level; hosted types are skipped with the reason. The model is never changed. |
| `create_box_family` | W | Create a plain box family (W x D x H, type parameters Width, Depth, Height, Material; origin at the middle of its back edge, front facing −Y, base at the level) from the category's metric template (else Metric Generic Model with the category set) and load it as a new family |  | New in B2M, Step 10 (2026-11-17): only when the user answers `{"create": "box"}` for a fit-out item no family suits. A family of that name is never overwritten (`already_there`); `created_ids` (family, type, anything the load brought) are removed with the run; not in `batch_execute`. |
| `list_loaded_families` | R | List all loaded families (loadable + in-place + system) in the active document grouped by category |  |  |
| `load_family_from_path` | D | Load or replace family definitions; restore model changes with Revit Undo |  |  |
| `rename_family_type` | D | Rename an existing type; restore its name with Revit Undo |  |  |
| `replace_family_type` | D | Replace instance types; restore them with Revit Undo |  |  |
| `unload_family` | D | Delete a family and optionally its instances; restore them with Revit Undo |  |  |

### geometry (13)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `analyze_geometry_complexity` | R | Infer geometry complexity based on solid/face count and complexity metrics |  |  |
| `clash_detection` | R | Detect clashes between elements of category A and category B. Scope with viewId and keep maxPairs low |  |  |
| `compute_element_area` | R | Compute the total face area of one or more elements in square meters (m2) |  |  |
| `compute_element_volume` | R | Compute the geometric solid volume of one or more elements in cubic meters (m3) |  |  |
| `create_mesh_shapes` | D | Create DirectShape elements with materials from a JSON mesh file; replace_existing deletes earlier shapes and update_materials overwrites ma… |  |  |
| `find_elements_in_volume` | R | Find elements inside or intersecting an axis-aligned 3D volume or a room's bounding box |  |  |
| `find_overlapping_elements` | R | Find potentially overlapping elements of the same category, using bounding box intersection analysis |  |  |
| `get_element_bounding_box` | R | Get the bounding box of one or more elements, optionally relative to a view, and optionally including transform data |  |  |
| `get_element_centroid` | R | Compute the geometric centroid of one or more elements |  |  |
| `get_element_geometry` | R | Get geometric details and optional vertex samples for one or more elements |  |  |
| `measure_distance_between_elements` | R | Measure shortest distance between two elements based on bounding boxes, element locations, or bounding box pre-filter |  |  |
| `project_point_onto_face` | R | Project a 3D point onto a specific face of a Revit element |  |  |
| `raycast_from_point` | R | Cast a ray from a 3D origin point in a given direction to find the nearest element hit within a 3D view |  |  |

### graphics (12)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `apply_filter_to_view` | D | Change view filter visibility; restore it with Revit Undo |  |  |
| `clear_element_overrides` | D | Clear saved element graphics; restore them with Revit Undo |  |  |
| `create_view_filter` | W | Create a parameter-based view filter (ParameterFilterElement) targeting one or more categories |  |  |
| `get_view_visibility` | R | Report a view's visibility/graphics state: hidden categories, applied filters, detail level, discipline, scale, view template, graphics-over… |  |  |
| `list_phases` | R | List all project phases (in sequence order) and all phase filters |  |  |
| `list_view_filters` | R | List all view filter definitions (ParameterFilterElement) in the document |  |  |
| `override_element_graphics` | D | Replace element graphics; restore them with Revit Undo |  |  |
| `remove_filter_from_view` | D | Remove a view filter and optionally delete its definition; restore it with Revit Undo |  |  |
| `set_category_visibility` | D | Change saved category visibility; restore it with Revit Undo |  |  |
| `set_element_phase` | D | Replace element phase assignments; restore them with Revit Undo |  |  |
| `set_filter_overrides` | D | Replace view filter graphics; restore them with Revit Undo |  |  |
| `set_view_phase` | D | Replace saved view phase settings; restore them with Revit Undo |  |  |

### materials (10)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `assign_material_to_element` | D | Replace element or type material assignments; restore them with Revit Undo |  |  |
| `create_material` | W | Create a new material with optional graphics parameters |  |  |
| `duplicate_material` | W | Duplicate an existing material with a new name |  |  |
| `get_material_properties` | R | Get detailed properties of a material by ID or name |  |  |
| `get_material_takeoff` | W | Calculate material takeoff |  | Marked as a write but reads like a read; confirm and fix the annotation. |
| `list_materials` | R | List and filter materials in the active document |  |  |
| `set_material_appearance` | D | Replace material graphics; restore them with Revit Undo |  |  |
| `set_material_identity` | D | Replace material identity values; restore them with Revit Undo |  |  |
| `set_material_structural_asset` | D | Replace material structural properties; restore them with Revit Undo |  |  |
| `set_material_thermal_asset` | D | Replace material thermal properties; restore them with Revit Undo |  |  |

### parameters (8)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `bind_shared_parameter` | D | Add or replace parameter bindings; restore document changes with Revit Undo |  |  |
| `create_project_parameter` | W | Create a pure project parameter |  |  |
| `create_shared_parameter` | W | Create a shared parameter definition in the shared parameter file |  |  |
| `export_shared_parameter_file` | W | Export a shared parameter file as structured DTO data |  | Marked as a write but reads like a read; confirm and fix the annotation. |
| `list_project_parameter_bindings` | R | List all project parameter bindings in the document, including name, GUID (if shared), categories, and binding type |  |  |
| `list_shared_parameters` | R | List all shared parameters defined in the shared parameter file |  |  |
| `remove_parameter_binding` | D | Remove parameter bindings or categories; restore them with Revit Undo |  |  |
| `set_parameter_value_by_guid` | D | Replace parameter values by GUID; restore them with Revit Undo |  |  |

### organization (10)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `apply_view_template` | D | Replace view settings or template assignments; restore them with Revit Undo |  |  |
| `create_view_template_from_view` | W | Create a new view template from an existing view |  |  |
| `delete_saved_selection` | D | Delete a saved selection filter; restore it with Revit Undo |  |  |
| `delete_view_template` | D | Delete a view template and optionally detach dependent views; restore them with Revit Undo |  |  |
| `duplicate_view_template` | W | Duplicate an existing view template |  |  |
| `list_saved_selections` | R | List all saved selection filters in the document |  |  |
| `list_view_templates` | R | List view templates in the document |  |  |
| `load_selection` | R | Load a bounded page from a saved selection filter |  |  |
| `save_selection` | D | Create or replace a saved selection; restore document changes with Revit Undo |  |  |
| `select_elements` | R | Update active selection in the Revit UI. Runs pure UI selection mutation (must NOT run in transaction) |  |  |

### links (12)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `acquire_coordinates_from_link` | D | Replace host shared coordinates; restore them with Revit Undo |  |  |
| `get_link_coordinate_system` | R | Inspect a Revit or CAD link's instance and total transforms and map its origins into host internal/shared coordinates |  |  |
| `get_link_elements` | R | Retrieve elements from a Revit link instance document |  |  |
| `get_project_coordinate_system` | R | Read the complete project coordinate setup: immutable Internal Origin, Project Base Point, Survey Point, active and named Project Locations,… |  |  |
| `import_cad_to_view` | W | Import or link a CAD drawing (.dwg, .dxf) into a specific view |  |  |
| `link_revit_model` | W | Link a Revit model (.rvt) into the project |  |  |
| `list_linked_cad` | R | List imported or linked CAD files in the model |  |  |
| `list_linked_models` | R | List external Revit linked models and instances |  |  |
| `publish_coordinates_to_link` | D | Publish host shared coordinates to a loaded Revit link |  |  |
| `reload_link` | D | Reload a Revit link type by type or instance ID. timeout_seconds: plugin wait 1-900s, default 600 - reloading a large link can exceed 60s |  |  |
| `set_project_base_point` | D | Replace base point or survey point values; restore them with Revit Undo |  |  |
| `unload_link` | D | Unload a Revit link type by type or instance ID. Changes link loading outside a document transaction |  |  |

### documents (3)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `manage_checkpoints` | D | List checkpoints to inspect them, prune to free space, or restore to inspect an earlier state of the confirmed active local model or family |  |  |
| `save_checkpoint` | D | With user approval, create a checkpoint of a local model or family before risky work or at handover |  |  |
| `save_document` | D | Save the confirmed active document after verified work, following revit_get_document_state policy |  |  |

### lint (4)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `analyze_view_naming_patterns` | R | Infer naming patterns with bounded detail |  |  |
| `detect_firm_profile` | R | Fingerprint project naming (views + sheets + levels), match against firm-profile library |  |  |
| `get_model_warnings_summary` | R | Group warnings by description with bounded types/examples | ✓ |  |
| `suggest_view_name_corrections` | R | Propose corrected view names for outliers |  |  |

### workflows (8)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `workflow_clash_review` | D | Create clash-review views and overwrite graphics; restore model changes with Revit Undo |  |  |
| `workflow_data_roundtrip` | D | Export/import category parameter data with validation |  |  |
| `workflow_model_audit` | R | Run a read-only composite model audit covering warnings, families, views, schedules, and MEP connectivity signals |  |  |
| `workflow_naming_normalization` | D | Rename existing model elements; restore their names with Revit Undo |  |  |
| `workflow_room_documentation` | W | Generate room documentation records, optional callouts, room tags, finish schedule, and sheet placement |  |  |
| `workflow_sheet_set` | D | Create sheets and replace sheet data; restore model changes with Revit Undo |  |  |
| `workflow_takeoff_report` | D | Generate a takeoff report and optionally overwrite a JSON/CSV file via output_path; Revit Undo cannot restore files, so back up the destinat… |  |  |
| `workflow_view_cleanup` | D | Delete eligible views or schedules; restore them with Revit Undo |  |  |

### structural (12)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `analyze_structural_connections` | R | Audit structural joins between columns and beams |  |  |
| `create_foundation_isolated` | W | Create an isolated/spread footing at a point or under an existing column |  |  |
| `create_foundation_wall` | W | Create a wall foundation under an existing wall |  |  |
| `create_rebar_set` | W | Create a straight rebar set (single bar or arrayed) inside a structural host |  |  |
| `create_rebar_stirrup` | W | Create a shape-driven rebar (typically a closed stirrup) inside a concrete host |  |  |
| `create_structural_beam` | W | Create a structural beam between two points |  |  |
| `create_structural_column` | W | Create a structural column at a point |  |  |
| `create_structural_wall` | W | Create a structural wall between two points |  |  |
| `get_structural_loads` | R | List structural loads (point, line, area) |  |  |
| `list_rebar` | R | List rebar instances |  |  |
| `set_structural_load` | D | Replace structural load values; restore them with Revit Undo |  |  |
| `tag_structural_framing` | W | Place structural framing tags on beams in the active or specified view |  |  |

### mep (16)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `analyze_mep_network` | R | Analyze one MEP system |  |  |
| `connect_mep_elements` | D | Change MEP connectivity; restore connections with Revit Undo |  |  |
| `create_air_terminal` | W | Place an air terminal (diffuser/grille) family instance at a point (mm) |  | Step 10 (F5): same placement options as `create_point_based_element` in snake_case (`offset`, `rotation`, `host_offset`, `wall_side`, `toward_x/y`, `face_down`); `created_ids`. |
| `create_cable_tray` | W | Create an electrical cable tray between two points (mm) |  |  |
| `create_conduit` | W | Create an electrical conduit between two points (mm) |  |  |
| `create_duct` | W | Create an HVAC duct between two points (mm) |  |  |
| `create_lighting_fixture` | W | Place a lighting fixture family instance at a point (mm) |  | Step 10 (F5): face-based on a ceiling's bottom face or a wall side; without a ceiling at `offset` with `face_down`; `rotation`; `created_ids`. |
| `create_mep_fitting` | W | Insert an MEP fitting at connectors of existing MEP elements |  |  |
| `create_mep_run` | W | Pipe, duct, conduit or cable tray run along 3D points with elbows at bends; ends joined to a fixture's free connector of the run's domain and system, a free curve end, or a pipe/duct main (tee) |  | New in B2M, Step 10 (F5). One transaction; refused joins listed in `failed_connections` (or `require_connections` undoes all); returns `segment_ids`, `fitting_ids`, `created_ids`, `modified_ids`. |
| `create_electrical_circuit` | W | Put devices and fixtures on one circuit and assign a panel |  | New in B2M, Step 10 (F5). Elements without a free electrical connector of the circuit type are named and nothing is created. |
| `create_pipe` | W | Create a pipe (mm) |  |  |
| `detect_system_elements` | R | Walk an MEP system and page IDs per category with startElement + maxElements (hard max 5000) |  |  |
| `find_mep_disconnects` | R | Find MEP elements with open/unconnected End connectors (potential gaps in ductwork, piping, conduit) |  |  |
| `get_mep_element_connectors` | R | Inspect all connectors on an MEP element (duct/pipe/fitting/equipment/terminal): domain, shape, position (mm), connection status, flow, dire… |  |  |
| `get_panel_schedule` | R | Read panel metadata and a bounded circuit page |  |  |
| `get_system_inventory` | R | Return the full element inventory of one MEP system: network members, terminals, and base equipment, deduplicated by element ID, with catego… |  |  |
| `list_mep_systems` | R | List all MEP systems (mechanical/HVAC, piping/plumbing, electrical) |  |  |
| `set_system_classification` | D | Change MEP system membership; restore it with Revit Undo |  |  |

### meta (24)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `analyze_usage_patterns` | R | Analyze MCP tool usage |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. |
| `batch_execute` | D | Run multiple MCP commands atomically inside one Revit TransactionGroup (single undo on success) | ✓ | Record the IDs each sub-command creates, for key-to-ID swap and removal. |
| `delete_code_module` | D | Delete an unused module head from local storage without Revit |  | Developer mode only, off by default; never called by the build flow. Runs in the gateway; rebuild in the Python service. |
| `get_change_records` | R | Query local per-model MCP change history by server-issued modelKey, optional elementId or uniqueId, ISO UTC date range and limit (1-100) |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. Move its store from SQLite to JSON Lines in the log folder. |
| `get_current_target` | R | Read the next command's Revit binding, connected pid, generation and confirmation state |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. |
| `get_document_state` | R | Inspect the active or all open documents, including file version, backup and checkpoint state and the save policy |  |  |
| `get_failure_records` | R | Read the local failure ledger without dispatching Revit; works while native UI is modal and with history or send-code disabled |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. Move its store from SQLite to JSON Lines in the log folder. |
| `get_operation_status` | R | Read an issued operationId's local execution/outcome receipts while Revit is modal; finalResult.data is available while retained, and waitSe… |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. |
| `get_report_history` | R | Read exact SQLite History for a saved Agent Report: capture call IDs, record IDs, reasons and before/after |  | Move its store from SQLite to JSON Lines in the log folder. |
| `health` | R | Read gateway/plugin identity and last observed host state after selecting a target; defaults offline, with check_updates=true opting into a … |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. Add an add-in probe returning Revit year, process ID and active document. |
| `list_available_targets` | R | List discovered live Revit instances with pid, window_title, target identity and connection state |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. Build it on the add-in's discovery files. |
| `list_code_modules` | R | List stored C# helper modules without Revit; use before reusing, replacing or deleting a module |  | Developer mode only, off by default; never called by the build flow. Runs in the gateway; rebuild in the Python service. |
| `list_recent_models` | R | List up to 50 recent model paths from this Revit's Revit.ini, most recent first, even without an open document |  |  |
| `lookup_message` | R | Explain a Revit dialog or failure code from the catalogue of the bound Revit version |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. Port its message catalogue with it. |
| `open_model` | W | Open a confirmed absolute path on the Revit machine, including when no document is open; older files may upgrade in memory, reaching disk on… |  |  |
| `purge_unused` | D | Delete unused family symbols; restore them with Revit Undo |  |  |
| `record_change` | W | Attach the user's reason to explicit server-issued change callIds in one model's local history |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. Move its store from SQLite to JSON Lines in the log folder. |
| `report_task_result` | R | Report the finished task to the Revit user in the Reports window |  | Needs a minimal Reports window in the add-in. |
| `resolve_history_identity` | D | Record the owner's explicit choice for the active saved model's local history after Save As, a copy or an uncertain path change |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. Move its store from SQLite to JSON Lines in the log folder. |
| `save_code_module` | D | Save a reusable C# declarations-only helper after compile-only validation on the pinned capable Revit host |  | Developer mode only, off by default; never called by the build flow. Runs in the gateway; rebuild in the Python service. |
| `send_code_to_revit` | W | Full-trust C#; confirm edits, prefer typed tools |  | Developer mode only, off by default; never called by the build flow. Brings the Roslyn compiler. |
| `set_project_info` | D | Replace project information fields; restore them with Revit Undo |  |  |
| `show_message` | R | Show a Revit TaskDialog |  |  |
| `switch_target` | W | Pin an instance listed by revit_list_available_targets using pid:<n>, id:revit-<year>-<pid>, auto or a calendar year (never R-codes); verify… |  | Runs in rvt-mcp's .NET gateway; rebuild in the Python service. |

### toolbaker (5)

| Command | Access | What it does | Flow | Port notes |
| --- | --- | --- | --- | --- |
| `accept_bake_suggestion` | D | Accept an adaptive ToolBaker suggestion by id |  | Developer mode only, off by default; never called by the build flow. Runs in the gateway. |
| `dismiss_bake_suggestion` | D | Dismiss an adaptive ToolBaker suggestion |  | Developer mode only, off by default; never called by the build flow. Runs in the gateway. |
| `list_bake_suggestions` | W | List bounded adaptive ToolBaker suggestions |  | Developer mode only, off by default; never called by the build flow. Runs in the gateway. Marked as a write but reads like a read; confirm and fix the annotation. |
| `list_baked_tools` | R | List baked tools with nameFilter and bounded paging |  | Developer mode only, off by default; never called by the build flow. |
| `run_baked_tool` | D | Run a baked tool by name |  | Developer mode only, off by default; never called by the build flow. |
