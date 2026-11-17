// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/RenumberSheetsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: conflicts and failures are errors carrying the planned changes and conflicts (rvt-mcp
// returned success with an error field); items are checked field by field (unknown fields, wrong types, both
// or neither of sheet_id/sheet_number) because the schema validator does not look inside array items; an
// unknown sheet lists the valid ones; items cannot be mixed with find/replace/prefix/suffix; replace needs
// find; the camelCase aliases are gone. dry_run still defaults to true.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Sheets
{
    public sealed class RenumberSheetsHandler : IRevitCommand
    {
        private static readonly string[] ItemFields = { "sheet_id", "sheet_number", "new_sheet_number", "new_sheet_name" };

        private sealed class Change
        {
            public ViewSheet Sheet;
            public string OldNumber, NewNumber, OldName, NewName;
        }

        public string Name => "renumber_sheets";
        public string Description =>
            "Renumber or rename sheets, swaps included. Either items [{sheet_id or sheet_number, new_sheet_number?, new_sheet_name?}], "
            + "or rules applied to every sheet number: find/replace (case-insensitive text), then prefix and suffix. "
            + "dry_run defaults to TRUE and only reports the plan; pass dry_run false to apply. Collisions are refused before anything changes.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""items"":{""type"":""array"",""items"":{""type"":""object""}},
            ""find"":{""type"":""string""},
            ""replace"":{""type"":""string""},
            ""prefix"":{""type"":""string""},
            ""suffix"":{""type"":""string""},
            ""dry_run"":{""type"":""boolean""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var items = p["items"] as JArray;
            var find = p.Value<string>("find") ?? "";
            var replace = p.Value<string>("replace");
            var prefix = p.Value<string>("prefix") ?? "";
            var suffix = p.Value<string>("suffix") ?? "";
            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var rulesGiven = find.Length > 0 || replace != null || prefix.Length > 0 || suffix.Length > 0;

            var allSheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            var changes = new List<Change>();

            if (items != null)
            {
                if (rulesGiven) return CommandResult.Fail("INVALID_PARAMS: pass items, or find/replace/prefix/suffix rules, not both.");
                if (items.Count == 0) return CommandResult.Fail("INVALID_PARAMS: items cannot be empty.");
                for (var i = 0; i < items.Count; i++)
                {
                    if (!TryItem(items[i] as JObject, i, allSheets, out var change, out var failure)) return failure;
                    changes.Add(change);
                }
            }
            else
            {
                if (!rulesGiven) return CommandResult.Fail("INVALID_PARAMS: pass items, or at least one of find/replace, prefix, suffix.");
                if (replace != null && find.Length == 0) return CommandResult.Fail("INVALID_PARAMS: replace needs find.");
                foreach (var sheet in allSheets)
                {
                    var number = find.Length > 0
                        ? Regex.Replace(sheet.SheetNumber, Regex.Escape(find), (replace ?? "").Replace("$", "$$"), RegexOptions.IgnoreCase)
                        : sheet.SheetNumber;
                    number = prefix + number + suffix;
                    if (string.IsNullOrWhiteSpace(number))
                        return CommandResult.Fail("INVALID_PARAMS: the rules leave sheet " + sheet.SheetNumber + " with an empty number.");
                    changes.Add(new Change { Sheet = sheet, OldNumber = sheet.SheetNumber, NewNumber = number, OldName = sheet.Name, NewName = sheet.Name });
                }
            }

            var effective = changes.Where(c => c.OldNumber != c.NewNumber || c.OldName != c.NewName).ToList();
            if (effective.Count == 0) return CommandResult.Fail("NO_CHANGES: the request changes no sheet number or name.");

            var conflicts = new JArray();
            foreach (var group in effective.GroupBy(c => RevitCompat.GetId(c.Sheet.Id)).Where(g => g.Count() > 1))
                foreach (var c in group)
                    conflicts.Add(Conflict(c, null, "The same sheet appears more than once in items."));
            foreach (var group in effective.GroupBy(c => c.NewNumber, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var c in group)
                    conflicts.Add(Conflict(c, c.NewNumber, "Two sheets would get the same number."));
            var moving = new HashSet<long>(effective.Select(c => RevitCompat.GetId(c.Sheet.Id)));
            var staying = new HashSet<string>(allSheets.Where(s => !moving.Contains(RevitCompat.GetId(s.Id))).Select(s => s.SheetNumber),
                StringComparer.OrdinalIgnoreCase);
            foreach (var c in effective.Where(c => staying.Contains(c.NewNumber)))
                conflicts.Add(Conflict(c, c.NewNumber, "Another sheet that is not being renumbered already has this number."));

            var planned = new JArray(effective.Select(c => new JObject
            {
                ["sheet_id"] = RevitCompat.GetId(c.Sheet.Id),
                ["old_sheet_number"] = c.OldNumber,
                ["new_sheet_number"] = c.NewNumber,
                ["old_sheet_name"] = c.OldName,
                ["new_sheet_name"] = c.NewName
            }));
            if (conflicts.Count > 0)
                return CommandResult.Fail("RENUMBER_CONFLICT: " + conflicts.Count + " conflict(s); nothing was changed.",
                    new JObject { ["dry_run"] = dryRun, ["changes"] = planned, ["conflicts"] = conflicts });
            if (dryRun)
                return CommandResult.Ok(new JObject { ["dry_run"] = true, ["updated"] = false, ["count"] = planned.Count, ["changes"] = planned });

            using (var tx = new Transaction(doc, "Plinth: Renumber Sheets"))
            {
                tx.Start();
                try
                {
                    // Two passes: unique temporary numbers first, so swaps and chains never collide midway.
                    for (var i = 0; i < effective.Count; i++)
                        effective[i].Sheet.SheetNumber = "PLINTH_TMP_" + Guid.NewGuid().ToString("N") + "_" + i;
                    foreach (var c in effective)
                    {
                        c.Sheet.SheetNumber = c.NewNumber;
                        if (c.Sheet.Name != c.NewName) c.Sheet.Name = c.NewName;
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("RENUMBER_FAILED: " + ex.Message + " Nothing was changed.", new JObject { ["changes"] = planned });
                }
            }
            return CommandResult.Ok(new JObject { ["dry_run"] = false, ["updated"] = true, ["count"] = planned.Count, ["changes"] = planned });
        }

        private static bool TryItem(JObject item, int index, List<ViewSheet> allSheets, out Change change, out CommandResult failure)
        {
            change = null;
            failure = null;
            var at = "items[" + index + "]";
            if (item == null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + at + " must be an object.");
                return false;
            }
            var stray = item.Properties().Select(x => x.Name).Where(n => !ItemFields.Contains(n)).ToList();
            if (stray.Count > 0)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + at + " has unknown field(s) " + string.Join(", ", stray) + ". Valid: " + string.Join(", ", ItemFields) + ".");
                return false;
            }
            var idToken = item["sheet_id"];
            if (idToken != null && idToken.Type != JTokenType.Integer
                || item.Properties().Any(x => x.Name != "sheet_id" && x.Value.Type != JTokenType.String && x.Value.Type != JTokenType.Null))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: in " + at + ", sheet_id must be an integer and the other fields strings.");
                return false;
            }
            var id = idToken?.Value<long>();
            var number = item.Value<string>("sheet_number");
            if (id.HasValue == !string.IsNullOrEmpty(number))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + at + " needs exactly one of sheet_id or sheet_number.");
                return false;
            }
            var sheet = id.HasValue
                ? allSheets.FirstOrDefault(s => RevitCompat.GetId(s.Id) == id.Value)
                : allSheets.FirstOrDefault(s => string.Equals(s.SheetNumber, number, StringComparison.OrdinalIgnoreCase));
            if (sheet == null)
            {
                failure = CommandResult.Fail("SHEET_NOT_FOUND: " + at + ": no sheet " + (id.HasValue ? "has id " + id.Value : "numbered '" + number + "'") + ".",
                    new JObject
                    {
                        ["valid_sheets"] = new JArray(allSheets.Take(100).Select(s => new JObject
                        {
                            ["id"] = RevitCompat.GetId(s.Id),
                            ["number"] = s.SheetNumber,
                            ["name"] = s.Name
                        }))
                    });
                return false;
            }
            var newNumber = item.Value<string>("new_sheet_number");
            var newName = item.Value<string>("new_sheet_name");
            if (newNumber == null && newName == null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + at + " needs new_sheet_number and/or new_sheet_name.");
                return false;
            }
            if (newNumber != null && string.IsNullOrWhiteSpace(newNumber) || newName != null && string.IsNullOrWhiteSpace(newName))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: " + at + ": a new number or name cannot be empty.");
                return false;
            }
            change = new Change
            {
                Sheet = sheet,
                OldNumber = sheet.SheetNumber,
                NewNumber = newNumber?.Trim() ?? sheet.SheetNumber,
                OldName = sheet.Name,
                NewName = newName?.Trim() ?? sheet.Name
            };
            return true;
        }

        private static JObject Conflict(Change c, string number, string reason) => new JObject
        {
            ["sheet_id"] = RevitCompat.GetId(c.Sheet.Id),
            ["sheet_number"] = c.OldNumber,
            ["conflict_number"] = number,
            ["reason"] = reason
        };
    }
}
