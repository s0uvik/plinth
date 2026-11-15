# B2M Brief-to-Model

Turns a written brief for a G+3/G+4 apartment block into a native Revit model. See
[`PRD Revit Brief-to-Model.md`](PRD%20Revit%20Brief-to-Model.md) for the product plan.

## Layout

| Path | What |
| --- | --- |
| `addin/src/Core` | Add-in code with no Revit dependency: transports, token, discovery, request lifecycle, schema checks, batch logic |
| `addin/src/Revit` | Revit-side bridge (`Bridge/`) and commands (`Commands/<toolset>/`) |
| `addin/B2M.Revit.R24` … `R27` | One project per Revit year, all compiling the shared source |
| `addin/tests` | Core tests, run on .NET 8 and .NET Framework 4.8 |
| `service/b2m/revit` | Python client: discovery, pipe/TCP connection, `RevitClient` |
| `service/scripts` | Gate scripts run against a live Revit |

Much of the add-in is derived from [rvt-mcp](https://github.com/bimwright/rvt-mcp) (Apache-2.0); see `NOTICE`.

## Add-in

Needs the .NET 10 SDK (builds every year, including .NET Framework 4.8 and .NET 8 targets).

```sh
cd addin
dotnet build B2M.Revit.sln -c Release          # all four years + tests
dotnet test tests/B2M.Revit.Core.Tests          # core tests on .NET 8 and 4.8
dotnet build B2M.Revit.R26 -c Release -p:Deploy=true   # install into Revit 2026 for this user
```

Deploy copies `B2M.Revit.addin` and `B2M\` into `%APPDATA%\Autodesk\Revit\Addins\<year>\`. Restart Revit to
load it. To remove it, delete those two entries.

At startup the add-in writes `%LOCALAPPDATA%\B2M\discovery\revit-<year>-<pid>.json` (pipe name or port,
session token) and logs to `%LOCALAPPDATA%\B2M\logs\addin-<date>.log`.

### Protocol

One JSON object per line each way over the named pipe (Revit 2025+) or loopback TCP (Revit 2024):

```json
{"id":"1","token":"<from discovery>","command":"create_level","params":{"elevation":3000},"expected_document_id":"<confirmed>"}
{"id":"1","success":true,"data":{...},"_target":{"document_id":"...","document_title":"..."}}
```

- `health` answers off Revit's queue: add-in version, Revit year, process ID, protocol version, commands.
- Writes need `expected_document_id`: read it from `_target.document_id` of any read command
  (`get_current_view_info`) after checking the document title.
- Errors are `"CODE: message"`, often with `data` listing valid options.
- Any Revit warning or error during a command rolls its transaction back and is listed in `failures`.
- `batch_execute` runs up to 20 commands as one undo step; any failure rolls all of them back.

## Packaging and install (Step 6)

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1        # dist\B2M-<version>\ and .zip
# on a user's PC, from the unzipped package, with Revit closed:
powershell -ExecutionPolicy Bypass -File install.ps1                # add-in for each installed Revit 2024-2027 + service
powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-Purge]
```

The install is per user (no admin). It needs Python 3.12 for the service and puts it in `%LOCALAPPDATA%\B2M`
with `start-b2m.cmd`. `standards.example.json` shows how to map the logical types to a firm's own. The B2M
project template and family pack (PRD) still need to be made in Revit 2024 and added to the package.

## Service

```sh
cd service
python -m venv .venv
.venv\Scripts\python -m pip install -e ".[dev]"
.venv\Scripts\python -m pytest
```

### Step 0 gate

With Revit 2026 open on a **scratch** project and the add-in deployed:

```sh
.venv\Scripts\python scripts\step0_smoke.py --expect-title "<scratch project title>"
```

Then press Ctrl+Z once in Revit: the two levels and four walls it created should disappear together.

### Brief agent, web page and Claude Desktop (Step 3)

Claude (Claude Sonnet 5.5, as the PRD names) reads a brief and answers only by asking for missing essentials
or submitting a spec; defaults it applies are listed for review. It needs `ANTHROPIC_API_KEY` (or an
`ant auth login` profile) in the service's environment.

- Web page: start the service and open http://127.0.0.1:8765 — brief, questions, assumptions and layout
  preview, editable spec, Revit document title, build with per-batch progress, remove.
- Claude Desktop: add to `claude_desktop_config.json` (the service must be running):

  ```json
  {"mcpServers": {"b2m": {"command": "C:\\...\\plinth\\service\\.venv\\Scripts\\python.exe", "args": ["-m", "b2m.mcp_server"]}}}
  ```

- Gate (calls Claude, small cost): `.venv\Scripts\python scripts\step3_gate.py`

### Local API

```sh
.venv\Scripts\python -m b2m.api      # http://127.0.0.1:8765
```

`POST /builds {"plan": ..., "document_title": "..."}` starts a build and returns a `run_id`;
`GET /runs/{id}` shows progress (`batches`), created ids, read-back and errors;
`POST /runs/{id}/remove` deletes exactly what that run created. Runs are kept in
`%LOCALAPPDATA%\B2M\runs\<run_id>\` (plan, run record, every tool call).

### Layout engine (Step 2)

`b2m.spec` validates a building spec (PRD format); `b2m.engine.generate(spec)` returns a build plan and a
report, or a `LayoutError` saying why nothing fits.

- Envelope: plot minus setbacks from `b2m/rules/kolkata_setbacks.yaml`. **The setback values are
  provisional placeholders** (`verified: false`) until Step 4 transcribes the official KMC rules.
- Templates: T2 (two flats), T3 (two front flats plus one full-depth flat), T4 (four flats), each around a
  stair-and-lobby column with the stair at the front and an optional lift at the rear.
- Flats: one deep layout for 1/2/3BHK (living room at the outer end, a passage, one row of rooms on the
  outer wall), sized to the target carpet area, never below the room minimums in `engine/catalog.py`.
- Walls are derived from the boundaries between spaces, so they cannot overlap; doors and windows stay
  clear of wall ends and junctions; every floor is validated before a plan is emitted.
- The engine names logical types (`Brick 230`, `RCC 150`, `Door 900x2100`...). A project not made from the
  B2M template needs `%LOCALAPPDATA%\B2M\standards.json` mapping them to its own types (see
  `b2m/revit/types.py`).

```sh
.venv\Scripts\python scripts\render_floor.py tests\golden\specs\14-t4-2bhk-stilt-g4-lift.json plan.png
.venv\Scripts\python scripts\update_golden.py      # after an intended engine change; review the diff
```

Golden tests (`tests/golden`) hold 20 specs covering every template and flat type; each must give exactly
its stored plan.

### Command tests and Step 1 gate

Revit must be closed while deploying a new add-in build (Revit locks the loaded DLL).

```sh
set B2M_LIVE=1
set B2M_TITLE=<scratch project title>
.venv\Scripts\python -m pytest tests/live -v
.venv\Scripts\python scripts\step1_gate.py --title "<scratch project title>"
.venv\Scripts\python scripts\step2_gate.py --title "<scratch project title>" [--types standards.json]
```
