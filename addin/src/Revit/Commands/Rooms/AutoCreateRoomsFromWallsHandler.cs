// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/AutoCreateRoomsFromWallsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a room that fails to place is counted in failedCount (rvt-mcp only added a
// warning, so batch_execute kept a partial result); dry_run defaults to true as before; the level
// miss lists valid levels; room area and centroid are read after commit as before.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Rooms
{
    public sealed class AutoCreateRoomsFromWallsHandler : IRevitCommand
    {
        public string Name => "auto_create_rooms_from_walls";
        public string Description => "Place a room in every enclosed area on a level that has none. dry_run (default true) only counts them.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""level_name"":{""type"":""string""},
            ""phase_name"":{""type"":""string""},
            ""name_prefix"":{""type"":""string""},
            ""number_prefix"":{""type"":""string""},
            ""start_number"":{""type"":""integer""},
            ""dry_run"":{""type"":""boolean""},
            ""limit"":{""type"":""integer""}},""required"":[""level_name""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var dryRun = p.Value<bool?>("dry_run") ?? true;
            var limit = p.Value<int?>("limit") ?? 500;
            var startNumber = p.Value<int?>("start_number") ?? 1;
            var namePrefix = p.Value<string>("name_prefix");
            var numberPrefix = p.Value<string>("number_prefix") ?? "";
            if (limit < 1 || limit > 2000) return CommandResult.Fail("INVALID_PARAMS: limit must be 1 to 2000.");
            if (startNumber < 1) return CommandResult.Fail("INVALID_PARAMS: start_number must be 1 or more.");
            if (!Lookup.TryLevel(doc, p.Value<string>("level_name"), out var level, out var failure)) return failure;

            var phase = ResolvePhase(app, doc, p.Value<string>("phase_name"), out failure);
            if (phase == null) return failure;

            // Plan topology may update Revit's cached topology, so it shares one transaction with
            // room placement; a dry run rolls that back.
            using (var tx = new Transaction(doc, "B2M: Auto Create Rooms"))
            {
                tx.Start();
                try
                {
                    var circuits = doc.get_PlanTopology(level, phase).Circuits.Cast<PlanCircuit>()
                        .Where(c => c != null && !c.IsRoomLocated).ToList();
                    if (circuits.Count > limit)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("TOO_MANY: " + circuits.Count + " enclosed areas exceed limit " + limit + ". Raise limit to proceed.");
                    }
                    if (dryRun)
                    {
                        tx.RollBack();
                        return CommandResult.Ok(new
                        {
                            dry_run = true,
                            level = level.Name,
                            phase = phase.Name,
                            candidate_count = circuits.Count,
                            created_count = 0,
                            created_room_ids = new long[0]
                        });
                    }

                    var created = new List<long>();
                    var errors = new List<string>();
                    for (var i = 0; i < circuits.Count; i++)
                    {
                        try
                        {
                            var room = doc.Create.NewRoom(doc.Create.NewRoom(phase), circuits[i]);
                            room.Name = string.IsNullOrEmpty(namePrefix) ? "Room" : namePrefix;
                            room.Number = numberPrefix + (startNumber + i);
                            created.Add(RevitCompat.GetId(room.Id));
                        }
                        catch (Exception ex)
                        {
                            errors.Add("area " + i + ": " + ex.Message);
                        }
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");

                    var rooms = new JArray(created.Select(id =>
                    {
                        var room = doc.GetElement(RevitCompat.ToElementId(id)) as Room;
                        var point = (room?.Location as LocationPoint)?.Point;
                        return new JObject
                        {
                            ["room_id"] = id,
                            ["number"] = room?.Number,
                            ["area_m2"] = room == null ? null : new JValue(Math.Round(room.Area * 0.09290304, 4)),
                            ["centroid"] = point == null ? null : new JObject { ["x_mm"] = Mm.FromFeet(point.X), ["y_mm"] = Mm.FromFeet(point.Y) }
                        };
                    }));
                    return CommandResult.Ok(new JObject
                    {
                        ["dry_run"] = false,
                        ["level"] = level.Name,
                        ["phase"] = phase.Name,
                        ["candidate_count"] = circuits.Count,
                        ["created_count"] = created.Count,
                        ["failedCount"] = errors.Count,
                        ["errors"] = new JArray(errors),
                        ["created_room_ids"] = new JArray(created),
                        ["rooms"] = rooms
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create rooms: " + ex.Message);
                }
            }
        }

        private static Phase ResolvePhase(UIApplication app, Document doc, string name, out CommandResult failure)
        {
            failure = null;
            var phases = new FilteredElementCollector(doc).OfClass(typeof(Phase)).Cast<Phase>().ToList();
            if (!string.IsNullOrEmpty(name))
            {
                var named = phases.FirstOrDefault(ph => string.Equals(ph.Name, name, StringComparison.OrdinalIgnoreCase));
                if (named == null)
                    failure = CommandResult.Fail("PHASE_NOT_FOUND: no phase named '" + name + "'.",
                        new JObject { ["valid_phases"] = new JArray(phases.Select(ph => ph.Name)) });
                return named;
            }
            var viewPhase = app.ActiveUIDocument?.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE);
            var phase = viewPhase != null && viewPhase.HasValue ? doc.GetElement(viewPhase.AsElementId()) as Phase : null;
            phase = phase ?? phases.LastOrDefault();
            if (phase == null) failure = CommandResult.Fail("PHASE_NOT_FOUND: the project has no phase.");
            return phase;
        }
    }
}
