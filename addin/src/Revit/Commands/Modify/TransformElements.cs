// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Handlers/Elements/TransformElementsHandlers.cs and PlacementInput.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: FailureControl.BeginBatch / NotifyBatchRolledBack are not ported (FailureScope rolls back on any
// Revit warning or error); the TransactionGroup and the before/after placement check are kept, so a move or rotation
// that Revit constrains (hosted, joined, level-bound elements) rolls back as a whole; mm through Mm; missing,
// pinned, type and group-member IDs are listed in the error data.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Modify
{
    public sealed class MoveElementsHandler : IRevitCommand
    {
        public string Name => "move_elements";
        public string Description => "Move elements by dx_mm, dy_mm, dz_mm in model coordinates, all or nothing (rolled back if Revit constrains any).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""dx_mm"":{""type"":""number""},""dy_mm"":{""type"":""number""},""dz_mm"":{""type"":""number""}},
            ""required"":[""elementIds"",""dx_mm"",""dy_mm"",""dz_mm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p) => Placement.Run(app, p, false);
    }

    public sealed class RotateElementsHandler : IRevitCommand
    {
        public string Name => "rotate_elements";
        public string Description => "Rotate elements by angle_degrees (counterclockwise) about a vertical axis through pivot_x_mm, pivot_y_mm, "
            + "or with pivot_mode each_location each about its own location. All or nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""angle_degrees"":{""type"":""number""},
            ""pivot_x_mm"":{""type"":""number""},""pivot_y_mm"":{""type"":""number""},""pivot_z_mm"":{""type"":""number""},
            ""pivot_mode"":{""type"":""string"",""enum"":[""point"",""each_location""]}},
            ""required"":[""elementIds"",""angle_degrees""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p) => Placement.Run(app, p, true);
    }

    internal static class Placement
    {
        private const int MaxIds = 1000;
        private static readonly double PositionTolerance = Mm.ToFeet(1.0);
        private const double AngleTolerance = 1e-6;

        public static CommandResult Run(UIApplication app, JObject p, bool rotate)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!ModifyHelpers.TryIds(p, "elementIds", MaxIds, out var rawIds, out var failure)) return failure;

            var ids = rawIds.Select(RevitCompat.ToElementId).ToList();
            var elements = ids.Select(doc.GetElement).ToList();
            var missing = rawIds.Where((id, i) => elements[i] == null).ToList();
            if (missing.Count > 0)
                return CommandResult.Fail("ELEMENT_NOT_FOUND: some elementIds do not exist. Nothing was changed.", new { missingIds = missing });
            var refused = elements.Where(e => e is ElementType || e.Pinned || e.GroupId != ElementId.InvalidElementId)
                .Select(e => RevitCompat.GetId(e.Id)).ToList();
            if (refused.Count > 0)
                return CommandResult.Fail("ELEMENT_REFUSED: types, pinned elements and members of groups cannot be moved this way. Nothing was changed.",
                    new { refusedIds = refused });

            var eachLocation = rotate && p.Value<string>("pivot_mode") == "each_location";
            var hasPivot = p["pivot_x_mm"] != null || p["pivot_y_mm"] != null || p["pivot_z_mm"] != null;
            if (eachLocation && hasPivot)
                return CommandResult.Fail("INVALID_PARAMS: pivot_mode each_location turns each element about its own location; omit pivot_x_mm, pivot_y_mm and pivot_z_mm.");
            if (rotate && !eachLocation && (p["pivot_x_mm"] == null || p["pivot_y_mm"] == null))
                return CommandResult.Fail("INVALID_PARAMS: give pivot_x_mm and pivot_y_mm, or pivot_mode each_location.");

            var vector = rotate
                ? new XYZ(Mm.ToFeet(p.Value<double?>("pivot_x_mm") ?? 0), Mm.ToFeet(p.Value<double?>("pivot_y_mm") ?? 0), Mm.ToFeet(p.Value<double?>("pivot_z_mm") ?? 0))
                : new XYZ(Mm.ToFeet(p.Value<double>("dx_mm")), Mm.ToFeet(p.Value<double>("dy_mm")), Mm.ToFeet(p.Value<double>("dz_mm")));
            var degrees = rotate ? p.Value<double>("angle_degrees") : 0;
            if (new[] { vector.X, vector.Y, vector.Z, degrees }.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                return CommandResult.Fail("INVALID_PARAMS: coordinates and angles must be finite numbers.");
            var radians = degrees % 360.0 * Math.PI / 180.0;

            var before = elements.Select(Snapshot).ToArray();
            if (before.Any(s => s.Point == null))
                return CommandResult.Fail("UNSUPPORTED_ELEMENT: only elements with a point or bounded curve location can be moved this way. Nothing was changed.",
                    new { unsupportedIds = before.Where(s => s.Point == null).Select(s => s.Id).ToList() });
            var pivots = before.Select(s => !eachLocation ? vector : s.End == null ? s.Point : (s.Point + s.End) / 2).ToArray();
            var transforms = pivots.Select(pivot => rotate ? Transform.CreateRotationAtPoint(XYZ.BasisZ, radians, pivot) : Transform.CreateTranslation(vector)).ToArray();

            State[] after;
            var title = rotate ? "B2M: Rotate elements" : "B2M: Move elements";
            using (var group = new TransactionGroup(doc, title))
            {
                group.Start();
                try
                {
                    using (var tx = new Transaction(doc, title))
                    {
                        tx.Start();
                        try
                        {
                            if (!rotate) ElementTransformUtils.MoveElements(doc, ids, vector);
                            else if (!eachLocation) ElementTransformUtils.RotateElements(doc, ids, Line.CreateBound(vector, vector + XYZ.BasisZ), radians);
                            else
                                for (var i = 0; i < ids.Count; i++)
                                    ElementTransformUtils.RotateElement(doc, ids[i], Line.CreateBound(pivots[i], pivots[i] + XYZ.BasisZ), radians);
                            doc.Regenerate();
                            var problem = Check(doc, ids, before, transforms, rotate ? radians : 0, out _);
                            if (problem != null)
                            {
                                tx.RollBack();
                                group.RollBack();
                                return CommandResult.Fail(problem);
                            }
                            if (tx.Commit() != TransactionStatus.Committed)
                            {
                                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                                return CommandResult.Fail("NOT_COMMITTED: Revit rolled the change back; see failures. Nothing was changed.");
                            }
                        }
                        catch
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                            throw;
                        }
                    }
                    // Commit can apply joins and other finalisation; check the committed state while the group can still undo it.
                    var committedProblem = Check(doc, ids, before, transforms, rotate ? radians : 0, out after);
                    if (committedProblem != null)
                    {
                        group.RollBack();
                        return CommandResult.Fail(committedProblem);
                    }
                    if (group.Assimilate() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit did not finalise the change. Nothing was changed.");
                }
                catch (Exception ex)
                {
                    if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                    return CommandResult.Fail("MODIFY_FAILED: " + ex.Message + " Nothing was changed.");
                }
            }
            return CommandResult.Ok(new JObject
            {
                ["operation"] = rotate ? "rotate" : "move",
                ["count"] = ids.Count,
                ["angle_degrees"] = rotate ? new JValue(degrees) : JValue.CreateNull(),
                ["pivot_mode"] = rotate ? (eachLocation ? "each_location" : "point") : null,
                ["pivot_mm"] = rotate && !eachLocation ? ModifyHelpers.Point(vector) : null,
                ["before"] = new JArray(before.Select(s => s.Dto)),
                ["after"] = new JArray(after.Select(s => s.Dto))
            });
        }

        private static string Check(Document doc, List<ElementId> ids, State[] before, Transform[] transforms, double angle, out State[] after)
        {
            after = ids.Select(id => Snapshot(doc.GetElement(id))).ToArray();
            for (var i = 0; i < after.Length; i++)
            {
                var actual = after[i];
                var expected = before[i];
                var t = transforms[i];
                if (actual.Point == null || actual.Point.DistanceTo(t.OfPoint(expected.Point)) > PositionTolerance
                    || (expected.End != null && (actual.End == null || actual.End.DistanceTo(t.OfPoint(expected.End)) > PositionTolerance))
                    || (expected.Rotation.HasValue && (!actual.Rotation.HasValue
                        || Math.Abs(Math.IEEERemainder(actual.Rotation.Value - expected.Rotation.Value - angle, 2 * Math.PI)) > AngleTolerance)))
                    return "CONSTRAINED: Revit did not place element " + expected.Id
                        + " where asked (hosted, joined or level-bound elements can be held back). Nothing was changed.";
            }
            return null;
        }

        private sealed class State
        {
            public long Id;
            public XYZ Point;
            public XYZ End;
            public double? Rotation;
            public JObject Dto;
        }

        private static State Snapshot(Element element)
        {
            if (element == null) throw new InvalidOperationException("an element disappeared during the change");
            var pointLocation = element.Location as LocationPoint;
            var curve = (element.Location as LocationCurve)?.Curve;
            var point = pointLocation?.Point ?? (curve != null && curve.IsBound ? curve.GetEndPoint(0) : null);
            var end = curve != null && curve.IsBound ? curve.GetEndPoint(1) : null;
            double? rotation = null;
            if (pointLocation != null)
            {
                try
                {
                    rotation = pointLocation.Rotation;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    // Rooms and model groups have a location point without a rotation; only position is checked.
                }
            }
            var box = element.get_BoundingBox(null);
            return new State
            {
                Id = RevitCompat.GetId(element.Id),
                Point = point,
                End = end,
                Rotation = rotation,
                Dto = new JObject
                {
                    ["element_id"] = RevitCompat.GetId(element.Id),
                    ["location_mm"] = ModifyHelpers.Point(point),
                    ["curve_end_mm"] = ModifyHelpers.Point(end),
                    ["rotation_degrees"] = rotation.HasValue ? new JValue(Math.Round(rotation.Value * 180 / Math.PI, 6)) : JValue.CreateNull(),
                    ["bbox_min_mm"] = ModifyHelpers.Point(box?.Min),
                    ["bbox_max_mm"] = ModifyHelpers.Point(box?.Max)
                }
            };
        }
    }
}
