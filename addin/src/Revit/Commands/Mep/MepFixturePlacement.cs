// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Handlers/Mep/CreateAirTerminalHandler.cs and src/shared/Handlers/Mep/CreateLightingFixtureHandler.cs,
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the two near-identical handlers share this placement routine; level is required by name and
// the type is chosen by type_id or type_name (+ family_name), a miss listing the options (rvt-mcp took type_id only
// and used the level nearest z); a type of the wrong category is an error (rvt-mcp returned success with
// created=false for air terminals); unsupported placement types are refused; the host Revit assigned is checked.
// Modified for B2M Step 10 (F5): placement goes through FamilyPlacer, so face-based fixtures sit on a ceiling's bottom
// face or a wall's chosen side face, or with no ceiling at an offset (facing down); rotation and offset; created_ids.

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Create;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    internal static class MepFixturePlacement
    {
        public static readonly string Schema = @"{""type"":""object"",""properties"":{
            ""type_id"":{""type"":""integer""},
            ""type_name"":{""type"":""string""},
            ""family_name"":{""type"":""string""},
            ""level"":{""type"":""string""},
            " + FamilyPlacer.SchemaProperties(true) + @"},
            ""required"":[""level""]}";

        public static readonly string OptionsText = FamilyPlacer.OptionsText
            .Replace("hostOffset", "host_offset").Replace("wallSide", "wall_side").Replace("towardX/towardY", "toward_x/toward_y")
            .Replace("faceDown", "face_down");

        public static CommandResult Place(UIApplication app, JObject p, BuiltInCategory category, string what)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!Lookup.TryFamilySymbol(doc, p.Value<long?>("type_id"), p.Value<string>("family_name"), p.Value<string>("type_name"),
                    category, out var symbol, out var failure)) return failure;
            if (symbol.Category?.BuiltInCategory != category)
                return CommandResult.Fail("WRONG_CATEGORY: type " + RevitCompat.GetId(symbol.Id) + " is a " + (symbol.Category?.Name ?? "type without category")
                    + ", not a " + what + " (" + category + ").");
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            if (!FamilyPlacer.TryRead(p, true, out var request, out failure)) return failure;
            if (!FamilyPlacer.TryPlace(doc, symbol, level, request, what, out var placed, out failure)) return failure;

            var instance = placed.Instance;
            return CommandResult.Ok(new
            {
                instance_id = RevitCompat.GetId(instance.Id),
                type_id = RevitCompat.GetId(symbol.Id),
                type_name = symbol.Name,
                family_name = symbol.FamilyName,
                level = level.Name,
                placement_type = symbol.Family.FamilyPlacementType.ToString(),
                placement_mode = placed.Mode.ToString(),
                host_id = instance.Host == null ? (long?)null : RevitCompat.GetId(instance.Host.Id),
                side = placed.Side,
                location_mm = MepHelpers.PointMm(placed.Location),
                offset_mm = Mm.FromFeet(placed.Location.Z - level.ProjectElevation),
                created_ids = placed.CreatedIds,
                warning = placed.Warning
            });
        }
    }
}
