// New in Plinth (Step 10, fit-out families first): measure_family_types. The fit-out lays furniture out with the real
// footprint of the family chosen for each item and inserts each family from its box, so it needs every type's box
// about its insertion point. One instance per type is placed unrotated at the origin of a level (level-based: at the
// level; work-plane-based: on the level's plane), its bounding box read, and everything is rolled back: from the
// user's point of view nothing changes (one transaction, always rolled back; one sub-transaction per type).
// Boxes are relative to the point asked for (0, 0, the level's elevation). base_offset_mm makes a Revit quirk
// explicit: some level-based families take the point's z as their offset above the level, so they stand the level's
// elevation higher; their own box is the box less base_offset_mm.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    public sealed class MeasureFamilyTypesHandler : IRevitCommand
    {
        private const int MaxTypes = 200;

        public string Name => "measure_family_types";
        public string Description =>
            "Measure family types: one unrotated instance per type at a level's origin (level-based at the level, work-plane-based "
            + "on the level's plane), its bounding box read relative to that point (mm), then everything rolled back (the model is "
            + "not changed). Per type: type_id, family, type, placement, min, max, base_offset_mm (how far above the point Revit put "
            + "the family's origin: a family that takes the point's z as its offset above the level shows the level's elevation), "
            + "facing and hand (unit vectors). Hosted and other placement types are listed in skipped with the reason. "
            + "level: the level's name (default: the lowest level above elevation 0).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""type_ids"":{""type"":""array"",""items"":{""type"":""integer""},""minItems"":1,""maxItems"":200},
            ""level"":{""type"":""string""}},""required"":[""type_ids""]}";

        // Read-only from the user's point of view: its one transaction is always rolled back.
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            var ids = (p["type_ids"] as JArray ?? new JArray()).Select(t => t.Value<long>()).Distinct().ToList();
            if (ids.Count == 0 || ids.Count > MaxTypes)
                return CommandResult.Fail("INVALID_PARAMS: give 1 to " + MaxTypes + " type_ids.");

            Level level;
            var levelName = p.Value<string>("level");
            if (!string.IsNullOrEmpty(levelName))
            {
                if (!Lookup.TryLevel(doc, levelName, out level, out var failure)) return failure;
            }
            else
            {
                var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                var name = FamilyMeasure.DefaultLevel(levels.Select(l => new KeyValuePair<string, double>(l.Name, Mm.FromFeet(l.ProjectElevation))));
                level = levels.FirstOrDefault(l => l.Name == name);
                if (level == null) return CommandResult.Fail("NO_LEVEL: the document has no level to measure on.");
            }
            if (doc.IsReadOnly) return CommandResult.Fail("READ_ONLY_DOCUMENT: the document cannot be changed, not even for a moment.");
            if (doc.IsModifiable) return CommandResult.Fail("TRANSACTION_OPEN: another transaction is open; call measure_family_types on its own.");

            var elevationMm = Mm.FromFeet(level.ProjectElevation);
            var origin = new XYZ(0, 0, level.ProjectElevation);
            var originMm = new[] { 0.0, 0.0, elevationMm };
            var types = new JArray();
            var skipped = new JArray();

            using (var tx = new Transaction(doc, "Plinth: Measure family types (rolled back)"))
            {
                tx.Start();
                try
                {
                    foreach (var id in ids)
                    {
                        var symbol = doc.GetElement(RevitCompat.ToElementId(id)) as FamilySymbol;
                        if (symbol == null)
                        {
                            skipped.Add(Skip(id, "not a family type in this document"));
                            continue;
                        }
                        var placement = symbol.Family.FamilyPlacementType.ToString();
                        var why = FamilyMeasure.NotMeasuredReason(placement);
                        if (why != null)
                        {
                            skipped.Add(Skip(id, why, symbol));
                            continue;
                        }
                        using (var sub = new SubTransaction(doc))
                        {
                            sub.Start();
                            try
                            {
                                if (!symbol.IsActive)
                                {
                                    symbol.Activate();
                                    doc.Regenerate();
                                }
                                var instance = placement == "WorkPlaneBased"
                                    ? doc.Create.NewFamilyInstance(level.GetPlaneReference()
                                        ?? throw new InvalidOperationException("Revit gives no plane reference for this level."), origin, XYZ.BasisX, symbol)
                                    : doc.Create.NewFamilyInstance(origin, symbol, level, StructuralType.NonStructural);
                                if (instance == null) throw new InvalidOperationException("Revit returned no instance.");
                                doc.Regenerate();
                                var box = instance.get_BoundingBox(null) ?? throw new InvalidOperationException("the instance has no geometry to measure.");
                                var location = (instance.Location as LocationPoint)?.Point;
                                var offset = OffsetParameter(instance);
                                var baseOffset = placement == "OneLevelBased"
                                    ? FamilyMeasure.BaseOffsetMm(0, offset.HasValue ? Mm.FromFeet(offset.Value) : (double?)null,
                                        location != null ? Mm.FromFeet(location.Z - level.ProjectElevation) : (double?)null, elevationMm)
                                    : 0;
                                types.Add(new JObject
                                {
                                    ["type_id"] = id,
                                    ["family"] = symbol.FamilyName,
                                    ["type"] = symbol.Name,
                                    ["category"] = symbol.Category?.Name,
                                    ["built_in_category"] = symbol.Category != null ? Lookup.BuiltInName(symbol.Category) : null,
                                    ["placement"] = placement,
                                    ["min"] = new JArray(FamilyMeasure.Relative(FamiliesHelpers.PointMm(box.Min), originMm)),
                                    ["max"] = new JArray(FamilyMeasure.Relative(FamiliesHelpers.PointMm(box.Max), originMm)),
                                    ["base_offset_mm"] = baseOffset,
                                    ["location"] = location != null ? new JArray(FamilyMeasure.Relative(FamiliesHelpers.PointMm(location), originMm)) : null,
                                    ["facing"] = Unit(instance.FacingOrientation),
                                    ["hand"] = Unit(instance.HandOrientation),
                                    ["level"] = level.Name
                                });
                            }
                            catch (Exception ex)
                            {
                                skipped.Add(Skip(id, (placement == "WorkPlaneBased"
                                    ? "could not be placed on the level's plane (a face-based family needs a face): "
                                    : "could not be placed at the level: ") + ex.Message, symbol));
                            }
                            finally
                            {
                                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                            }
                        }
                    }
                }
                finally
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["level"] = level.Name,
                ["level_elevation_mm"] = elevationMm,
                ["measured"] = types.Count,
                ["types"] = types,
                ["skipped"] = skipped,
                ["rolled_back"] = true
            });
        }

        /// <summary>The instance's offset above its level (feet), when it has one.</summary>
        internal static double? OffsetParameter(FamilyInstance instance)
        {
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
            {
                var parameter = instance.get_Parameter(bip);
                if (parameter != null && parameter.StorageType == StorageType.Double && parameter.HasValue) return parameter.AsDouble();
            }
            return null;
        }

        private static JArray Unit(XYZ v) =>
            v == null ? null : new JArray(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6));

        private static JObject Skip(long id, string reason, FamilySymbol symbol = null)
        {
            var row = new JObject { ["type_id"] = id, ["reason"] = reason };
            if (symbol != null)
            {
                row["family"] = symbol.FamilyName;
                row["type"] = symbol.Name;
            }
            return row;
        }
    }
}
