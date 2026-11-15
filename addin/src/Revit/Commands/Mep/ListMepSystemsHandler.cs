// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/ListMepSystemsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: a limit outside 1..10000 is an error (rvt-mcp clamped it); truncated is reported;
// is_well_connected is null for electrical systems (rvt-mcp reported true); IDs are numbers; errors are "CODE: message".

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class ListMepSystemsHandler : IRevitCommand
    {
        public string Name => "list_mep_systems";
        public string Description =>
            "List MEP systems (mechanical, piping, electrical circuits). domain_filter: all (default), mechanical, piping, electrical. "
            + "limit caps the systems returned (default 1000, max 10000). element_count is the pipe or duct network (ducts, pipes, fittings) "
            + "or the circuit members; terminal_count excludes base equipment. A failed membership read is an error, never a zero.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""domain_filter"":{""type"":""string"",""enum"":[""all"",""mechanical"",""piping"",""electrical""]},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var domain = p.Value<string>("domain_filter") ?? "all";
            var limit = p.Value<int?>("limit") ?? 1000;
            if (limit < 1 || limit > 10000) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 10000.");

            var systems = new List<(MEPSystem System, string Domain)>();
            if (domain == "all" || domain == "mechanical")
                systems.AddRange(new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem)).Cast<MEPSystem>().Select(s => (s, "mechanical")));
            if (domain == "all" || domain == "piping")
                systems.AddRange(new FilteredElementCollector(doc).OfClass(typeof(PipingSystem)).Cast<MEPSystem>().Select(s => (s, "piping")));
            if (domain == "all" || domain == "electrical")
                systems.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<MEPSystem>().Select(s => (s, "electrical")));

            var rows = new JArray();
            try
            {
                foreach (var entry in systems.Take(limit))
                {
                    var counts = MepSystemMembership.Read(entry.System).Counts;
                    rows.Add(new JObject
                    {
                        ["id"] = RevitCompat.GetId(entry.System.Id),
                        ["name"] = MepHelpers.SafeName(entry.System) ?? "",
                        ["domain"] = entry.Domain,
                        ["system_type"] = MepHelpers.SystemTypeName(doc, entry.System),
                        ["element_count"] = counts.ElementCount,
                        ["terminal_count"] = counts.TerminalCount,
                        ["is_well_connected"] = MepHelpers.IsWellConnected(entry.System)
                    });
                }
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("READ_FAILED: " + ex.Message);
            }

            return CommandResult.Ok(new
            {
                doc_title = doc.Title,
                total_systems = systems.Count,
                returned = rows.Count,
                truncated = systems.Count > rows.Count,
                systems = rows
            });
        }
    }
}
