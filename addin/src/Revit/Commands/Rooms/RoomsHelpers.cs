// New in B2M: shared lookups for the rooms toolset commands ported in Step 8 (get_room_openings,
// create_room_separator, create_area, create_space, list_areas, compute_room_finishes, tag_all_areas).
// Logic follows the rvt-mcp handlers (https://github.com/bimwright/rvt-mcp, commit b44ee0b,
// Apache License 2.0) that each repeated it; misses here return the valid options.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Rooms
{
    internal static class RoomsHelpers
    {
        public const double SquareMetresPerSquareFoot = 0.09290304;
        public const double CubicMetresPerCubicFoot = 0.0283168466;
        private const int MaxOptions = 100;

        public static double M2(double squareFeet) => Math.Round(squareFeet * SquareMetresPerSquareFoot, 4);

        public static double M3(double cubicFeet) => Math.Round(cubicFeet * CubicMetresPerCubicFoot, 4);

        public static JObject Point(XYZ point) =>
            point == null ? null : new JObject
            {
                ["x_mm"] = Mm.FromFeet(point.X),
                ["y_mm"] = Mm.FromFeet(point.Y),
                ["z_mm"] = Mm.FromFeet(point.Z)
            };

        public static JObject Ref(Element element) =>
            element == null ? null : new JObject { ["element_id"] = RevitCompat.GetId(element.Id), ["name"] = element.Name };

        /// <summary>"placed", "not_enclosed" or "unplaced" for any spatial element (rooms, spaces).</summary>
        public static string StatusOf(SpatialElement element)
        {
            if (!(element.Location is LocationPoint)) return "unplaced";
            var boundary = element.GetBoundarySegments(new SpatialElementBoundaryOptions());
            return element.Area > 0.0001 && boundary != null && boundary.Count > 0 ? "placed" : "not_enclosed";
        }

        /// <summary>A phase by name; without a name, the active view's phase, else the project's last phase.</summary>
        public static bool TryPhase(UIApplication app, Document doc, string name, out Phase phase, out CommandResult failure)
        {
            failure = null;
            var phases = doc.Phases.Cast<Phase>().ToList();
            if (!string.IsNullOrEmpty(name))
            {
                phase = phases.FirstOrDefault(ph => string.Equals(ph.Name, name, StringComparison.OrdinalIgnoreCase));
                if (phase == null)
                    failure = CommandResult.Fail("PHASE_NOT_FOUND: no phase named '" + name + "'.",
                        new JObject { ["valid_phases"] = new JArray(phases.Take(MaxOptions).Select(ph => ph.Name)) });
                return phase != null;
            }
            var viewPhase = app.ActiveUIDocument?.ActiveView?.get_Parameter(BuiltInParameter.VIEW_PHASE);
            phase = viewPhase != null && viewPhase.HasValue ? doc.GetElement(viewPhase.AsElementId()) as Phase : null;
            phase = phase ?? phases.LastOrDefault();
            if (phase == null) failure = CommandResult.Fail("PHASE_NOT_FOUND: the project has no phase.");
            return phase != null;
        }

        public static bool TryAreaScheme(Document doc, string name, out AreaScheme scheme, out CommandResult failure)
        {
            failure = null;
            var schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().OrderBy(s => s.Name).ToList();
            scheme = schemes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (scheme != null) return true;
            failure = CommandResult.Fail("AREA_SCHEME_NOT_FOUND: no area scheme named '" + name + "'.",
                new JObject { ["valid_area_schemes"] = new JArray(schemes.Take(MaxOptions).Select(s => s.Name)) });
            return false;
        }

        public static List<ViewPlan> AreaPlans(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => v.ViewType == ViewType.AreaPlan && !v.IsTemplate).OrderBy(v => v.Name).ToList();

        /// <summary>An area plan (not a template) by ID or by exact name. Misses list the area plans.</summary>
        public static bool TryAreaPlan(Document doc, long? id, string name, out ViewPlan plan, out CommandResult failure)
        {
            failure = null;
            var plans = AreaPlans(doc);
            if (id.HasValue)
                plan = plans.FirstOrDefault(v => RevitCompat.GetId(v.Id) == id.Value);
            else
            {
                var matches = plans.Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1)
                {
                    plan = null;
                    failure = CommandResult.Fail("AREA_PLAN_AMBIGUOUS: several area plans are named '" + name + "'; pass area_plan_view_id.",
                        new JObject { ["valid_area_plans"] = DescribePlans(matches) });
                    return false;
                }
                plan = matches.FirstOrDefault();
            }
            if (plan != null) return true;
            failure = CommandResult.Fail("AREA_PLAN_NOT_FOUND: " + (id.HasValue
                    ? "no area plan has id " + id.Value + "."
                    : "no area plan named '" + name + "'."),
                new JObject { ["valid_area_plans"] = DescribePlans(plans) });
            return false;
        }

        public static JArray DescribePlans(IEnumerable<ViewPlan> plans) =>
            new JArray(plans.Take(MaxOptions).Select(DescribePlan));

        public static JObject DescribePlan(ViewPlan v) => new JObject
        {
            ["element_id"] = RevitCompat.GetId(v.Id),
            ["name"] = v.Name,
            ["level_name"] = v.GenLevel?.Name,
            ["area_scheme_name"] = v.AreaScheme?.Name
        };

        /// <summary>Sets a spatial element's name and number through their parameters.</summary>
        public static void SetNameAndNumber(SpatialElement element, string name, string number)
        {
            if (!string.IsNullOrEmpty(name))
            {
                var param = element.get_Parameter(BuiltInParameter.ROOM_NAME);
                if (param != null && !param.IsReadOnly) param.Set(name);
                else element.Name = name;
            }
            if (!string.IsNullOrEmpty(number))
            {
                var param = element.get_Parameter(BuiltInParameter.ROOM_NUMBER);
                if (param != null && !param.IsReadOnly) param.Set(number);
                else element.Number = number;
            }
        }
    }
}
