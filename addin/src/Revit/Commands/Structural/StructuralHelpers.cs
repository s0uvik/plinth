// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), helpers repeated across the files
// src/shared/Handlers/Structural/*.cs (ResolveSymbol, ResolveLevel, ResolveBarType, ResolveShape), commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: gathered into one class; every lookup needs an explicit choice and a miss returns the valid
// options (rvt-mcp used the first symbol, level, bar type or shape found); levels go through Lookup.TryLevel.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Structural
{
    internal static class StructuralHelpers
    {
        public const int MaxOptions = 100;

        /// <summary>A loadable family type of one category by type_id, or by type_name (+ family_name).</summary>
        public static bool TrySymbol(Document doc, JObject p, BuiltInCategory category, string what, out FamilySymbol symbol,
            out CommandResult failure)
        {
            if (!Lookup.TryFamilySymbol(doc, p.Value<long?>("type_id"), p.Value<string>("family_name"), p.Value<string>("type_name"),
                    category, out symbol, out failure)) return false;
            if (symbol.Category?.BuiltInCategory == category) return true;
            failure = CommandResult.Fail("WRONG_CATEGORY: type " + RevitCompat.GetId(symbol.Id) + " is a "
                + (symbol.Category?.Name ?? "type without category") + ", not a " + what + " (" + category + ").");
            symbol = null;
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
                miss = "no " + what + " has id " + id.Value + ".";
            }
            else if (!string.IsNullOrEmpty(name))
            {
                type = TypesOf<T>(doc).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                miss = "no " + what + " named '" + name + "'.";
            }
            else
            {
                miss = "pass " + idField + " or " + nameField + ".";
            }
            if (type != null) return true;
            failure = CommandResult.Fail("TYPE_NOT_FOUND: " + miss,
                new JObject { ["valid_" + what.Replace(' ', '_') + "s"] = Options(TypesOf<T>(doc)) });
            return false;
        }

        public static IEnumerable<T> TypesOf<T>(Document doc) where T : Element =>
            new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().OrderBy(t => t.Name);

        public static JArray Options(IEnumerable<Element> elements) =>
            new JArray(elements.Take(MaxOptions).Select(e => new JObject
            {
                ["id"] = RevitCompat.GetId(e.Id),
                ["name"] = e.Name
            }));

        public static JArray PointMm(XYZ p) =>
            p == null ? null : new JArray(Mm.FromFeet(p.X), Mm.FromFeet(p.Y), Mm.FromFeet(p.Z));

        /// <summary>Rotates an element about the vertical axis through point, in degrees counter-clockwise.</summary>
        public static void RotateAboutZ(Document doc, ElementId id, XYZ point, double degrees)
        {
            if (Math.Abs(degrees) < 1e-9) return;
            var axis = Line.CreateBound(point, point + XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, id, axis, degrees * Math.PI / 180.0);
        }

        public static string LevelName(Document doc, Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            return parameter == null || parameter.StorageType != StorageType.ElementId ? null : doc.GetElement(parameter.AsElementId())?.Name;
        }

        public static double? LengthMm(Element element, BuiltInParameter bip)
        {
            var parameter = element.get_Parameter(bip);
            return parameter != null && parameter.HasValue && parameter.StorageType == StorageType.Double
                ? Mm.FromFeet(parameter.AsDouble())
                : (double?)null;
        }

        /// <summary>Sets a parameter, throwing when the element cannot take it so the caller rolls back.</summary>
        public static void Set(Element element, BuiltInParameter bip, ElementId value, string field)
        {
            var parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.IsReadOnly) throw new InvalidOperationException(field + " cannot be set on this element.");
            parameter.Set(value);
        }

        public static void Set(Element element, BuiltInParameter bip, double value, string field)
        {
            var parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.IsReadOnly) throw new InvalidOperationException(field + " cannot be set on this element.");
            parameter.Set(value);
        }
    }
}
