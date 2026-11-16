// New in B2M: a structural foundation slab (raft / plinth-level slab) from a closed outline, for the drawing-to-model
// build (Step 9). type_name must be a foundation slab FloorType (FloorType.IsFoundationSlab; make one with
// ensure_floor_type foundation: true). It uses Floor.Create (Revit 2022+ API) with that type and structural = true, so
// Revit makes a Structural Foundations element. The outline is checked first (PlanPolygon: 3+ points, simple, with area;
// then Revit's BoundaryValidation). Any Revit warning or error rolls the slab back (FailureScope), as does a result that
// is not a structural foundation.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Geometry;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreateFoundationSlabHandler : IRevitCommand
    {
        private const int MaxOptions = 100;
        private const string Unchanged = " Nothing was changed.";

        public string Name => "create_foundation_slab";
        public string Description =>
            "Create a structural foundation slab on a level from a closed outline of [x, y] points in mm (3 or more; do not repeat the first "
            + "point). type_name must be a foundation slab type (make one with ensure_floor_type foundation: true). height_offset_mm sets Height "
            + "Offset From Level. Any Revit warning rolls it back. Returns element_id, area_m2 and type_name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""level"":{""type"":""string""},
            ""points"":{""type"":""array"",""items"":{""type"":""array""},""description"":""closed polygon of [x, y] points in mm, 3 or more, first point not repeated""},
            ""type_name"":{""type"":""string"",""description"":""a foundation slab FloorType""},
            ""height_offset_mm"":{""type"":""number"",""description"":""Height Offset From Level in mm (default 0)""}},
            ""required"":[""level"",""points"",""type_name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: create_foundation_slab works on a project document, not a family.");

            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out var failure)) return failure;
            if (!TryFoundationType(doc, (p.Value<string>("type_name") ?? "").Trim(), out var type, out failure)) return failure;
            if (!PlanPoints.TryList(p["points"], "points", 3, true, level.ProjectElevation, app.Application.ShortCurveTolerance,
                    out var points, out failure)) return failure;
            var mm = ((JArray)p["points"]).Select(t => new[] { t[0].Value<double>(), t[1].Value<double>() }).ToList();
            var outlineError = PlanPolygon.Validate(mm, "points");
            if (outlineError != null) return CommandResult.Fail(outlineError);

            var heightOffsetMm = p.Value<double?>("height_offset_mm");
            if (heightOffsetMm.HasValue && !PlanPoints.IsFinite(heightOffsetMm.Value))
                return CommandResult.Fail("INVALID_PARAMS: height_offset_mm must be a finite number of mm.");

            var loop = new CurveLoop();
            for (var i = 0; i < points.Count; i++) loop.Append(Line.CreateBound(points[i], points[(i + 1) % points.Count]));
            var loops = new List<CurveLoop> { loop };
            if (!BoundaryValidation.IsValidHorizontalBoundary(loops))
                return CommandResult.Fail("INVALID_PARAMS: Revit does not accept points as a horizontal boundary (it must be one closed, "
                    + "non-self-intersecting loop).");

            using (var tx = new Transaction(doc, "B2M: Create foundation slab"))
            {
                tx.Start();
                try
                {
                    var slab = Floor.Create(doc, loops, type.Id, level.Id, true, null, 0.0);
                    if (slab == null) return RolledBack(tx, "CREATE_FAILED: Revit did not create the foundation slab.");
                    if (slab.Category == null || slab.Category.BuiltInCategory != BuiltInCategory.OST_StructuralFoundation)
                        return RolledBack(tx, "CREATE_FAILED: Revit made a " + (slab.Category?.Name ?? "element of no category") + ", not a structural "
                            + "foundation, from type '" + type.Name + "'.");
                    if (heightOffsetMm.HasValue)
                    {
                        var offset = slab.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                        if (offset == null || offset.IsReadOnly)
                            return RolledBack(tx, "CREATE_FAILED: the slab's Height Offset From Level cannot be set.");
                        offset.Set(Mm.ToFeet(heightOffsetMm.Value));
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the foundation slab back (a warning or error); see failures." + Unchanged);

                    var area = slab.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble();
                    var areaM2 = area.HasValue && area.Value > 0 ? area.Value * 0.09290304 : PlanPolygon.AreaMm2(mm) / 1e6;
                    var heightOffset = slab.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.AsDouble();
                    return CommandResult.Ok(new JObject
                    {
                        ["element_id"] = RevitCompat.GetId(slab.Id),
                        ["area_m2"] = Math.Round(areaM2, 3),
                        ["type_name"] = type.Name,
                        ["type_id"] = RevitCompat.GetId(type.Id),
                        ["level"] = level.Name,
                        ["height_offset_mm"] = heightOffset.HasValue ? (JToken)Mm.FromFeet(heightOffset.Value) : null,
                        ["thickness_mm"] = Thickness(type)
                    });
                }
                catch (Exception ex)
                {
                    return RolledBack(tx, "CREATE_FAILED: could not create the foundation slab: " + ex.Message);
                }
            }
        }

        /// <summary>The foundation slab FloorType of that name; a floor type of that name, or none, is an error with the options.</summary>
        private static bool TryFoundationType(Document doc, string name, out FloorType type, out CommandResult failure)
        {
            type = null;
            failure = null;
            var all = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().ToList();
            var foundations = all.Where(t => t.IsFoundationSlab).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var options = new JObject
            {
                ["valid_types"] = new JArray(foundations.Take(MaxOptions).Select(t => new JObject
                {
                    ["type_name"] = t.Name,
                    ["type_id"] = RevitCompat.GetId(t.Id),
                    ["thickness_mm"] = Thickness(t)
                }))
            };
            var matches = all.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            type = matches.FirstOrDefault(t => t.IsFoundationSlab && t.Name == name) ?? matches.FirstOrDefault(t => t.IsFoundationSlab);
            if (type != null) return true;
            if (name.Length == 0)
                failure = CommandResult.Fail("INVALID_PARAMS: type_name must name a foundation slab type.", options);
            else if (matches.Count > 0)
                failure = CommandResult.Fail("TYPE_NOT_FOUNDATION: '" + matches[0].Name + "' is a floor type, not a foundation slab type. Pass one of "
                    + "valid_types, or make one with ensure_floor_type foundation: true.", options);
            else
                failure = CommandResult.Fail("TYPE_NOT_FOUND: no foundation slab type is named '" + name + "'. Pass one of valid_types, or make one "
                    + "with ensure_floor_type foundation: true.", options);
            return false;
        }

        private static JToken Thickness(FloorType type)
        {
            try
            {
                var cs = type.GetCompoundStructure();
                return cs == null ? null : (JToken)Mm.FromFeet(cs.GetWidth());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        private static CommandResult RolledBack(Transaction tx, string error)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            return CommandResult.Fail(error + Unchanged);
        }
    }
}
