// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// files src/shared/Handlers/Mep/CreateAirTerminalHandler.cs and src/shared/Handlers/Mep/CreateLightingFixtureHandler.cs,
// commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the two near-identical handlers share this placement routine; level is required by name and
// the type is chosen by type_id or type_name (+ family_name), a miss listing the options (rvt-mcp took type_id only
// and used the level nearest z); a type of the wrong category is an error (rvt-mcp returned success with
// created=false for air terminals); unsupported placement types are refused; the host Revit assigned is checked.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    internal static class MepFixturePlacement
    {
        public const string Schema = @"{""type"":""object"",""properties"":{
            ""type_id"":{""type"":""integer""},
            ""type_name"":{""type"":""string""},
            ""family_name"":{""type"":""string""},
            ""level"":{""type"":""string""},
            ""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""},
            ""host_id"":{""type"":""integer""}},
            ""required"":[""level"",""x"",""y""]}";

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

            var hostId = p.Value<long?>("host_id");
            Element host = null;
            string warning = null;
            var placement = symbol.Family.FamilyPlacementType;
            switch (placement)
            {
                case FamilyPlacementType.OneLevelBased:
                    if (hostId.HasValue) warning = "host_id ignored: this family is not hosted.";
                    break;
                case FamilyPlacementType.OneLevelBasedHosted:
                case FamilyPlacementType.WorkPlaneBased:
                    if (!hostId.HasValue)
                        return CommandResult.Fail("HOST_REQUIRED: this " + what + " family (" + placement + ") needs host_id (a ceiling, duct or wall). Nothing was created.");
                    host = doc.GetElement(RevitCompat.ToElementId(hostId.Value));
                    if (host == null) return CommandResult.Fail("ELEMENT_NOT_FOUND: host " + hostId.Value + " is not in this document.");
                    break;
                default:
                    return CommandResult.Fail("UNSUPPORTED_PLACEMENT: placement type " + placement + " is not supported. Nothing was created.");
            }

            var z = p.Value<double?>("z");
            var point = new XYZ(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")),
                z.HasValue ? Mm.ToFeet(z.Value) : level.ProjectElevation);

            using (var tx = new Transaction(doc, "B2M: Place " + what))
            {
                tx.Start();
                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }
                    var instance = host == null
                        ? doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural)
                        : doc.Create.NewFamilyInstance(point, symbol, host, level, StructuralType.NonStructural);
                    if (instance == null) throw new InvalidOperationException("Revit returned no instance.");
                    doc.Regenerate();
                    if (host != null && (instance.Host == null || instance.Host.Id != host.Id))
                        throw new InvalidOperationException("Revit did not assign the requested host.");

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var actual = (instance.Location as LocationPoint)?.Point;
                    return CommandResult.Ok(new
                    {
                        instance_id = RevitCompat.GetId(instance.Id),
                        type_id = RevitCompat.GetId(symbol.Id),
                        type_name = symbol.Name,
                        family_name = symbol.FamilyName,
                        level = level.Name,
                        placement_type = placement.ToString(),
                        host_id = instance.Host == null ? (long?)null : RevitCompat.GetId(instance.Host.Id),
                        location_mm = MepHelpers.PointMm(actual),
                        warning
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not place the " + what + ": " + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
