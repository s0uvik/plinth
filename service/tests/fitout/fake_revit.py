"""A small stateful Revit document for fit-out tests: project types, installed templates, placed elements, MEP runs,
circuits, and the commands the fit-out uses with the add-in's parameter and result shapes (see the C# handlers).

It keeps every element (id, category, level, family/type, what it was made by) so a test can check exactly what a
run added and what removing it left behind. A batch is all or nothing, as in the add-in.
"""

from __future__ import annotations

import copy
from typing import Any

from b2m.revit.client import RevitError

FAMILY_KINDS = ("family_type",)
MEP_CATEGORY = {"pipe_type": "OST_PipeCurves", "pipe_system_type": "OST_PipingSystem", "duct_type": "OST_DuctCurves",
                "duct_system_type": "OST_DuctSystem", "conduit_type": "OST_Conduit", "cable_tray_type": "OST_CableTray"}
CURVE = {"pipe": "OST_PipeCurves", "duct": "OST_DuctCurves", "conduit": "OST_Conduit", "cable_tray": "OST_CableTray"}
FITTING = {"pipe": "OST_PipeFitting", "duct": "OST_DuctFitting", "conduit": "OST_ConduitFitting",
           "cable_tray": "OST_CableTrayFitting"}


def _fail(error: str, data: Any = None) -> RevitError:
    return RevitError({"success": False, "error": error, "data": data})


class FitoutRevit:
    """The fake document. ``types``: project family types {category, family, type, dims (mm), writable (names),
    placement, box: what measure_family_types answers for it {min, max, base_offset_mm} (mm, about the point asked)};
    ``mep``: project MEP types {kind, name}; ``templates``: {path: {"family_types": [...], "mep_types": {kind: [...]}}}
    in list_types_in_file's shape; ``elements``: id -> {category, level, ...} already in the model (a built run)."""

    def __init__(self, types=None, mep=None, templates=None, elements=None, title="Scratch", year="2026"):
        self.next_id = 50000
        self.elements: dict[int, dict[str, Any]] = {}
        for t in types or []:
            type_id = self._add_type("family_type", t["type"], t["family"], t["category"], dims=t.get("dims") or {},
                                     writable=t.get("writable"), placement=t.get("placement", "OneLevelBased"))
            if t.get("box"):  # what measure_family_types answers for it: {min, max, base_offset_mm?} (mm)
                self.elements[type_id]["box"] = dict(t["box"])
        for m in mep or []:
            self._add_type(m["kind"], m["name"])
        for element_id, element in (elements or {}).items():
            self.elements[element_id] = dict(element)
        self.templates = templates or {}
        self.title = title
        self.year = year
        self.calls: list[tuple[str, dict]] = []
        self.health = {"addin_version": "0.9.0", "revit_year": int(year)}
        self.observer = None
        self.warnings = 0

    # ------------------------------------------------------------------ helpers

    def _new_id(self) -> int:
        self.next_id += 1
        return self.next_id

    def _add_type(self, kind, name, family=None, category=None, dims=None, writable=None, placement="OneLevelBased",
                  made_by=None) -> int:
        if family is not None and not any(e.get("is_family") and e["family"] == family for e in self.elements.values()):
            self.elements[self._new_id()] = {"is_family": True, "family": family, "category": category,
                                             "made_by": made_by}
        type_id = self._new_id()
        self.elements[type_id] = {"is_type": True, "kind": kind, "type": name, "family": family,
                                  "category": category or MEP_CATEGORY.get(kind), "dims": dict(dims or {}),
                                  "writable": list(writable if writable is not None else (dims or {}).keys()),
                                  "placement": placement, "made_by": made_by}
        return type_id

    def project_types(self, kind="family_type") -> dict[int, dict]:
        return {i: e for i, e in self.elements.items() if e.get("is_type") and e["kind"] == kind}

    def instances(self) -> dict[int, dict]:
        return {i: e for i, e in self.elements.items()
                if not e.get("is_type") and not e.get("is_family") and not e.get("is_material")}

    def _find_type(self, kind, name, family=None, category=None) -> int:
        for i, e in self.project_types(kind).items():
            if e["type"] == name and (family is None or e["family"] == family) and (category is None or e["category"] == category):
                return i
        raise _fail(f"TYPE_NOT_FOUND: no {kind} {family!r}: {name!r} in the project")

    # ------------------------------------------------------------------ the RevitClient surface

    def confirm_document(self, expected_title=None):
        if expected_title is not None and expected_title != self.title:
            raise ConnectionError("wrong document")
        return self.title

    def __enter__(self):
        return self

    def __exit__(self, *a):
        pass

    def call(self, command, params=None, *, timeout_seconds=None):
        params = params or {}
        self.calls.append((command, copy.deepcopy(params)))
        result = getattr(self, "_" + command)(params)
        if self.observer:
            self.observer({"command": command, "params": params}, {"success": True})
        return result

    # ------------------------------------------------------------------ reading

    def _list_template_files(self, p):
        files = [{"name": path.rsplit("\\", 1)[-1], "path": path, "language": "English"} for path in self.templates]
        return {"year": self.year, "running_year": self.year, "exists": True, "count": len(files), "files": files}

    def _list_types_in_file(self, p):
        listing = self.templates.get(p["path"])
        if listing is None:
            raise _fail("FILE_NOT_FOUND: " + p["path"])
        kinds = p.get("kinds")
        out: dict[str, Any] = {"path": p["path"], "file_name": p["path"].rsplit("\\", 1)[-1], "from_cache": False}
        if kinds is None or "family_type" in kinds:
            rows = [r for r in listing.get("family_types", []) if not p.get("categories") or r["category"] in p["categories"]]
            out["family_types"], out["family_types_total"] = rows, len(rows)
        mep = {k: v for k, v in listing.get("mep_types", {}).items() if kinds is None or k in kinds}
        if mep:
            out["mep_types"] = mep
        return out

    def _get_available_family_types(self, p):
        bic = p.get("builtInCategory")
        if bic is None:  # every loadable family type, grouped by category (the add-in lists FamilySymbols)
            groups: dict[str, list] = {}
            for i, e in self.elements.items():
                if e.get("is_type") and e["kind"] == "family_type":
                    groups.setdefault(e["category"], []).append({"typeId": i, "familyName": e["family"], "typeName": e["type"]})
            return {"totalCategories": len(groups), "totalTypes": sum(len(v) for v in groups.values()),
                    "categories": [{"category": c, "builtInCategory": c, "count": len(v), "types": v}
                                   for c, v in sorted(groups.items())]}
        rows = [(i, e) for i, e in self.elements.items() if e.get("is_type") and e["category"] == bic]
        types = [{"typeId": i, "familyName": e["family"] or e["kind"], "typeName": e["type"]} for i, e in rows]
        return {"totalCategories": 1 if types else 0, "totalTypes": len(types),
                "categories": [{"category": bic, "builtInCategory": bic, "count": len(types), "types": types}] if types else []}

    def _get_type_parameters(self, p):
        out = []
        for type_id in p.get("typeIds") or []:
            e = self.elements[type_id]
            parameters = [{"name": name.capitalize(), "storageType": "Double", "unit": "mm", "value": value,
                           "isReadOnly": name not in e["writable"]} for name, value in e["dims"].items()]
            out.append({"typeId": type_id, "typeName": e["type"], "familyName": e["family"],
                        "builtInCategory": e["category"], "parameters": parameters})
        return {"count": len(out), "types": out}

    def _measure_family_types(self, p):
        """One instance per type at the level's origin, measured and rolled back (as the add-in): nothing changes."""
        level = p.get("level") or "L1"
        types, skipped = [], []
        for type_id in p["type_ids"]:
            e = self.elements.get(type_id)
            if e is None or not e.get("is_type") or e["kind"] != "family_type":
                skipped.append({"type_id": type_id, "reason": "not a family type in this document"})
            elif e["placement"] not in ("OneLevelBased", "WorkPlaneBased"):
                skipped.append({"type_id": type_id, "reason": f"placement {e['placement']}: it needs a host to be measured"})
            elif not e.get("box"):
                skipped.append({"type_id": type_id, "reason": "no box in the fake"})
            else:
                box = e["box"]
                types.append({"type_id": type_id, "family": e["family"], "type": e["type"], "placement": e["placement"],
                              "min": list(box["min"]), "max": list(box["max"]),
                              "base_offset_mm": box.get("base_offset_mm", 0.0), "facing": [0.0, 1.0, 0.0],
                              "hand": [1.0, 0.0, 0.0], "level": level})
        return {"level": level, "level_elevation_mm": 3000.0, "measured": len(types), "types": types, "skipped": skipped}

    def _analyze_model_statistics(self, p):
        ids = p.get("elementIds") or []
        counts: dict[tuple[str, str | None], int] = {}
        missing = []
        for i in ids:
            e = self.elements.get(i)
            if e is None:
                missing.append(i)
                continue
            key = (e["category"], e.get("level") if p.get("groupByLevel") else None)
            counts[key] = counts.get(key, 0) + 1
        rows = [{"category": c, "builtInCategory": c, "count": n, **({"level": lv} if p.get("groupByLevel") else {})}
                for (c, lv), n in sorted(counts.items(), key=lambda kv: (kv[0][0], kv[0][1] or ""))]
        return {"missingIds": missing, "categories": rows}

    def _get_model_warnings_summary(self, p):
        return {"total_warnings": self.warnings, "warnings": []}

    # ------------------------------------------------------------------ types

    def _copy_types_from_file(self, p):
        listing = self.templates[p["path"]]
        before = set(self.elements)
        results = []
        for index, item in enumerate(p["items"]):
            row: dict[str, Any] = {"index": index, "kind": item["kind"], "name": item["name"]}
            if item["kind"] == "family_type":
                found = [r for r in listing.get("family_types", []) if r["type"] == item["name"]
                         and r["family"] == item.get("family") and (not item.get("category") or r["category"] == item["category"])]
            else:
                found = [r for r in listing.get("mep_types", {}).get(item["kind"], []) if r["name"] == item["name"]]
            if len(found) != 1:
                results.append({**row, "status": "not_found" if not found else "ambiguous"})
                continue
            source = found[0]
            family = source.get("family")
            row.update(source_id=source["id"], family=family, category=source.get("category"))
            existing = [i for i, e in self.project_types(item["kind"]).items()
                        if e["type"] == item["name"] and e["family"] == family]
            if existing:
                results.append({**row, "status": "already_there", "existing_id": existing[0]})
                continue
            new_id = self._add_type(item["kind"], item["name"], family, source.get("category"), dims=source.get("dims_mm"),
                                    writable=source.get("writable"), placement=source.get("placement", "OneLevelBased"),
                                    made_by="copy")
            results.append({**row, "status": "copied", "new_id": new_id, "new_name": item["name"],
                            **({"new_family_name": family} if family else {})})
        created = sorted(set(self.elements) - before)
        count = lambda s: sum(r["status"] == s for r in results)  # noqa: E731
        return {"file_name": p["path"].rsplit("\\", 1)[-1], "copied": count("copied"), "already_there": count("already_there"),
                "not_found": count("not_found") + count("ambiguous"), "failed": 0, "items": results, "created_ids": created}

    def _duplicate_family_type(self, p):
        source = self.elements.get(p["source_type_id"])
        if source is None or not source.get("is_type"):
            raise _fail("ELEMENT_NOT_FOUND: no type " + str(p["source_type_id"]))
        for i, e in self.project_types(source["kind"]).items():
            if e["family"] == source["family"] and e["type"] == p["new_type_name"]:
                raise _fail(f"NAME_IN_USE: a type named '{p['new_type_name']}' already exists in this family.",
                            {"existing_type_id": i})
        dims = dict(source["dims"])
        for name, value in (p.get("type_parameter_overrides") or {}).items():
            if name.lower() not in dims:
                raise _fail(f"PARAMETER_NOT_FOUND: the type has no parameter named '{name}'.")
            if name.lower() not in source["writable"]:
                raise _fail(f"SET_FAILED: '{name}': the parameter is read-only.")
            dims[name.lower()] = value
        new_id = self._add_type(source["kind"], p["new_type_name"], source["family"], source["category"], dims=dims,
                                writable=source["writable"], placement=source["placement"], made_by="duplicate")
        return {"duplicated": True, "source_type_id": p["source_type_id"], "new_type_id": new_id,
                "new_type_name": p["new_type_name"], "family_name": source["family"]}

    def _create_box_family(self, p):
        """As the add-in's create_box_family: a NEW family with one type (Width, Depth, Height), origin at the middle of its
        back, front -Y, base at the level; its material comes along when the project has none of that name. A family of
        that name already in the project is returned as already_there (never overwritten, nothing created)."""
        name = p["family_name"]
        w, d, h = p["width_mm"], p["depth_mm"], p["height_mm"]
        type_name = p.get("type_name") or f"{w:g} x {d:g} x {h:g}mm (B2M)"
        family = next((i for i, e in self.elements.items() if e.get("is_family") and e["family"].casefold() == name.casefold()), None)
        if family is not None:
            types = [(i, e) for i, e in self.project_types().items() if e["family"] == self.elements[family]["family"]]
            chosen = next(((i, e) for i, e in types if e["type"] == type_name), types[0] if types else (None, None))
            return {"action": "already_there", "dry_run": False, "family_name": self.elements[family]["family"],
                    "type_name": chosen[1]["type"] if chosen[1] else None, "family_id": family, "type_id": chosen[0],
                    "category": self.elements[family]["category"], "created_ids": [],
                    "notes": [] if chosen[1] and chosen[1]["type"] == type_name else [f"it has no type '{type_name}'"]}
        before = set(self.elements)
        type_id = self._add_type("family_type", type_name, name, p.get("category") or "OST_Furniture",
                                 dims={"width": w, "depth": d, "height": h}, placement="OneLevelBased", made_by="box")
        self.elements[type_id]["box"] = {"min": [-w / 2, -d, 0], "max": [w / 2, 0, h]}
        material = p.get("material_name") or "B2M Fit-out"
        if not any(e.get("is_material") and e["name"] == material for e in self.elements.values()):
            self.elements[self._new_id()] = {"is_material": True, "name": material, "category": "OST_Materials"}
        family_id = next(i for i in sorted(set(self.elements) - before) if self.elements[i].get("is_family"))
        others = [i for i in sorted(set(self.elements) - before) if i not in (family_id, type_id)]
        return {"action": "created", "dry_run": False, "family_name": name, "type_name": type_name, "family_id": family_id,
                "type_id": type_id, "category": p.get("category") or "OST_Furniture", "template": "Metric Furniture.rft",
                "placement": "OneLevelBased", "created_ids": [family_id, type_id, *others], "brought_along_ids": others}

    # ------------------------------------------------------------------ building

    def _batch_execute(self, p):
        saved = copy.deepcopy((self.elements, self.next_id))
        results = []
        for index, sub in enumerate(p["commands"]):
            assert sub["command"] not in ("copy_types_from_file", "list_types_in_file", "duplicate_family_type",
                                          "create_box_family"), sub
            try:
                results.append({"index": index, "ok": True, "data": getattr(self, "_" + sub["command"])(sub["params"])})
            except RevitError as e:
                self.elements, self.next_id = saved
                raise RevitError({"success": False, "error": "BATCH_ROLLED_BACK: " + e.error,
                                  "data": {"results": [{"index": index, "ok": False, "error": e.error}], "rolledBack": True}})
        return {"results": results, "rolledBack": False}

    def _place(self, p, snake: bool, category: str | None):
        name = (lambda c, s: s) if snake else (lambda c, s: c)
        type_id = self._find_type("family_type", p[name("typeName", "type_name")], p.get(name("familyName", "family_name")),
                                  category)
        symbol = self.elements[type_id]
        placement = symbol["placement"]
        host = p.get("host_id")
        if host is not None and host not in self.elements:
            raise _fail(f"ELEMENT_NOT_FOUND: host {host} is not in this document.")
        side = p.get(name("wallSide", "wall_side")) or p.get(name("towardX", "toward_x")) is not None
        if placement == "OneLevelBased" and (side or p.get(name("faceDown", "face_down"))):
            raise _fail("INVALID_PARAMS: a wall side applies to wall-hosted and face-based families; this family is level-based.")
        if placement == "OneLevelBasedHosted" and host is None:
            raise _fail("HOST_REQUIRED: this family is hosted; give host_id. Nothing was created.")
        if placement == "WorkPlaneBased" and host is not None and not side:
            raise _fail("SIDE_REQUIRED: a face-based family on a wall needs the side.")
        if host is not None and p.get("rotation"):
            raise _fail("INVALID_PARAMS: a hosted family follows its host; rotation is not allowed.")
        instance = self._new_id()
        self.elements[instance] = {"category": symbol["category"], "level": p["level"], "type_id": type_id,
                                   "family": symbol["family"], "type": symbol["type"], "host": host,
                                   "x": p["x"], "y": p["y"], "rotation": p.get("rotation", 0), "offset": p.get("offset", 0)}
        created = [instance]
        if p.get(name("faceDown", "face_down")):
            plane = self._new_id()
            self.elements[plane] = {"category": "OST_CLines", "level": None}
            created.append(plane)
        return instance, created

    def _create_point_based_element(self, p):
        instance, created = self._place(p, False, p.get("builtInCategory"))
        return {"elementId": instance, "created_ids": created, "builtInCategory": self.elements[instance]["category"]}

    def _create_lighting_fixture(self, p):
        instance, created = self._place(p, True, "OST_LightingFixtures")
        return {"instance_id": instance, "created_ids": created}

    def _create_air_terminal(self, p):
        instance, created = self._place(p, True, "OST_DuctTerminal")
        return {"instance_id": instance, "created_ids": created}

    def _create_mep_run(self, p):
        kind = p["kind"]
        self._find_type(f"{kind}_type", p["type_name"])
        if kind in ("pipe", "duct"):
            self._find_type(f"{kind}_system_type", p["system_type_name"])
        for end in ("start_connect", "end_connect"):
            if end in p and p[end]["element_id"] not in self.elements:
                raise _fail(f"ELEMENT_NOT_FOUND: {end} {p[end]['element_id']} is not in this document.")
        segments, fittings = [], []
        for _ in range(len(p["points"]) - 1):
            segments.append(self._new_id())
            self.elements[segments[-1]] = {"category": CURVE[kind], "level": p["level"]}
        for _ in range(len(p["points"]) - 2):
            fittings.append(self._new_id())
            self.elements[fittings[-1]] = {"category": FITTING[kind], "level": p["level"]}
        return {"kind": kind, "segment_ids": segments, "fitting_ids": fittings, "created_ids": segments + fittings,
                "modified_ids": [], "failed_connections": []}

    def _create_electrical_circuit(self, p):
        for i in p["element_ids"]:
            if i not in self.elements:
                raise _fail(f"NO_ELECTRICAL_CONNECTOR: {i}: not in this document. Nothing was created.")
        if p.get("panel_id") is not None and self.elements[p["panel_id"]]["category"] != "OST_ElectricalEquipment":
            raise _fail("NOT_A_PANEL: not electrical equipment")
        circuit = self._new_id()
        self.elements[circuit] = {"category": "OST_ElectricalCircuit", "level": None, "members": list(p["element_ids"])}
        return {"circuit_id": circuit, "panel_id": p.get("panel_id"), "element_ids": p["element_ids"], "created_ids": [circuit]}

    # ------------------------------------------------------------------ removing

    def _delete_element(self, p):
        deleted, failed = 0, []
        for i in p["elementIds"]:
            e = self.elements.pop(i, None)
            if e is None:
                failed.append(i)
                continue
            deleted += 1
            if e.get("is_family"):  # its types go with it
                for j in [j for j, t in self.elements.items() if t.get("is_type") and t["family"] == e["family"]]:
                    del self.elements[j]
        return {"deleted": deleted, "failedCount": len(failed), "failedIds": failed,
                "errors": [f"{i}: not found" for i in failed]}
