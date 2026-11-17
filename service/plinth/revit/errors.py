"""Plain-language hints for the add-in's error codes, shown next to the raw error in runs and on the page."""

from __future__ import annotations

HINTS = {
    "DOCUMENT_UNCONFIRMED": "Confirm which Revit document to build in before writing.",
    "DOCUMENT_CHANGED": "The open Revit document changed (or Revit restarted). Switch back to the confirmed document, "
                        "or start again so the new one is confirmed.",
    "NO_DOCUMENT": "Open the project in Revit first.",
    "REVIT_REQUEST_NOT_STARTED": "Revit was busy (often a dialog is open). Close any dialog in Revit and try again; nothing ran.",
    "REVIT_OPERATION_TIMED_OUT": "Revit is still working on this. Wait for it to finish before doing anything else in the model.",
    "REVIT_SHUTTING_DOWN": "Revit is closing. Reopen it and the project, then try again.",
    "UNAUTHORIZED": "The add-in restarted. Try again; the service reads the new connection details automatically.",
    "RATE_LIMITED": "Too many requests at once. Wait a few seconds and try again.",
    "BATCH_ROLLED_BACK": "One step failed, so Revit undid that whole batch. Nothing from it was kept; earlier batches remain.",
    "LEVEL_NOT_FOUND": "A level the plan expects is missing from the model.",
    "TYPE_NOT_FOUND": "A wall, floor, door or window type is missing. Load it, or map it in standards.json.",
    "FAMILY_NOT_FOUND": "Plinth could not find this family in the project or the family libraries on this PC, nor a "
                        "similar one to adapt. Say whether Plinth may create it from Revit's family template, or give "
                        "the folder that holds your family file.",
    "TYPE_DIMENSION_MISMATCH": "A type with this name exists but has a different size. Plinth never edits existing types; "
                               "rename the type in standards.json or fix it in Revit.",
    "TYPE_AMBIGUOUS": "Several families have a type with this name. Give the family name in standards.json.",
    "NOT_COMMITTED": "Revit refused the change because it raised a warning or error; see the failures listed.",
    "NAME_IN_USE": "Something with this name already exists in the model, perhaps from an earlier run. Remove that run first.",
    "ROOM_NOT_ENCLOSED": "A room point is not inside closed walls.",
}


def hint(error: str | None) -> str | None:
    if not error:
        return None
    code = error.split(":", 1)[0].strip()
    for part in [code] + [p.strip().split(":", 1)[0] for p in error.split(":")[1:2]]:
        if part in HINTS:
            return HINTS[part]
    return None
