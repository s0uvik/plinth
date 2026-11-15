// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/GetTitleblockParametersHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: sheet resolution through Lookup.TrySheet (a miss lists the valid sheets; placeholders
// refused); a sheet with several title blocks needs title_block_id (rvt-mcp read the first); parameters are
// ordered by name and each carries its unit (lengths mm, areas m², volumes m³, angles degrees, converted
// through SheetsHelpers/Mm); the camelCase aliases are gone.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Sheets
{
    public sealed class GetTitleblockParametersHandler : IRevitCommand
    {
        public string Name => "get_titleblock_parameters";
        public string Description =>
            "Read the title block parameters of a sheet (sheet_id or sheet_number, default the active sheet). target: instance, type or both "
            + "(default both); include_read_only (default true). Lengths are mm, areas m², volumes m³, angles degrees. "
            + "title_block_id picks one when the sheet has several title blocks.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""sheet_id"":{""type"":""integer""},
            ""sheet_number"":{""type"":""string""},
            ""title_block_id"":{""type"":""integer""},
            ""target"":{""type"":""string"",""enum"":[""instance"",""type"",""both""]},
            ""include_read_only"":{""type"":""boolean""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var target = p.Value<string>("target") ?? "both";
            var includeReadOnly = p.Value<bool?>("include_read_only") ?? true;
            if (!TryResolve(doc, p, out var sheet, out var titleBlock, out var failure)) return failure;
            var type = doc.GetElement(titleBlock.GetTypeId()) as FamilySymbol;

            JArray Collect(Element element) => new JArray(element.Parameters.Cast<Parameter>()
                .Where(x => x?.Definition != null && (includeReadOnly || !x.IsReadOnly))
                .OrderBy(x => x.Definition.Name, StringComparer.OrdinalIgnoreCase)
                .Select(SheetsHelpers.ParameterDto));

            return CommandResult.Ok(new JObject
            {
                ["sheet_id"] = RevitCompat.GetId(sheet.Id),
                ["sheet_number"] = sheet.SheetNumber,
                ["title_block_instance_id"] = RevitCompat.GetId(titleBlock.Id),
                ["title_block_type_id"] = RevitCompat.GetIdOrNull(type?.Id),
                ["title_block_name"] = type == null ? null : type.FamilyName + ": " + type.Name,
                ["instance_parameters"] = target != "type" ? Collect(titleBlock) : new JArray(),
                ["type_parameters"] = target != "instance" && type != null ? Collect(type) : new JArray()
            });
        }

        /// <summary>The sheet and its one title block (or the one named by title_block_id). Shared with set_titleblock_parameters.</summary>
        internal static bool TryResolve(Document doc, JObject p, out ViewSheet sheet, out FamilyInstance titleBlock, out CommandResult failure)
        {
            titleBlock = null;
            var sheetId = p.Value<long?>("sheet_id");
            var sheetNumber = p.Value<string>("sheet_number");
            if (sheetId.HasValue && !string.IsNullOrEmpty(sheetNumber))
            {
                sheet = null;
                failure = CommandResult.Fail("INVALID_PARAMS: pass sheet_id or sheet_number, not both.");
                return false;
            }
            if (!Lookup.TrySheet(doc, sheetId, sheetNumber, out sheet, out failure)) return false;

            var all = SheetsHelpers.TitleBlocksOn(doc, sheet);
            var options = new JObject
            {
                ["valid_title_blocks"] = new JArray(all.Select(t => new JObject
                {
                    ["id"] = RevitCompat.GetId(t.Id),
                    ["name"] = t.Symbol == null ? null : t.Symbol.FamilyName + ": " + t.Symbol.Name
                }))
            };
            var wanted = p.Value<long?>("title_block_id");
            if (wanted.HasValue)
            {
                titleBlock = all.FirstOrDefault(t => RevitCompat.GetId(t.Id) == wanted.Value);
                if (titleBlock == null)
                    failure = CommandResult.Fail("TITLEBLOCK_NOT_FOUND: title block " + wanted.Value + " is not on sheet " + sheet.SheetNumber + ".", options);
            }
            else if (all.Count == 1)
            {
                titleBlock = all[0];
            }
            else
            {
                failure = all.Count == 0
                    ? CommandResult.Fail("NO_TITLEBLOCK: sheet " + sheet.SheetNumber + " has no title block.")
                    : CommandResult.Fail("TITLEBLOCK_AMBIGUOUS: sheet " + sheet.SheetNumber + " has " + all.Count + " title blocks; pass title_block_id.", options);
            }
            return titleBlock != null;
        }
    }
}
