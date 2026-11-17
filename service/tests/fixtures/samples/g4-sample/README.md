# G+4 sample drawing set (Step 9 test fixture)

A real office drawing set used to build and gate the drawing-to-model pipeline (PRD Step 9). It is a test case for
the system, not a project Plinth is built for: no pipeline code may branch on it.

- `dwg/`: the 9 source DWGs as received (AutoCAD 2018 format).
- `dxf/`: the same, converted with ODA File Converter 27.10 (ACAD2018 DXF). Tests read these.
- `drawing-set.pdf`: the printed sheet set (9 A4 pages).
- `door-window-estimate.xlsx`: the office's door/window estimate for this building (expected output of the estimate engine).

Location shown on the site plan: Golapbag More, GT Road, Bardhaman (West Bengal).
