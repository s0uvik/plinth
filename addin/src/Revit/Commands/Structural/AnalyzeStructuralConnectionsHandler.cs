// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/AnalyzeStructuralConnectionsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: unknown element_ids are reported (rvt-mcp dropped them silently); total and truncated are reported;
// framing members also report whether joins are allowed at each end (StructuralFramingUtils), besides geometry
// joins; errors are "CODE: message".

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Structural
{
    public sealed class AnalyzeStructuralConnectionsHandler : IRevitCommand
    {
        public string Name => "analyze_structural_connections";
        public string Description =>
            "Audit joins of structural framing and columns: per element its geometry-joined neighbours and, for framing, whether joins are allowed at each "
            + "end. Optional element_ids (default every framing member and column); limit (default and max 500).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""element_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var limit = p.Value<int?>("limit") ?? 500;
            if (limit < 1 || limit > 500) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 500.");

            List<Element> elements;
            var missing = new List<long>();
            var ids = p["element_ids"] as JArray;
            if (ids != null && ids.Count > 0)
            {
                elements = new List<Element>();
                foreach (var id in ids.Values<long>().Distinct())
                {
                    var element = doc.GetElement(RevitCompat.ToElementId(id));
                    if (element == null) missing.Add(id);
                    else elements.Add(element);
                }
            }
            else
            {
                elements = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType().ToElements()
                    .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralColumns).WhereElementIsNotElementType().ToElements())
                    .ToList();
            }

            var rows = new JArray();
            foreach (var element in elements.Take(limit))
            {
                var joined = new List<long>();
                try
                {
                    joined = JoinGeometryUtils.GetJoinedElements(doc, element).Select(RevitCompat.GetId).ToList();
                }
                catch
                {
                    // Some categories cannot take part in geometry joins.
                }
                var row = new JObject
                {
                    ["id"] = RevitCompat.GetId(element.Id),
                    ["category"] = element.Category?.Name,
                    ["name"] = element.Name,
                    ["joined_count"] = joined.Count,
                    ["joined_with"] = new JArray(joined)
                };
                if (element is FamilyInstance framing && element.Category?.BuiltInCategory == BuiltInCategory.OST_StructuralFraming)
                {
                    row["start_join_allowed"] = EndAllowed(framing, 0);
                    row["end_join_allowed"] = EndAllowed(framing, 1);
                }
                rows.Add(row);
            }

            return CommandResult.Ok(new
            {
                count = rows.Count,
                total = elements.Count,
                truncated = elements.Count > rows.Count,
                missing_ids = missing,
                items = rows
            });
        }

        private static bool? EndAllowed(FamilyInstance framing, int end)
        {
            try { return StructuralFramingUtils.IsJoinAllowedAtEnd(framing, end); }
            catch { return null; }
        }
    }
}
