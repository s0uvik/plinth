// New in Plinth (Step 10, F1): the kinds of type list_types_in_file reports and copy_types_from_file copies: loadable
// family types (with their family) and the MEP system families' types the fit-out needs. One table, so both commands
// name a kind the same way.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    internal static class FileTypeKinds
    {
        public const string FamilyType = "family_type";

        /// <summary>MEP kind name to its Revit type class, in listing order.</summary>
        public static readonly KeyValuePair<string, Type>[] Mep =
        {
            new KeyValuePair<string, Type>("pipe_type", typeof(PipeType)),
            new KeyValuePair<string, Type>("pipe_system_type", typeof(PipingSystemType)),
            new KeyValuePair<string, Type>("duct_type", typeof(DuctType)),
            new KeyValuePair<string, Type>("duct_system_type", typeof(MechanicalSystemType)),
            new KeyValuePair<string, Type>("conduit_type", typeof(ConduitType)),
            new KeyValuePair<string, Type>("cable_tray_type", typeof(CableTrayType)),
            new KeyValuePair<string, Type>("distribution_system", typeof(DistributionSysType)),
            new KeyValuePair<string, Type>("voltage_type", typeof(VoltageType)),
        };

        public static IEnumerable<string> All => new[] { FamilyType }.Concat(Mep.Select(m => m.Key));

        public static string AllText => string.Join(", ", All);

        public static bool IsKnown(string kind) => All.Contains(kind);

        public static Type ClassOf(string kind) =>
            kind == FamilyType ? typeof(FamilySymbol) : Mep.Where(m => m.Key == kind).Select(m => m.Value).FirstOrDefault();

        /// <summary>The kind of a type element, or null when it is none of the listed kinds.</summary>
        public static string KindOf(Element element)
        {
            if (element is FamilySymbol) return FamilyType;
            foreach (var m in Mep)
                if (element != null && element.GetType() == m.Value) return m.Key;
            return null;
        }

        public static IEnumerable<Element> TypesOf(Document doc, string kind) =>
            new FilteredElementCollector(doc).OfClass(ClassOf(kind)).WhereElementIsElementType().Cast<Element>();

        /// <summary>The same type in another document: same kind and name (and, for a family type, same family and category).</summary>
        public static bool SameType(Element a, Element b)
        {
            if (KindOf(a) != KindOf(b) || !string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (!(a is FamilySymbol fa) || !(b is FamilySymbol fb)) return true;
            return string.Equals(fa.FamilyName, fb.FamilyName, StringComparison.OrdinalIgnoreCase)
                && fa.Category?.BuiltInCategory == fb.Category?.BuiltInCategory;
        }

        public static JObject DescribeFamilyType(FamilySymbol symbol)
        {
            var o = new JObject
            {
                ["id"] = RevitCompat.GetId(symbol.Id),
                ["category"] = Lookup.BuiltInName(symbol.Category),
                ["category_name"] = symbol.Category?.Name,
                ["family"] = symbol.FamilyName,
                ["type"] = symbol.Name
            };
            try { o["placement"] = symbol.Family?.FamilyPlacementType.ToString(); }
            catch (Exception) { o["placement"] = null; }
            var dims = new JObject();
            foreach (var name in new[] { "Width", "Depth", "Length", "Height", "Diameter" })
            {
                var parameter = symbol.LookupParameter(name);
                if (parameter == null || !parameter.HasValue || parameter.StorageType != StorageType.Double) continue;
                if (parameter.Definition.GetDataType() != SpecTypeId.Length) continue;
                dims[name.ToLowerInvariant()] = Mm.FromFeet(parameter.AsDouble());
            }
            o["dims_mm"] = dims;
            var description = symbol.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString();
            if (!string.IsNullOrEmpty(description)) o["description"] = description;
            return o;
        }

        public static JObject DescribeMep(Document doc, Element type)
        {
            var o = new JObject { ["id"] = RevitCompat.GetId(type.Id), ["name"] = type.Name };
            switch (type)
            {
                case PipingSystemType piping:
                    o["classification"] = piping.SystemClassification.ToString();
                    break;
                case MechanicalSystemType mechanical:
                    o["classification"] = mechanical.SystemClassification.ToString();
                    break;
                case DuctType duct:
                    o["shape"] = duct.Shape.ToString();
                    break;
                case DistributionSysType distribution:
                    o["phase"] = distribution.ElectricalPhase.ToString();
                    o["configuration"] = distribution.ElectricalPhaseConfiguration.ToString();
                    o["wires"] = distribution.NumWires;
                    o["voltage_line_to_line"] = distribution.VoltageLineToLine?.Name;
                    o["voltage_line_to_ground"] = distribution.VoltageLineToGround?.Name;
                    break;
                case VoltageType voltage:
                    o["volts"] = Math.Round(UnitUtils.ConvertFromInternalUnits(voltage.ActualValue, UnitTypeId.Volts), 3);
                    break;
            }
            return o;
        }

        public static JObject Describe(Document doc, Element type) =>
            type is FamilySymbol symbol ? DescribeFamilyType(symbol) : DescribeMep(doc, type);
    }
}
