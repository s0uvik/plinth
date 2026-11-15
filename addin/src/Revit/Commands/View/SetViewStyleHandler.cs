// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/SetViewStyleHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: view lookup through ViewHelpers (a name shared by several views is an error with the
// options; rvt-mcp took the first); the schema enums replace the hand-written value checks; B2M error codes.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.View
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class SetViewStyleHandler : IRevitCommand
    {
        private static readonly Dictionary<string, DisplayStyle> Styles = new Dictionary<string, DisplayStyle>
        {
            ["wireframe"] = DisplayStyle.Wireframe,
            ["hidden_line"] = DisplayStyle.HLR,
            ["shaded"] = DisplayStyle.Shading,
            ["shaded_edges"] = DisplayStyle.ShadingWithEdges,
            ["consistent_colors"] = DisplayStyle.FlatColors,
            ["realistic"] = DisplayStyle.Realistic,
        };

        private static readonly Dictionary<string, ViewDetailLevel> DetailLevels = new Dictionary<string, ViewDetailLevel>
        {
            ["coarse"] = ViewDetailLevel.Coarse,
            ["medium"] = ViewDetailLevel.Medium,
            ["fine"] = ViewDetailLevel.Fine,
        };

        public string Name => "set_view_style";
        public string Description =>
            "Set a view's display style and/or detail level (view_id or view_name, default the active view).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""view_name"":{""type"":""string""},
            ""display_style"":{""type"":""string"",""enum"":[""wireframe"",""hidden_line"",""shaded"",""shaded_edges"",""consistent_colors"",""realistic""]},
            ""detail_level"":{""type"":""string"",""enum"":[""coarse"",""medium"",""fine""]}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var styleName = p.Value<string>("display_style");
            var detailName = p.Value<string>("detail_level");
            if (styleName == null && detailName == null)
                return CommandResult.Fail("INVALID_PARAMS: pass display_style, detail_level or both.");
            if (!ViewHelpers.TryResolveView(uidoc, p.Value<long?>("view_id"), p.Value<string>("view_name"), false, out var view, out var failure))
                return failure;

            using (var tx = new Transaction(doc, "B2M: Set View Style"))
            {
                tx.Start();
                try
                {
                    if (styleName != null) view.DisplayStyle = Styles[styleName];
                    if (detailName != null) view.DetailLevel = DetailLevels[detailName];
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    var hint = view.ViewTemplateId != ElementId.InvalidElementId ? " The view's template may control these settings." : "";
                    return CommandResult.Fail("STYLE_FAILED: could not set the style of '" + view.Name + "' (" + view.ViewType + "): "
                        + ex.Message + hint + " Nothing was changed.");
                }
            }

            return CommandResult.Ok(new
            {
                view_id = RevitCompat.GetId(view.Id),
                view_name = view.Name,
                display_style = NameOf(Styles, view.DisplayStyle),
                detail_level = NameOf(DetailLevels, view.DetailLevel)
            });
        }

        // Report values in the words the command accepts, so a caller can pass them straight back.
        private static string NameOf<T>(Dictionary<string, T> names, T value) =>
            names.FirstOrDefault(pair => EqualityComparer<T>.Default.Equals(pair.Value, value)).Key ?? value.ToString();
    }
}
