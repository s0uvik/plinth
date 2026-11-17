// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/CreatePlaceholderSheetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: failures are errors rather than a success with created=false; a taken sheet number is
// an error carrying the existing sheet's id; the camelCase aliases are gone (the schema rejects them).

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Sheets
{
    public sealed class CreatePlaceholderSheetHandler : IRevitCommand
    {
        public string Name => "create_placeholder_sheet";
        public string Description =>
            "Create a placeholder sheet (a sheet-list row with a number and name but no title block or views, e.g. for a consultant's drawing).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheet_number"":{""type"":""string""},
            ""sheet_name"":{""type"":""string""}},
            ""required"":[""sheet_number"",""sheet_name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var sheetNumber = p.Value<string>("sheet_number");
            var sheetName = p.Value<string>("sheet_name");
            if (string.IsNullOrWhiteSpace(sheetNumber)) return CommandResult.Fail("INVALID_PARAMS: sheet_number cannot be empty.");
            if (string.IsNullOrWhiteSpace(sheetName)) return CommandResult.Fail("INVALID_PARAMS: sheet_name cannot be empty.");
            if (SheetsHelpers.SheetNumberInUse(doc, sheetNumber, out var existing))
                return CommandResult.Fail("SHEET_NUMBER_IN_USE: sheet number '" + existing.SheetNumber + "' already exists.",
                    new { existingSheetId = RevitCompat.GetId(existing.Id) });

            using (var tx = new Transaction(doc, "Plinth: Create Placeholder Sheet"))
            {
                tx.Start();
                try
                {
                    var sheet = ViewSheet.CreatePlaceholder(doc);
                    sheet.SheetNumber = sheetNumber;
                    sheet.Name = sheetName;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new
                    {
                        sheet_id = RevitCompat.GetId(sheet.Id),
                        sheet_number = sheet.SheetNumber,
                        sheet_name = sheet.Name,
                        is_placeholder = sheet.IsPlaceholder
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the placeholder sheet: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
