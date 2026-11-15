// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/GetPanelScheduleHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: rvt-mcp's ResponsePaging helper replaced by inline paging; a panel name miss or ambiguity lists
// the panels; circuits are sorted by circuit number; load and voltage are converted from Revit's internal units to
// VA and V explicitly; IDs are numbers; errors are "CODE: message".

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    public sealed class GetPanelScheduleHandler : IRevitCommand
    {
        public string Name => "get_panel_schedule";
        public string Description =>
            "Read an electrical panel's circuits (panel by panel_id or panel_name): panel name and type, then per circuit its number, "
            + "name, rating (A), apparent load (VA), voltage (V), poles and member count. Paged by start_circuit and max_circuits "
            + "(default 100, max 1000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""panel_id"":{""type"":""integer""},
            ""panel_name"":{""type"":""string""},
            ""start_circuit"":{""type"":""integer""},
            ""max_circuits"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var start = p.Value<int?>("start_circuit") ?? 0;
            var max = p.Value<int?>("max_circuits") ?? 100;
            if (start < 0) return CommandResult.Fail("INVALID_PARAMS: start_circuit must be 0 or more.");
            if (max < 1 || max > 1000) return CommandResult.Fail("INVALID_PARAMS: max_circuits must be between 1 and 1000.");
            var panelId = p.Value<long?>("panel_id");
            var panelName = p.Value<string>("panel_name");

            FamilyInstance panel;
            if (panelId.HasValue)
            {
                panel = doc.GetElement(RevitCompat.ToElementId(panelId.Value)) as FamilyInstance;
                if (panel == null || panel.Category?.BuiltInCategory != BuiltInCategory.OST_ElectricalEquipment)
                    return CommandResult.Fail("PANEL_NOT_FOUND: element " + panelId.Value + " is not an electrical equipment instance.");
            }
            else if (!string.IsNullOrWhiteSpace(panelName))
            {
                var panels = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().ToList();
                var matches = panels.Where(x => string.Equals(PanelName(x), panelName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                    return CommandResult.Fail(matches.Count == 0
                            ? "PANEL_NOT_FOUND: no electrical panel named '" + panelName + "'."
                            : "PANEL_AMBIGUOUS: " + matches.Count + " panels are named '" + panelName + "'; pass panel_id.",
                        new JObject
                        {
                            ["valid_panels"] = new JArray((matches.Count > 1 ? matches : panels).Take(MepHelpers.MaxOptions)
                                .Select(x => new JObject { ["id"] = RevitCompat.GetId(x.Id), ["name"] = PanelName(x) }))
                        });
                panel = matches[0];
            }
            else
            {
                return CommandResult.Fail("INVALID_PARAMS: pass panel_id or panel_name.");
            }

            var circuits = new List<JObject>();
            foreach (var circuit in new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>())
            {
                FamilyInstance baseEquipment;
                try { baseEquipment = circuit.BaseEquipment; } catch { continue; }
                if (baseEquipment == null || baseEquipment.Id != panel.Id) continue;

                var row = new JObject { ["id"] = RevitCompat.GetId(circuit.Id) };
                Read(row, "circuit_number", () => circuit.CircuitNumber);
                Read(row, "name", () => circuit.Name);
                Read(row, "rating_amps", () => Math.Round(circuit.Rating, 2));
                Read(row, "load_va", () => Math.Round(UnitUtils.ConvertFromInternalUnits(circuit.ApparentLoad, UnitTypeId.VoltAmperes), 1));
                Read(row, "voltage", () => Math.Round(UnitUtils.ConvertFromInternalUnits(circuit.Voltage, UnitTypeId.Volts), 1));
                Read(row, "poles", () => circuit.PolesNumber);
                Read(row, "element_count", () => circuit.Elements?.Size ?? 0);
                circuits.Add(row);
            }
            circuits = circuits.OrderBy(c => c.Value<string>("circuit_number") ?? "", NaturalComparer.Instance).ToList();
            var page = circuits.Skip(start).Take(max).ToList();
            var truncated = circuits.Count > start + page.Count;

            return CommandResult.Ok(new
            {
                panel_id = RevitCompat.GetId(panel.Id),
                panel_name = PanelName(panel),
                panel_type = panel.Symbol?.Name,
                total_circuits = circuits.Count,
                start_circuit = start,
                returned_circuit_count = page.Count,
                truncated,
                next_circuit = truncated ? (int?)(start + page.Count) : null,
                circuits = page
            });
        }

        private static void Read(JObject row, string key, Func<JToken> read)
        {
            try { row[key] = read(); }
            catch { row[key] = null; } // Some circuit properties throw for spare or unloaded circuits.
        }

        private static string PanelName(Element panel)
        {
            var name = panel.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
            return string.IsNullOrEmpty(name) ? MepHelpers.SafeName(panel) : name;
        }

        /// <summary>Sorts "2" before "10" and "1,3" after "1".</summary>
        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new NaturalComparer();

            public int Compare(string a, string b)
            {
                var na = LeadingNumber(a);
                var nb = LeadingNumber(b);
                if (na != nb) return na.CompareTo(nb);
                return string.CompareOrdinal(a, b);
            }

            private static long LeadingNumber(string s)
            {
                var digits = new string((s ?? "").TakeWhile(char.IsDigit).ToArray());
                return digits.Length > 0 && digits.Length < 18 ? long.Parse(digits) : long.MaxValue;
            }
        }
    }
}
