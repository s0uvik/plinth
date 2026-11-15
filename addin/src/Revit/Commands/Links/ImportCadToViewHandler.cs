// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Links/ImportCadToViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: view_id is required (rvt-mcp fell back to the active view) and must not be a template;
// the path must be absolute (drive or UNC) and exist; only that file is read; transaction named "B2M: ...",
// rolled back only while open; unit gains centimeter/decimeter; the result reads the created instance back.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Links
{
    using View = Autodesk.Revit.DB.View;

    public sealed class ImportCadToViewHandler : IRevitCommand
    {
        public string Name => "import_cad_to_view";
        public string Description =>
            "Import (default) or link (link: true) one DWG/DXF file into the view view_id. path must be an absolute path to an existing .dwg/.dxf; "
            + "only that file is read. placement: origin (default), center or shared. unit: default (from the file), millimeter, centimeter, "
            + "decimeter, meter, inch or foot. this_view_only (default true) cannot be used with a 3D view. visible_layers_only defaults to true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""path"":{""type"":""string""},
            ""view_id"":{""type"":""integer""},
            ""link"":{""type"":""boolean""},
            ""placement"":{""type"":""string"",""enum"":[""origin"",""center"",""shared""]},
            ""unit"":{""type"":""string"",""enum"":[""default"",""millimeter"",""centimeter"",""decimeter"",""meter"",""inch"",""foot""]},
            ""this_view_only"":{""type"":""boolean""},
            ""visible_layers_only"":{""type"":""boolean""}},
            ""required"":[""path"",""view_id""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: CAD import here needs a project document.");

            if (!LinksHelpers.TryInputFile(p.Value<string>("path"), new[] { ".dwg", ".dxf" }, out var path, out var failure)) return failure;
            if (!Lookup.TryView(doc, p.Value<long>("view_id"), out var view, out failure)) return failure;

            var link = p.Value<bool?>("link") ?? false;
            var placement = p.Value<string>("placement") ?? "origin";
            var unit = p.Value<string>("unit") ?? "default";
            var thisViewOnly = p.Value<bool?>("this_view_only") ?? true;
            var visibleLayersOnly = p.Value<bool?>("visible_layers_only") ?? true;

            if (thisViewOnly && view.ViewType == ViewType.ThreeD)
                return CommandResult.Fail("INVALID_VIEW: this_view_only cannot be used with 3D view '" + view.Name + "'; pass this_view_only: false or a 2D view.");
            if (view is ViewSheet || view is ViewSchedule)
                return CommandResult.Fail("INVALID_VIEW: view '" + view.Name + "' (" + view.ViewType + ") cannot hold CAD; use a plan, section, elevation, drafting or 3D view.");

            var options = new DWGImportOptions
            {
                ThisViewOnly = thisViewOnly,
                VisibleLayersOnly = visibleLayersOnly,
                Placement = placement == "center" ? ImportPlacement.Centered : placement == "shared" ? ImportPlacement.Shared : ImportPlacement.Origin,
                Unit = ToUnit(unit)
            };

            ElementId createdId;
            using (var tx = new Transaction(doc, link ? "B2M: Link CAD" : "B2M: Import CAD"))
            {
                tx.Start();
                try
                {
                    var ok = link ? doc.Link(path, options, view, out createdId) : doc.Import(path, options, view, out createdId);
                    if (!ok || createdId == null || createdId == ElementId.InvalidElementId)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("IMPORT_FAILED: Revit did not " + (link ? "link" : "import") + " " + path + ". Nothing was added.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the CAD " + (link ? "link" : "import") + " back; see failures. Nothing was added.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("IMPORT_FAILED: could not " + (link ? "link" : "import") + " " + path + ": " + ex.Message);
                }
            }

            var instance = doc.GetElement(createdId) as ImportInstance;
            Transform transform = null;
            try
            {
                transform = instance?.GetTransform();
            }
            catch
            {
                // Reported as null.
            }
            return CommandResult.Ok(new JObject
            {
                ["instance_id"] = RevitCompat.GetId(createdId),
                ["type_id"] = instance == null ? null : (JToken)RevitCompat.GetId(instance.GetTypeId()),
                ["kind"] = instance == null ? null : instance.IsLinked ? "cad_link" : "cad_import",
                ["path"] = path,
                ["view"] = new JObject
                {
                    ["id"] = RevitCompat.GetId(view.Id),
                    ["name"] = view.Name,
                    ["view_type"] = view.ViewType.ToString()
                },
                ["placement"] = placement,
                ["unit"] = unit,
                ["this_view_only"] = thisViewOnly,
                ["visible_layers_only"] = visibleLayersOnly,
                ["origin_mm"] = transform == null ? null : LinksHelpers.Xyz(transform.Origin)
            });
        }

        private static ImportUnit ToUnit(string unit)
        {
            switch (unit)
            {
                case "millimeter": return ImportUnit.Millimeter;
                case "centimeter": return ImportUnit.Centimeter;
                case "decimeter": return ImportUnit.Decimeter;
                case "meter": return ImportUnit.Meter;
                case "inch": return ImportUnit.Inch;
                case "foot": return ImportUnit.Foot;
                default: return ImportUnit.Default;
            }
        }
    }
}
