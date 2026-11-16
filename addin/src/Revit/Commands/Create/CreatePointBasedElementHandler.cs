// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/CreatePointBasedElementHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD improvement for doors and windows):
// - hostOffset places a hosted family by distance along its wall from the wall's start, so callers
//   need no geometry; sillHeight sets the sill; flipFacing/flipHand set orientation;
// - typeName (with familyName and builtInCategory) as well as typeId;
// - an unknown level is an error with the valid options (no silent first-level fallback);
// - kept: placement-type checks, host and 1 mm position verification before commit.
// Modified for B2M Step 10 (F5): placement goes through FamilyPlacer (see there): offset above the level, rotation,
// the wall side for wall-hosted and face-based families, face-based families on wall, ceiling and floor faces, and
// face-based families without a host (level plane, or a reference plane facing down); created_ids in the result.

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreatePointBasedElementHandler : IRevitCommand
    {
        public string Name => "create_point_based_element";
        public string Description =>
            "Place a family instance (door, window, furniture, fixture, device, equipment) on a named level. Doors and windows need "
            + "host_id of a wall; optional sillHeight (mm), flipFacing, flipHand. " + FamilyPlacer.OptionsText + " "
            + "Choose the type by typeId or typeName (+ familyName, builtInCategory such as OST_Doors).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""typeId"":{""type"":""integer""},
            ""typeName"":{""type"":""string""},
            ""familyName"":{""type"":""string""},
            ""builtInCategory"":{""type"":""string""},
            ""level"":{""type"":""string""},
            ""sillHeight"":{""type"":""number""},
            " + FamilyPlacer.SchemaProperties(false) + @"},
            ""required"":[""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            BuiltInCategory? category = null;
            var categoryName = p.Value<string>("builtInCategory");
            if (!string.IsNullOrEmpty(categoryName))
            {
                if (!Lookup.TryBuiltInCategory(categoryName, out var bic, out var bad)) return bad;
                category = bic;
            }
            if (!Lookup.TryFamilySymbol(doc, p.Value<long?>("typeId"), p.Value<string>("familyName"), p.Value<string>("typeName"),
                    category, out var symbol, out var failure)) return failure;
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out failure)) return failure;
            if (!FamilyPlacer.TryRead(p, false, out var request, out failure)) return failure;
            if (!FamilyPlacer.TryPlace(doc, symbol, level, request, symbol.Category?.Name ?? "instance", out var placed, out failure))
                return failure;

            var instance = placed.Instance;
            var actual = placed.Location;
            var sillNow = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            var facing = instance.FacingOrientation;
            return CommandResult.Ok(new
            {
                elementId = RevitCompat.GetId(instance.Id),
                familyName = symbol.FamilyName,
                typeName = symbol.Name,
                typeId = RevitCompat.GetId(symbol.Id),
                builtInCategory = Lookup.BuiltInName(instance.Category),
                placement_type = symbol.Family.FamilyPlacementType.ToString(),
                placement_mode = placed.Mode.ToString(),
                host_id = instance.Host == null ? (long?)null : RevitCompat.GetId(instance.Host.Id),
                side = placed.Side,
                level = level.Name,
                location_mm = new { x = Mm.FromFeet(actual.X), y = Mm.FromFeet(actual.Y), z = Mm.FromFeet(actual.Z) },
                offsetMm = Mm.FromFeet(actual.Z - level.ProjectElevation),
                facing = new[] { System.Math.Round(facing.X, 4), System.Math.Round(facing.Y, 4), System.Math.Round(facing.Z, 4) },
                sillHeightMm = sillNow == null ? (double?)null : Mm.FromFeet(sillNow.AsDouble()),
                created_ids = placed.CreatedIds,
                warning = placed.Warning
            });
        }
    }
}
