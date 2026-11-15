// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Elements/CreateSurfaceBasedElementHandler.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: level is required and an unknown level or type is an error with the valid
// options (rvt-mcp fell back to the first level and the first type); typeName as well as typeId;
// the outline sits at the level's elevation; degenerate edges are rejected; area is read back.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreateSurfaceBasedElementHandler : IRevitCommand
    {
        public string Name => "create_surface_based_element";
        public string Description => "Create a floor or ceiling from a closed outline of {x, y} points in mm on a named level. Optional typeName or typeId.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""elementType"":{""type"":""string"",""enum"":[""floor"",""ceiling""]},
            ""points"":{""type"":""array"",""items"":{""type"":""object""}},
            ""level"":{""type"":""string""},
            ""typeId"":{""type"":""integer""},
            ""typeName"":{""type"":""string""}},
            ""required"":[""elementType"",""points"",""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var elementType = p.Value<string>("elementType");
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out var failure)) return failure;
            if (!Outline.TryLoop((JArray)p["points"], level.ProjectElevation, app.Application.ShortCurveTolerance, out var loop, out failure))
                return failure;

            ElementType type;
            if (elementType == "floor")
            {
                if (!Lookup.TryElementType<FloorType>(doc, p.Value<long?>("typeId"), p.Value<string>("typeName"),
                        ElementTypeGroup.FloorType, "floor", out var floorType, out failure)) return failure;
                type = floorType;
            }
            else
            {
                if (!Lookup.TryElementType<CeilingType>(doc, p.Value<long?>("typeId"), p.Value<string>("typeName"),
                        ElementTypeGroup.CeilingType, "ceiling", out var ceilingType, out failure)) return failure;
                type = ceilingType;
            }

            using (var tx = new Transaction(doc, "B2M: Create " + elementType))
            {
                tx.Start();
                try
                {
                    var loops = new List<CurveLoop> { loop };
                    Element created = elementType == "floor"
                        ? (Element)Floor.Create(doc, loops, type.Id, level.Id)
                        : Ceiling.Create(doc, loops, type.Id, level.Id);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    var area = created.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(created.Id),
                        elementType,
                        typeId = RevitCompat.GetId(type.Id),
                        typeName = type.Name,
                        level = level.Name,
                        areaM2 = Math.Round(area * 0.09290304, 3)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the " + elementType + ": " + ex.Message);
                }
            }
        }
    }
}
