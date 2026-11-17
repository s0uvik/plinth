// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Structural/CreateStructuralColumnHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: level (by name) and column type (type_id, or type_name + family_name) are required and a miss lists
// the options (rvt-mcp used the first column type and the lowest level); x and y are required (rvt-mcp defaulted to 0);
// z_mm dropped: the column stands on the level plus base_offset; the top is top_level + top_offset, or height above the
// base (rvt-mcp's height_mm wrote the top offset only, whatever the top level was); a top that cannot be set rolls back;
// the response reads base and top back from Revit.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Structural
{
    public sealed class CreateStructuralColumnHandler : IRevitCommand
    {
        public string Name => "create_structural_column";
        public string Description =>
            "Place a structural column at x, y (mm) on a named base level. Type by type_id or type_name (+ family_name). Optional "
            + "base_offset (mm); the top is top_level (+ top_offset mm) or height (mm above the base); rotation_deg turns it about its axis.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""type_id"":{""type"":""integer""},
            ""type_name"":{""type"":""string""},
            ""family_name"":{""type"":""string""},
            ""level"":{""type"":""string""},
            ""x"":{""type"":""number""},""y"":{""type"":""number""},
            ""base_offset"":{""type"":""number""},
            ""top_level"":{""type"":""string""},
            ""top_offset"":{""type"":""number""},
            ""height"":{""type"":""number""},
            ""rotation_deg"":{""type"":""number""}},
            ""required"":[""level"",""x"",""y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var height = p.Value<double?>("height");
            var topLevelName = p.Value<string>("top_level");
            var topOffset = p.Value<double?>("top_offset");
            if (height.HasValue && (!string.IsNullOrEmpty(topLevelName) || topOffset.HasValue))
                return CommandResult.Fail("INVALID_PARAMS: give height, or top_level (with top_offset), not both.");
            if (topOffset.HasValue && string.IsNullOrEmpty(topLevelName))
                return CommandResult.Fail("INVALID_PARAMS: top_offset needs top_level.");
            if (height.HasValue && height.Value <= 0) return CommandResult.Fail("INVALID_PARAMS: height must be more than 0 mm.");

            if (!StructuralHelpers.TrySymbol(doc, p, BuiltInCategory.OST_StructuralColumns, "structural column", out var symbol, out var failure))
                return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            Level topLevel = null;
            if (!string.IsNullOrEmpty(topLevelName) && !Lookup.TryLevel(doc, topLevelName, out topLevel, out failure)) return failure;

            var baseOffset = p.Value<double?>("base_offset") ?? 0;
            if (topLevel != null && topLevel.ProjectElevation + Mm.ToFeet(topOffset ?? 0) <= level.ProjectElevation + Mm.ToFeet(baseOffset))
                return CommandResult.Fail("INVALID_HEIGHT: the column top (top_level + top_offset) must be above its base (level + base_offset).");

            var point = new XYZ(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")), level.ProjectElevation);
            using (var tx = new Transaction(doc, "Plinth: Create Structural Column"))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }
                    var column = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.Column);
                    if (column == null) throw new InvalidOperationException("Revit returned no column.");

                    StructuralHelpers.Set(column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, Mm.ToFeet(baseOffset), "base_offset");
                    if (topLevel != null)
                    {
                        StructuralHelpers.Set(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, topLevel.Id, "top_level");
                        StructuralHelpers.Set(column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, Mm.ToFeet(topOffset ?? 0), "top_offset");
                    }
                    else if (height.HasValue)
                    {
                        StructuralHelpers.Set(column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, level.Id, "height");
                        StructuralHelpers.Set(column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, Mm.ToFeet(baseOffset + height.Value), "height");
                    }
                    StructuralHelpers.RotateAboutZ(doc, column.Id, point, p.Value<double?>("rotation_deg") ?? 0);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    return CommandResult.Ok(new
                    {
                        column_id = RevitCompat.GetId(column.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        family_name = symbol.FamilyName,
                        type_name = symbol.Name,
                        location_mm = StructuralHelpers.PointMm((column.Location as LocationPoint)?.Point),
                        base_level = StructuralHelpers.LevelName(doc, column, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM),
                        base_offset_mm = StructuralHelpers.LengthMm(column, BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM),
                        top_level = StructuralHelpers.LevelName(doc, column, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM),
                        top_offset_mm = StructuralHelpers.LengthMm(column, BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the column: " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
