// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Graphics/SetCategoryVisibilityHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: all or nothing (rvt-mcp hid what it could and listed the rest): every name must resolve and
// be hideable in the view before the transaction opens, else the error lists the categories that are; names may
// be built-in names such as OST_Walls (language independent); at most 500 names; failures are errors with codes.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Graphics
{
    public sealed class SetCategoryVisibilityHandler : IRevitCommand
    {
        public string Name => "set_category_visibility";
        public string Description => "Hide (hidden: true) or show (hidden: false) categories in a view. Categories are built-in names such as "
            + "OST_Walls or the names shown in Revit. view_id defaults to the active view.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""categories"":{""type"":""array"",""items"":{""type"":""string""}},
            ""hidden"":{""type"":""boolean""},
            ""view_id"":{""type"":""integer""}},""required"":[""categories"",""hidden""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var names = p["categories"].Select(t => t.Value<string>()).ToList();
            if (names.Count > 500) return CommandResult.Fail("INVALID_PARAMS: at most 500 categories per call.");
            var hidden = p.Value<bool>("hidden");
            if (!GraphicsHelpers.TryView(doc, p.Value<long?>("view_id"), out var view, out var failure)) return failure;

            Func<Category, bool> hideable = c =>
            {
                try
                {
                    return view.CanCategoryBeHidden(c.Id);
                }
                catch
                {
                    return false;
                }
            };
            var options = doc.Settings.Categories.Cast<Category>().Where(c => c != null && hideable(c)).ToList();
            if (!GraphicsHelpers.TryCategories(doc, names, hideable, "cannot be hidden or shown in view '" + view.Name + "'",
                    options, out var categories, out failure)) return failure;

            using (var tx = new Transaction(doc, hidden ? "Plinth: Hide Categories" : "Plinth: Show Categories"))
            {
                tx.Start();
                try
                {
                    foreach (var c in categories) view.SetCategoryHidden(c.Id, hidden);
                    if (tx.Commit() != TransactionStatus.Committed) return GraphicsHelpers.NotCommitted();
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("VISIBILITY_FAILED: could not change category visibility in view '" + view.Name + "'; nothing changed: " + ex.Message);
                }
            }

            var result = GraphicsHelpers.ViewRef(view);
            result["hidden"] = hidden;
            result["categories"] = new JArray(categories.Select(c =>
            {
                var row = GraphicsHelpers.CategoryRef(c);
                row["hidden_now"] = view.GetCategoryHidden(c.Id);
                return row;
            }));
            return CommandResult.Ok(result);
        }
    }
}
