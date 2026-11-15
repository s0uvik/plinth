// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/CreateRoomHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M (PRD improvement): department (B2M stores the flat ID there) is set in the same
// call, replacing the planned set_room_data; level is required with valid options on a miss
// (rvt-mcp took the first level); no floor plan view is needed; a room that ends up unenclosed is
// refused; area and status are read back. Failures go through FailureScope like every command.

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Create
{
    public sealed class CreateRoomHandler : IRevitCommand
    {
        public string Name => "create_room";
        public string Description => "Place a room at a point in mm inside walls on a named level. Optional name, number and department (B2M: flat ID).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""x"":{""type"":""number""},""y"":{""type"":""number""},
            ""level"":{""type"":""string""},
            ""name"":{""type"":""string""},
            ""number"":{""type"":""string""},
            ""department"":{""type"":""string""}},
            ""required"":[""x"",""y"",""level""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!Lookup.TryLevel(doc, p.Value<string>("level"), out var level, out var failure)) return failure;

            var point = new UV(Mm.ToFeet(p.Value<double>("x")), Mm.ToFeet(p.Value<double>("y")));
            using (var tx = new Transaction(doc, "B2M: Create Room"))
            {
                tx.Start();
                try
                {
                    var room = doc.Create.NewRoom(level, point);
                    var name = p.Value<string>("name");
                    var number = p.Value<string>("number");
                    var department = p.Value<string>("department");
                    if (!string.IsNullOrEmpty(name)) room.Name = name;
                    if (!string.IsNullOrEmpty(number)) room.Number = number;
                    if (!string.IsNullOrEmpty(department)) room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT).Set(department);
                    doc.Regenerate();
                    var status = RoomStatus.Of(room);
                    if (status != "placed")
                    {
                        tx.RollBack();
                        return CommandResult.Fail("ROOM_NOT_ENCLOSED: the point is not inside a closed boundary on level '"
                            + level.Name + "'. Nothing was created.");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                    return CommandResult.Ok(new
                    {
                        elementId = RevitCompat.GetId(room.Id),
                        name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString(),
                        number = room.Number,
                        department = room.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString(),
                        level = level.Name,
                        areaM2 = Math.Round(room.Area * 0.09290304, 3),
                        status = RoomStatus.Of(room)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create the room: " + ex.Message);
                }
            }
        }
    }
}
