// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Mep/FindMepDisconnectsHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: view_id replaces view_only (default the whole model; view_only=true meant the active view); limit
// outside 1..20000 is an error (rvt-mcp clamped it); open connectors report their stable connector_id beside the
// ordinal (rvt-mcp gave only the ordinal, which connect_mep_elements cannot use); domain_filter=electrical also
// matches cable tray and conduit connectors (rvt-mcp missed them); truncated replaces limit_hit;
// IDs are numbers; errors are "CODE: message".

using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Mep
{
    using View = Autodesk.Revit.DB.View;

    public sealed class FindMepDisconnectsHandler : IRevitCommand
    {
        public string Name => "find_mep_disconnects";
        public string Description =>
            "Find MEP elements (ducts, pipes, cable trays, conduits, fittings, equipment, terminals) with open End connectors: gaps in "
            + "runs. domain_filter: all (default), mechanical, piping, electrical. view_id limits the scan to what that view shows. "
            + "limit caps the elements listed (default 2000, max 20000). Each open connector has connector_id and origin in mm.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""domain_filter"":{""type"":""string"",""enum"":[""all"",""mechanical"",""piping"",""electrical""]},
            ""view_id"":{""type"":""integer""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var domainFilter = p.Value<string>("domain_filter") ?? "all";
            var limit = p.Value<int?>("limit") ?? 2000;
            if (limit < 1 || limit > 20000) return CommandResult.Fail("INVALID_PARAMS: limit must be between 1 and 20000.");
            View view = null;
            var viewId = p.Value<long?>("view_id");
            if (viewId.HasValue && !Lookup.TryView(doc, viewId.Value, out view, out var failure)) return failure;

            var candidates = Collector(doc, view).OfClass(typeof(MEPCurve)).WhereElementIsNotElementType().ToElements()
                .Concat(Collector(doc, view).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().ToElements()
                    .Where(e => MepHelpers.GetConnectorManager(e) != null));

            var totalOpen = 0;
            var withDisconnects = 0;
            var rows = new JArray();
            foreach (var element in candidates)
            {
                var open = OpenConnectors(element, domainFilter);
                if (open.Count == 0) continue;
                withDisconnects++;
                totalOpen += open.Count;
                if (rows.Count >= limit) continue;
                rows.Add(new JObject
                {
                    ["element_id"] = RevitCompat.GetId(element.Id),
                    ["category"] = element.Category?.Name,
                    ["type_name"] = MepHelpers.TypeName(doc, element) ?? MepHelpers.SafeName(element),
                    ["open_connector_count"] = open.Count,
                    ["open_connectors"] = open
                });
            }

            return CommandResult.Ok(new
            {
                view_id = view == null ? (long?)null : RevitCompat.GetId(view.Id),
                total_open_connectors = totalOpen,
                elements_with_disconnects = withDisconnects,
                returned = rows.Count,
                truncated = withDisconnects > rows.Count,
                disconnects = rows
            });
        }

        private static FilteredElementCollector Collector(Document doc, View view) =>
            view == null ? new FilteredElementCollector(doc) : new FilteredElementCollector(doc, view.Id);

        private static JArray OpenConnectors(Element element, string domainFilter)
        {
            var open = new JArray();
            var manager = MepHelpers.GetConnectorManager(element);
            if (manager == null) return open;
            var ordinal = -1;
            foreach (Connector connector in manager.Connectors)
            {
                ordinal++;
                try
                {
                    // Only End connectors are real openings; Curve and Logical ones are not.
                    if (connector.ConnectorType != ConnectorType.End || connector.IsConnected) continue;
                    var domain = connector.Domain;
                    if (!Matches(domain, domainFilter)) continue;
                    open.Add(new JObject
                    {
                        ["connector_id"] = connector.Id,
                        ["ordinal"] = ordinal,
                        ["domain"] = MepHelpers.DomainName(domain),
                        ["origin_mm"] = MepHelpers.PointMm(MepHelpers.SafeOrigin(connector))
                    });
                }
                catch
                {
                    // One unreadable connector must not abort the element.
                }
            }
            return open;
        }

        private static bool Matches(Domain domain, string filter)
        {
            switch (filter)
            {
                case "mechanical": return domain == Domain.DomainHvac;
                case "piping": return domain == Domain.DomainPiping;
                case "electrical": return domain == Domain.DomainElectrical || domain == Domain.DomainCableTrayConduit;
                default: return true;
            }
        }
    }
}
