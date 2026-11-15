// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/CreateSheetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: with no title block named, the project's default title block is used (rvt-mcp took
// the first one found); a miss or an ambiguous name lists the valid title blocks; failures are errors
// rather than a success with created=false; the camelCase aliases are gone (the schema rejects them);
// the response carries the title block frame on the sheet in mm, for placing views.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class CreateSheetHandler : IRevitCommand
    {
        public string Name => "create_sheet";
        public string Description =>
            "Create a sheet with a number, a name and a title block (title_block_type_id, or title_block_name as 'Type' or 'Family: Type'; "
            + "default is the project's default title block). Returns the title block frame in sheet mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheet_number"":{""type"":""string""},
            ""sheet_name"":{""type"":""string""},
            ""title_block_type_id"":{""type"":""integer""},
            ""title_block_name"":{""type"":""string""}},
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

            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => string.Equals(s.SheetNumber, sheetNumber, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return CommandResult.Fail("SHEET_NUMBER_IN_USE: sheet number '" + existing.SheetNumber + "' already exists.",
                    new { existingSheetId = RevitCompat.GetId(existing.Id) });

            FamilySymbol titleBlock;
            var typeId = p.Value<long?>("title_block_type_id");
            var typeName = p.Value<string>("title_block_name");
            if (typeId.HasValue && !string.IsNullOrEmpty(typeName))
                return CommandResult.Fail("INVALID_PARAMS: pass title_block_type_id or title_block_name, not both.");
            if (typeId.HasValue || !string.IsNullOrEmpty(typeName))
            {
                if (!Lookup.TryTitleBlock(doc, typeId, typeName, out titleBlock, out var failure)) return failure;
            }
            else
            {
                var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType()
                    .OfType<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
                titleBlock = doc.GetElement(doc.GetDefaultFamilyTypeId(new ElementId(BuiltInCategory.OST_TitleBlocks))) as FamilySymbol
                    ?? (all.Count == 1 ? all[0] : null);
                if (titleBlock == null && all.Count > 1)
                    return CommandResult.Fail("TITLEBLOCK_REQUIRED: the project has no default title block; pass title_block_name.",
                        new JObject { ["valid_title_blocks"] = Lookup.TitleBlocks(all) });
            }

            using (var tx = new Transaction(doc, "B2M: Create Sheet"))
            {
                tx.Start();
                try
                {
                    var sheet = ViewSheet.Create(doc, titleBlock?.Id ?? ElementId.InvalidElementId);
                    sheet.SheetNumber = sheetNumber;
                    sheet.Name = sheetName;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        sheet_id = RevitCompat.GetId(sheet.Id),
                        sheet_number = sheet.SheetNumber,
                        sheet_name = sheet.Name,
                        title_block_type_id = titleBlock == null ? (long?)null : RevitCompat.GetId(titleBlock.Id),
                        title_block_name = titleBlock == null ? null : titleBlock.FamilyName + ": " + titleBlock.Name,
                        frame_sheet_mm = ViewportSheetSupport.RectDto(ViewportSheetSupport.GetSheetFrame(doc, sheet)),
                        warning = titleBlock == null ? "No title block family is loaded; the sheet has no title block." : null
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the sheet: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
