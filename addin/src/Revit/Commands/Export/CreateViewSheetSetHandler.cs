// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Sheets/CreateViewSheetSetHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a name already used by a set fails with NAME_IN_USE before anything changes; views are
// checked with the shared lookup (templates and non-printable views refused, duplicates refused) and any bad
// id fails the call (rvt-mcp returned success with created:false); the set id is read back by name after
// commit; the rollback runs only while the transaction is still open.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Export
{
    using View = Autodesk.Revit.DB.View;

    public sealed class CreateViewSheetSetHandler : IRevitCommand
    {
        public string Name => "create_view_sheet_set";
        public string Description =>
            "Save a named view/sheet set (for printing and batch export) holding view_ids. Fails if the name is taken. "
            + "Also leaves the print manager's range set to 'selected views'.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""view_ids"":{""type"":""array"",""items"":{""type"":""integer""}}},
            ""required"":[""name"",""view_ids""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var name = (p.Value<string>("name") ?? "").Trim();
            if (name.Length == 0) return CommandResult.Fail("INVALID_PARAMS: name must not be empty.");
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return CommandResult.Fail("NAME_IN_USE: a view/sheet set named '" + existing.Name + "' already exists.",
                    new { existing_set_id = RevitCompat.GetId(existing.Id) });

            var ids = (JArray)p["view_ids"];
            if (ids.Count == 0) return CommandResult.Fail("INVALID_PARAMS: view_ids must not be empty.");
            if (ids.Count > ExportHelpers.MaxViews) return CommandResult.Fail("INVALID_PARAMS: at most " + ExportHelpers.MaxViews + " views per set.");
            var views = new List<View>();
            foreach (var token in ids)
            {
                if (!Lookup.TryView(doc, token.Value<long>(), out var view, out var failure)) return failure;
                if (views.Any(v => v.Id == view.Id)) return CommandResult.Fail("INVALID_PARAMS: view " + token + " is listed twice.");
                if (!view.CanBePrinted) return CommandResult.Fail("NOT_PRINTABLE: view '" + view.Name + "' (" + view.ViewType + ") cannot be printed, so it cannot be in a set.");
                views.Add(view);
            }

            using (var tx = new Transaction(doc, "Plinth: Create view/sheet set"))
            {
                tx.Start();
                try
                {
                    var pm = doc.PrintManager;
                    pm.PrintRange = PrintRange.Select;
                    var setting = pm.ViewSheetSetting;
                    var set = new ViewSet();
                    foreach (var view in views) set.Insert(view);
                    setting.CurrentViewSheetSet.Views = set;
                    setting.SaveAs(name);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. No set was created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the view/sheet set: " + ex.Message);
                }
            }

            var created = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            return CommandResult.Ok(new
            {
                set_id = created == null ? (long?)null : RevitCompat.GetId(created.Id),
                name,
                view_count = views.Count,
                views = views.Select(v => new { id = RevitCompat.GetId(v.Id), name = v.Name, sheet_number = (v as ViewSheet)?.SheetNumber })
            });
        }
    }
}
