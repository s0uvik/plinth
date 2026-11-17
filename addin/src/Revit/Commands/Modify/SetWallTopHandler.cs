// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/SetWallTopHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the level comes from Lookup.TryLevel (a miss lists the valid levels); mm through Mm; a wall
// whose top cannot be set fails the whole call through the transaction wrapper; all checks still run before any change.

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Modify
{
    public sealed class SetWallTopHandler : IRevitCommand
    {
        public string Name => "set_wall_top";
        public string Description => "Attach walls' top to a level (Top Constraint 'Up to level') with an optional top_offset in mm. All or nothing.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""wall_ids"":{""type"":""array"",""items"":{""type"":""integer""}},
            ""top_level"":{""type"":""string""},
            ""top_offset"":{""type"":""number""}},""required"":[""wall_ids"",""top_level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(ModifyHelpers.NoDocument);
            if (!ModifyHelpers.TryIds(p, "wall_ids", 5000, out var ids, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("top_level")?.Trim(), out var level, out failure)) return failure;
            var offset = Mm.ToFeet(p.Value<double?>("top_offset") ?? 0);
            var top = level.ProjectElevation + offset;

            var walls = new List<Wall>();
            foreach (var id in ids)
            {
                if (!Lookup.TryElement(doc, id, "wall", out Wall wall, out failure)) return failure;
                var baseLevel = doc.GetElement(wall.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                var baseOffset = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
                if (baseLevel != null && top <= baseLevel.ProjectElevation + baseOffset + 1e-6)
                    return CommandResult.Fail("TOP_BELOW_BASE: wall " + id + " starts at " + Mm.FromFeet(baseLevel.ProjectElevation + baseOffset)
                        + " mm ('" + baseLevel.Name + "'); a top at " + Mm.FromFeet(top) + " mm would be at or below it. Nothing was changed.");
                walls.Add(wall);
            }

            var result = ModifyHelpers.InTransaction(doc, "Set wall top", () =>
            {
                foreach (var wall in walls)
                {
                    var constraint = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
                    if (constraint == null || constraint.IsReadOnly || !constraint.Set(level.Id))
                        return CommandResult.Fail("TOP_NOT_SETTABLE: wall " + RevitCompat.GetId(wall.Id) + " does not allow changing its top constraint. Nothing was changed.");
                    // Top Offset is read-only while the wall is unconnected, so look it up only after attaching.
                    var topOffset = wall.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                    if (topOffset == null || topOffset.IsReadOnly || !topOffset.Set(offset))
                        return CommandResult.Fail("TOP_NOT_SETTABLE: wall " + RevitCompat.GetId(wall.Id) + " does not allow a top offset. Nothing was changed.");
                }
                return CommandResult.Ok(null);
            });
            if (!result.Success) return result;

            // Report what Revit stored; joins and attachments can change the resulting height.
            return CommandResult.Ok(new JObject
            {
                ["updated"] = walls.Count,
                ["walls"] = new JArray(walls.Select(w => new JObject
                {
                    ["wall_id"] = RevitCompat.GetId(w.Id),
                    ["top_constraint"] = doc.GetElement(w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId)?.Name,
                    ["top_offset_mm"] = w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET) is Parameter o ? new JValue(Mm.FromFeet(o.AsDouble())) : JValue.CreateNull(),
                    ["height_mm"] = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM) is Parameter h ? new JValue(Mm.FromFeet(h.AsDouble())) : JValue.CreateNull()
                }))
            });
        }
    }
}
