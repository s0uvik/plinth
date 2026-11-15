// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/CreateLineBasedElementHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD improvement for walls):
// - level is required; an unknown level or wall type is an error with the valid options
//   (rvt-mcp silently used the first level and ignored a wrong typeId);
// - typeName as well as typeId; the wall is created with its type in one call;
// - locationLine places the chosen reference line (finish face, core face...) on the given line;
// - baseOffset, topLevel and topOffset; zero-length lines are rejected with a clear message;
// - the response reads the wall back from Revit.
// - a curtain wall type makes a curtain wall; locationLine other than the centreline and structural are refused for it.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreateLineBasedElementHandler : IRevitCommand
    {
        private static readonly Dictionary<string, WallLocationLine> LocationLines = new Dictionary<string, WallLocationLine>(StringComparer.Ordinal)
        {
            ["wall_centerline"] = WallLocationLine.WallCenterline,
            ["core_centerline"] = WallLocationLine.CoreCenterline,
            ["finish_face_exterior"] = WallLocationLine.FinishFaceExterior,
            ["finish_face_interior"] = WallLocationLine.FinishFaceInterior,
            ["core_face_exterior"] = WallLocationLine.CoreExterior,
            ["core_face_interior"] = WallLocationLine.CoreInterior,
        };

        public string Name => "create_line_based_element";
        public string Description =>
            "Create a wall from a start and end point in mm on a named level. Optional: typeName or typeId, height (mm, default 3000) "
            + "or topLevel with topOffset, baseOffset, locationLine (which line of the wall lies on the given points), structural. "
            + "A curtain wall type makes a curtain wall (centreline only, not structural).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementType"":{""type"":""string"",""enum"":[""wall""]},
            ""startX"":{""type"":""number""},""startY"":{""type"":""number""},
            ""endX"":{""type"":""number""},""endY"":{""type"":""number""},
            ""level"":{""type"":""string""},
            ""typeId"":{""type"":""integer""},
            ""typeName"":{""type"":""string""},
            ""height"":{""type"":""number""},
            ""baseOffset"":{""type"":""number""},
            ""topLevel"":{""type"":""string""},
            ""topOffset"":{""type"":""number""},
            ""locationLine"":{""type"":""string"",""enum"":[""wall_centerline"",""core_centerline"",""finish_face_exterior"",""finish_face_interior"",""core_face_exterior"",""core_face_interior""]},
            ""structural"":{""type"":""boolean""}},
            ""required"":[""elementType"",""startX"",""startY"",""endX"",""endY"",""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out var failure)) return failure;
            if (!Lookup.TryElementType<WallType>(doc, p.Value<long?>("typeId"), p.Value<string>("typeName"),
                    ElementTypeGroup.WallType, "wall", out var wallType, out failure)) return failure;

            Level topLevel = null;
            var topLevelName = p.Value<string>("topLevel");
            if (!string.IsNullOrEmpty(topLevelName) && !Lookup.TryLevel(doc, topLevelName, out topLevel, out failure)) return failure;

            var heightMm = p.Value<double?>("height") ?? 3000;
            var baseOffsetMm = p.Value<double?>("baseOffset") ?? 0;
            var topOffsetMm = p.Value<double?>("topOffset") ?? 0;
            var locationKey = p.Value<string>("locationLine") ?? "wall_centerline";
            var structural = p.Value<bool?>("structural") ?? false;

            // A curtain wall type makes a curtain wall through the same Wall.Create call (B2M). Curtain walls have no
            // core or finish faces to align and are never structural, so refuse those options instead of failing inside Revit.
            var curtain = wallType.Kind == WallKind.Curtain;
            if (curtain && locationKey != "wall_centerline")
                return CommandResult.Fail("INVALID_PARAMS: '" + wallType.Name + "' is a curtain wall type; curtain walls are placed on their "
                    + "centreline, so locationLine must be wall_centerline or left out.");
            if (curtain && structural)
                return CommandResult.Fail("INVALID_PARAMS: '" + wallType.Name + "' is a curtain wall type; curtain walls cannot be structural.");

            if (topLevel == null && heightMm <= 0)
                return CommandResult.Fail("INVALID_HEIGHT: height must be more than 0 mm.");
            if (topLevel != null && topLevel.ProjectElevation + Mm.ToFeet(topOffsetMm) <= level.ProjectElevation + Mm.ToFeet(baseOffsetMm))
                return CommandResult.Fail("INVALID_HEIGHT: the wall top (topLevel + topOffset) must be above its base (level + baseOffset).");

            var z = level.ProjectElevation;
            var start = new XYZ(Mm.ToFeet(p.Value<double>("startX")), Mm.ToFeet(p.Value<double>("startY")), z);
            var end = new XYZ(Mm.ToFeet(p.Value<double>("endX")), Mm.ToFeet(p.Value<double>("endY")), z);
            if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                return CommandResult.Fail("LINE_TOO_SHORT: start and end are closer than Revit's shortest allowed line ("
                    + Mm.FromFeet(app.Application.ShortCurveTolerance) + " mm).");
            var line = Line.CreateBound(start, end);

            using (var tx = new Transaction(doc, "B2M: Create Wall"))
            {
                tx.Start();
                try
                {
                    var wall = Wall.Create(doc, line, wallType.Id, level.Id, Mm.ToFeet(heightMm), Mm.ToFeet(baseOffsetMm), false, structural);

                    if (topLevel != null)
                    {
                        wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(topLevel.Id);
                        wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET).Set(Mm.ToFeet(topOffsetMm));
                    }

                    var locationLine = LocationLines[locationKey];
                    if (locationLine != WallLocationLine.WallCenterline)
                    {
                        // Changing the location line keeps the wall where it is, so put the line back
                        // afterwards: the chosen reference then lies on the requested points.
                        wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set((int)locationLine);
                        doc.Regenerate();
                        ((LocationCurve)wall.Location).Curve = line;
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var placed = ((LocationCurve)wall.Location).Curve;
                    var a = placed.GetEndPoint(0);
                    var b = placed.GetEndPoint(1);
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(wall.Id),
                        elementType = "wall",
                        curtain,
                        typeId = RevitCompat.GetId(wall.WallType.Id),
                        typeName = wall.WallType.Name,
                        widthMm = Mm.FromFeet(wall.Width),
                        level = level.Name,
                        topLevel = topLevel?.Name,
                        locationLine = locationKey,
                        startMm = new[] { Mm.FromFeet(a.X), Mm.FromFeet(a.Y) },
                        endMm = new[] { Mm.FromFeet(b.X), Mm.FromFeet(b.Y) },
                        heightMm = Mm.FromFeet(wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM).AsDouble()),
                        baseOffsetMm = Mm.FromFeet(wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET).AsDouble()),
                        structural
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the wall: " + ex.Message);
                }
            }
        }
    }
}
