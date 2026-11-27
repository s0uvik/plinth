# Plinth quick start: drawings to a Revit model

Plinth reads an office's DWG drawing set (plans, sections, elevations, foundation and beam layouts), asks you about
everything the drawings leave open, and once you approve, builds a native Revit model. From that model it makes a
Revit drawing set and a door/window estimate in the office's Excel format. This page covers one complete run.

> **This is a trial (alpha).** It has been proven on one G+4 drawing set and on Revit 2026. Use a **scratch Revit
> project** and a **copy** of the drawings. Treat any compliance (NBC / KMC / West Bengal) result as advisory: the
> rule values are not yet verified.

## 1. Install (once)

You need Windows, Revit 2024, 2025, 2026 or 2027 (only 2026 has been tested live), and Python 3.12 from
python.org (tick "Add to PATH").

1. **ODA File Converter.** It converts DWG to DXF. It is free from opendesign.com ("ODA File Converter"). Install
   it to its default folder under `C:\Program Files\ODA\`. If you put it somewhere else, set the environment
   variable `PLINTH_ODA` to its folder.
2. **Plinth.** Close Revit completely. Unzip the Plinth package, then run this in PowerShell inside the unzipped folder:

   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```

   This installs the add-in for every Revit year it finds, and the Plinth service under `%LOCALAPPDATA%\Plinth`. To remove
   it later, run `uninstall.ps1`.

## 2. Start

1. Double-click `%LOCALAPPDATA%\Plinth\start-plinth.cmd`. A console window opens; leave it open while you work.
2. Open Revit, then start a **new project from the Metric template** and save it under a scratch name, e.g.
   `scratch-01`. Plinth builds only into the project whose title you give it.
3. Open **http://127.0.0.1:8765/import** in a browser.

## 3. Import, answer, approve, build

| Step | What you do | What to look for |
|---|---|---|
| Drawing folder | Paste the folder that holds the DWG (or DXF) sheets. Click **Import drawings**. | Every sheet is listed with what it was read as (plan, section, elevation, foundation, beam layout, site). A sheet read as the wrong kind is worth reporting. |
| Sheets | Click a sheet to see what Plinth recognised, drawn over the sheet. Click an element to see its source lines. | Missing or extra walls, doors, windows, columns and rooms. |
| Project location | Enter the City, Municipality, State and Country, then click **Save location**. | Plinth bases its assumptions on the location, so it asks for the location before assuming anything. |
| Questions | Answer each question, or click **Assume remaining** to accept the suggestions for that location. | Each assumption is listed with its reason. Change any that are wrong for this project. |
| Review and build | Add a short note (e.g. "checked all sheets against the PDF set") and click **Approve review**. Type the open project's title exactly as Revit shows it, then click **Build in Revit**. | The batches tick off one by one. A batch that fails stops the build and says why. |

**Family questions during the build.** Plinth first looks in the open project and in the family library installed with Revit.
If a size is missing, it adapts a **copy** of a close family. Only if nothing fits does it stop and ask whether to
use another family or create one. Answer the question, then build again.

**If the build stops on a question** (for example "2 loaded window families fit equally well"), click **Remove
generated model** first, answer the question on the review page, then click **Build in Revit** again. Your answer is
kept for the next build.

**Check the assumed values before approving.** "Assume remaining" fills what the drawings leave open with typical
values for the location (e.g. windows 900 sill, 2100 head). Where the office knows better (a ventilator 640 tall at
1460 sill, a stair window), change that answer before you approve.

**It will never** edit one of your existing types or families in place (it duplicates first), build without your
approval, or delete anything it did not create.

## 4. After the build

- **Self-check.** Plinth reads the model back and compares it with the plan and with the drawings. Open the
  self-check report. *Explained* differences are ones your answers caused (e.g. a door width you changed).
  *Unexplained* ones are what to check in Revit.
- **Make drawing set.** This makes sheets with views fitted to the title block at standard scales, and a PDF. It asks
  which title block to use and what to fill into the title fields; it never guesses those. **Remake drawing set**
  deletes the sheets and views Plinth made for this run and makes them again (it asks you to confirm first).
- **Make estimate.** This makes the door/window estimate workbook. Optionally, give the office's own estimate
  workbook to get a cell-by-cell comparison.
- **Remove generated model.** This deletes exactly what this run created, nothing else.

## 5. Fit-out (furniture and MEP)

Once the building is built, the **Fit-out** section at the bottom of the review page lays out furniture and the MEP
(plumbing and drainage, electrical, HVAC, fire) on it, and builds them as their own run.

> **The MEP is an indicative coordination model.** It is laid out from rules (NBC 2016 and typical practice for the
> location), not designed: an MEP engineer must design and check the real systems.

1. Choose the building run, check the Revit document title and tick the systems. Click **Plan fit-out**. Planning
   reads the open project (family types and their sizes) and builds nothing.
2. Read the plan: counts per system, the assumptions, and what was left out, not placed or not routed, with why.
3. **Family questions.** Where no family type fits an item, Plinth asks. Pick one of the types offered, **skip** the
   item, or let Plinth **create a plain box** family for it (it gets the pipe and electrical connectors the MEP joins
   to). The questions are in the question list too. Answer, then click **Plan fit-out** again.
4. Click **Build fit-out**. With questions still open, tick "build what is ready" to build the rest now.

Build the **furniture first**, then plan the MEP with that run chosen under "Furniture already built in run": the MEP
joins that furniture and never places it again. **Pilot one flat first** (flat and level), check it in Revit, then plan
the rest, leaving out what the pilot built.

**Issues are expected.** A run can end "Built, with issues": joins and circuits Revit refused, mostly on families
without a connector of that kind. The model is built; each issue is listed under the run.

**Remove** on a fit-out run deletes exactly what that run created. Remove the MEP run before the furniture run under
it, and the fit-out runs before the building.

## 6. What we need from your trial

For each run, write down **everything you had to fix by hand in Revit afterwards**. That list is the real measure of
the trial. For each item, note:

| Sheet | Element (e.g. wall near grid C/4, door D1 on Floor 2) | What was wrong | What you did |
|---|---|---|---|

When something fails, send the run folder `%LOCALAPPDATA%\Plinth\runs\<run id>\` (every Revit call Plinth made is in
`tool_calls.jsonl`) and the import folder `%LOCALAPPDATA%\Plinth\imports\<import id>\`. The run id is shown on the
review page.

## Known limits

- **Drawing set:** the template's view title wraps a long view name over its scale; grid heads of grids under
  ~150 mm apart overlap. Title-block fields (project name, client, drawn by...) stay blank until answered on the
  review page.
- **Estimate:** stair railings are measured as Revit built them (inner and outer railing per stair, landing
  included); an office that counts one railing per flight will see a difference to answer.

- Proven on one G+4 RCC frame set (Bardhaman). Other offices draw differently; expect new questions.
- Live-tested on Revit 2026 only.
- Not modelled: reinforcement, roof falls (slope arrows), plain wall openings without a door or window. Each is
  listed in the self-check as *not modelled*, with its reason.
- Compliance rule values (NBC 2016, KMC, West Bengal) are placeholders until verified. Use them as advisory only.
