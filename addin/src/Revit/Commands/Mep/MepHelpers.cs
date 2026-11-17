// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), helpers repeated across the files
// src/shared/Handlers/Mep/*.cs (GetConnectorManager, ResolveSystem, AllSystems, DomainOf, connector lookup), commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: gathered into one class; type and system lookups require an explicit choice and a miss
// returns the valid options (rvt-mcp used the first type or level found); millimetres via Mm.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Mep
{
    internal static class MepHelpers
    {
        public const int MaxOptions = 100;

        /// <summary>Schema fragment shared by the curve-creating commands (start/end points in mm).</summary>
        public const string EndpointProperties =
            @"""start_x"":{""type"":""number""},""start_y"":{""type"":""number""},""start_z"":{""type"":""number""},
            ""end_x"":{""type"":""number""},""end_y"":{""type"":""number""},""end_z"":{""type"":""number""}";

        public static readonly string[] EndpointFields = { "start_x", "start_y", "start_z", "end_x", "end_y", "end_z" };

        public static ConnectorManager GetConnectorManager(Element element)
        {
            try
            {
                if (element is MEPCurve curve) return curve.ConnectorManager;
                if (element is FamilyInstance fi) return fi.MEPModel?.ConnectorManager;
            }
            catch
            {
                // Elements without MEP data throw on access: they simply have no connectors.
            }
            return null;
        }

        /// <summary>The connector whose stable Connector.Id matches (not the enumeration ordinal).</summary>
        public static Connector FindConnector(ConnectorManager manager, int connectorId)
        {
            if (manager == null) return null;
            foreach (Connector c in manager.Connectors)
                if (c.Id == connectorId) return c;
            return null;
        }

        public static bool IsPhysical(Connector connector) =>
            connector.ConnectorType == ConnectorType.End || connector.ConnectorType == ConnectorType.Curve;

        public static XYZ SafeOrigin(Connector connector)
        {
            try { return connector.Origin; }
            catch { return null; } // Logical connectors have no coordinate system.
        }

        public static JArray PointMm(XYZ p) =>
            p == null ? null : new JArray(Mm.FromFeet(p.X), Mm.FromFeet(p.Y), Mm.FromFeet(p.Z));

        public static string DomainName(Domain domain) => domain.ToString().Replace("Domain", "");

        /// <summary>Reads start_x..end_z (mm, internal coordinates) and refuses a line shorter than Revit allows.</summary>
        public static bool TryEndpoints(Autodesk.Revit.ApplicationServices.Application app, JObject p, out XYZ start, out XYZ end,
            out CommandResult failure)
        {
            start = new XYZ(Mm.ToFeet(p.Value<double>("start_x")), Mm.ToFeet(p.Value<double>("start_y")), Mm.ToFeet(p.Value<double>("start_z")));
            end = new XYZ(Mm.ToFeet(p.Value<double>("end_x")), Mm.ToFeet(p.Value<double>("end_y")), Mm.ToFeet(p.Value<double>("end_z")));
            failure = null;
            if (start.DistanceTo(end) > app.ShortCurveTolerance) return true;
            failure = CommandResult.Fail("LINE_TOO_SHORT: start and end are closer than Revit's shortest allowed line ("
                + Mm.FromFeet(app.ShortCurveTolerance) + " mm).");
            return false;
        }

        /// <summary>An element type of class T by ID or exact name. One of the two is required; a miss lists the options.</summary>
        public static bool TryType<T>(Document doc, long? id, string name, string what, string idField, string nameField,
            out T type, out CommandResult failure) where T : ElementType
        {
            type = null;
            failure = null;
            if (id.HasValue && !string.IsNullOrEmpty(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass " + idField + " or " + nameField + ", not both.");
                return false;
            }
            string miss;
            if (id.HasValue)
            {
                type = doc.GetElement(RevitCompat.ToElementId(id.Value)) as T;
                miss = "no " + what + " type has id " + id.Value + ".";
            }
            else if (!string.IsNullOrEmpty(name))
            {
                type = TypesOf<T>(doc).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                miss = "no " + what + " type named '" + name + "'.";
            }
            else
            {
                miss = "pass " + idField + " or " + nameField + ".";
            }
            if (type != null) return true;
            failure = CommandResult.Fail("TYPE_NOT_FOUND: " + miss, new JObject { ["valid_" + what.Replace(' ', '_') + "_types"] = Options(TypesOf<T>(doc)) });
            return false;
        }

        public static JArray Options(IEnumerable<Element> elements) =>
            new JArray(elements.Take(MaxOptions).Select(e => new JObject
            {
                ["id"] = RevitCompat.GetId(e.Id),
                ["name"] = e.Name
            }));

        public static IEnumerable<T> TypesOf<T>(Document doc) where T : Element =>
            new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().OrderBy(t => t.Name);

        /// <summary>Every mechanical, piping and electrical system in the document.</summary>
        public static List<MEPSystem> AllSystems(Document doc)
        {
            var result = new List<MEPSystem>();
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(MechanicalSystem)).Cast<MEPSystem>());
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(PipingSystem)).Cast<MEPSystem>());
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ElectricalSystem)).Cast<MEPSystem>());
            return result;
        }

        public static string DomainOf(MEPSystem system)
        {
            if (system is MechanicalSystem) return "mechanical";
            if (system is PipingSystem) return "piping";
            if (system is ElectricalSystem) return "electrical";
            return "unknown";
        }

        /// <summary>An MEP system by ID, else by exact name (case-insensitive); ambiguity and misses list the options.</summary>
        public static bool TrySystem(Document doc, long? id, string name, out MEPSystem system, out CommandResult failure)
        {
            system = null;
            failure = null;
            if (id.HasValue)
            {
                var element = doc.GetElement(RevitCompat.ToElementId(id.Value));
                system = element as MEPSystem;
                if (system != null) return true;
                failure = CommandResult.Fail(element == null
                    ? "ELEMENT_NOT_FOUND: no element has id " + id.Value + "."
                    : "NOT_AN_MEP_SYSTEM: element " + id.Value + " is a " + element.GetType().Name + ", not an MEP system.");
                return false;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                failure = CommandResult.Fail("INVALID_PARAMS: pass system_id or system_name.");
                return false;
            }
            var all = AllSystems(doc);
            var target = name.Trim();
            var matches = all.Where(s => string.Equals(SafeName(s)?.Trim(), target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                system = matches[0];
                return true;
            }
            failure = CommandResult.Fail(matches.Count == 0
                    ? "SYSTEM_NOT_FOUND: no MEP system named '" + target + "'."
                    : "SYSTEM_AMBIGUOUS: " + matches.Count + " MEP systems are named '" + target + "'; pass system_id.",
                new JObject { ["valid_systems"] = DescribeSystems(matches.Count > 1 ? matches : all) });
            return false;
        }

        public static JArray DescribeSystems(IEnumerable<MEPSystem> systems) =>
            new JArray(systems.OrderBy(s => SafeName(s)).Take(MaxOptions).Select(s => new JObject
            {
                ["id"] = RevitCompat.GetId(s.Id),
                ["name"] = SafeName(s),
                ["domain"] = DomainOf(s)
            }));

        public static string SystemTypeName(Document doc, MEPSystem system)
        {
            var typeId = system.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;
            return (doc.GetElement(typeId) as MEPSystemType)?.Name;
        }

        /// <summary>IsWellConnected exists only on mechanical and piping systems; null for electrical.</summary>
        public static bool? IsWellConnected(MEPSystem system)
        {
            if (system is MechanicalSystem mechanical) return mechanical.IsWellConnected;
            if (system is PipingSystem piping) return piping.IsWellConnected;
            return null;
        }

        public static string SafeName(Element element)
        {
            try { return element?.Name; }
            catch { return null; }
        }

        public static string TypeName(Document doc, Element element)
        {
            var typeId = element.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;
            return SafeName(doc.GetElement(typeId));
        }

        /// <summary>Sets a length parameter in mm; throws when the element has no settable parameter, so the caller rolls back.</summary>
        public static void SetLengthMm(Element element, BuiltInParameter bip, double mm, string field)
        {
            if (mm <= 0) throw new InvalidOperationException(field + " must be more than 0 mm.");
            var parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.IsReadOnly)
                throw new InvalidOperationException(field + " cannot be set on this type (the parameter is missing or read-only).");
            parameter.Set(Mm.ToFeet(mm));
        }

        public static double? ReadLengthMm(Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            return parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double
                ? Mm.FromFeet(parameter.AsDouble())
                : (double?)null;
        }
    }
}
