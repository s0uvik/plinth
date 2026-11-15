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

### Local API

```sh
.venv\Scripts\python -m b2m.api      # http://127.0.0.1:8765
```

`POST /builds {"plan": ..., "document_title": "..."}` starts a build and returns a `run_id`;
`GET /runs/{id}` shows progress (`batches`), created ids, read-back and errors;
`POST /runs/{id}/remove` deletes exactly what that run created. Runs are kept in
`%LOCALAPPDATA%\B2M\runs\<run_id>\` (plan, run record, every tool call).

### Command tests and Step 1 gate

Revit must be closed while deploying a new add-in build (Revit locks the loaded DLL).

```sh
set B2M_LIVE=1
set B2M_TITLE=<scratch project title>
.venv\Scripts\python -m pytest tests/live -v
.venv\Scripts\python scripts\step1_gate.py --title "<scratch project title>"
```
