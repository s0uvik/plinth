// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/CreateStructuralWallHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: level (by name) is required (rvt-mcp used the lowest level); an unknown wall type is an error listing
// the options (rvt-mcp ignored it and kept the default type); without a type the project's default wall type is used and
// reported; the wall is created with its type and height in one call at the level's elevation (rvt-mcp drew it at z=0);
// base_offset added; lines shorter than Revit allows are refused.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Structural
{
    public sealed class CreateStructuralWallHandler : IRevitCommand
    {
        public string Name => "create_structural_wall";
        public string Description =>
            "Create a structural (load-bearing) wall from start to end (mm) on a named level. Optional wall_type_id or wall_type_name "
            + "(default the project's default wall type), height in mm (default 3000) and base_offset in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""wall_type_id"":{""type"":""integer""},
            ""wall_type_name"":{""type"":""string""},
            ""level"":{""type"":""string""},
            ""start_x"":{""type"":""number""},""start_y"":{""type"":""number""},
            ""end_x"":{""type"":""number""},""end_y"":{""type"":""number""},
            ""height"":{""type"":""number""},
            ""base_offset"":{""type"":""number""}},
            ""required"":[""level"",""start_x"",""start_y"",""end_x"",""end_y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var typeId = p.Value<long?>("wall_type_id");
            var typeName = p.Value<string>("wall_type_name");
            if (typeId.HasValue && !string.IsNullOrEmpty(typeName))
                return CommandResult.Fail("INVALID_PARAMS: pass wall_type_id or wall_type_name, not both.");
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out var failure)) return failure;
            if (!Lookup.TryElementType<WallType>(doc, typeId, typeName, ElementTypeGroup.WallType, "wall", out var wallType, out failure))
                return failure;

            var height = p.Value<double?>("height") ?? 3000;
            if (height <= 0) return CommandResult.Fail("INVALID_HEIGHT: height must be more than 0 mm.");
            var baseOffset = p.Value<double?>("base_offset") ?? 0;
            var z = level.ProjectElevation;
            var start = new XYZ(Mm.ToFeet(p.Value<double>("start_x")), Mm.ToFeet(p.Value<double>("start_y")), z);
            var end = new XYZ(Mm.ToFeet(p.Value<double>("end_x")), Mm.ToFeet(p.Value<double>("end_y")), z);
            if (start.DistanceTo(end) <= app.Application.ShortCurveTolerance)
                return CommandResult.Fail("LINE_TOO_SHORT: start and end are closer than Revit's shortest allowed line ("
                    + Mm.FromFeet(app.Application.ShortCurveTolerance) + " mm).");

            using (var tx = new Transaction(doc, "Plinth: Create Structural Wall"))
            {
                tx.Start();
                try
                {
                    var wall = Wall.Create(doc, Line.CreateBound(start, end), wallType.Id, level.Id, Mm.ToFeet(height), Mm.ToFeet(baseOffset), false, true);
                    if (wall == null) throw new InvalidOperationException("Revit returned no wall.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        wall_id = RevitCompat.GetId(wall.Id),
                        wall_type_id = RevitCompat.GetId(wall.WallType.Id),
                        wall_type_name = wall.WallType.Name,
                        level = level.Name,
                        height_mm = StructuralHelpers.LengthMm(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM),
                        base_offset_mm = StructuralHelpers.LengthMm(wall, BuiltInParameter.WALL_BASE_OFFSET),
                        structural = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the wall: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
