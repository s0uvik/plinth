// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Rooms/CreateAreaHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: no silent defaults: rvt-mcp took the first area scheme and the first level when
// none was named; now the scheme is the plan's, the named one, or the only one in the project, and a
// new area plan needs level_name. Without a plan id or name, the area plan of level_name (or the active
// area plan) is used, and several candidates are an error listing them. Misses list valid options.
// The plan creation and the area share one transaction (rvt-mcp's TransactionGroup and
// SafeFailuresPreprocessor are replaced by Plinth's FailureScope, which rolls back on any warning).

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Rooms
{
    public sealed class CreateAreaHandler : IRevitCommand
    {
        public string Name => "create_area";
        public string Description =>
            "Place an area at point x, y (mm) in an area plan: area_plan_view_id or area_plan_view_name, else the area plan of "
            + "level_name (+ area_scheme_name), else the active area plan. create_area_plan_if_missing creates the plan for level_name.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""x"":{""type"":""number""},
            ""y"":{""type"":""number""},
            ""area_plan_view_id"":{""type"":""integer""},
            ""area_plan_view_name"":{""type"":""string""},
            ""area_scheme_name"":{""type"":""string""},
            ""level_name"":{""type"":""string""},
            ""create_area_plan_if_missing"":{""type"":""boolean""},
            ""name"":{""type"":""string""},
            ""number"":{""type"":""string""}},""required"":[""x"",""y""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var xMm = p.Value<double>("x");
            var yMm = p.Value<double>("y");
            var planId = p.Value<long?>("area_plan_view_id");
            var planName = p.Value<string>("area_plan_view_name");
            var schemeName = p.Value<string>("area_scheme_name");
            var levelName = p.Value<string>("level_name");
            var createIfMissing = p.Value<bool?>("create_area_plan_if_missing") ?? false;
            var name = p.Value<string>("name");
            var number = p.Value<string>("number");
            if (planId.HasValue && !string.IsNullOrEmpty(planName))
                return CommandResult.Fail("INVALID_PARAMS: pass area_plan_view_id or area_plan_view_name, not both.");

            CommandResult failure;
            AreaScheme scheme = null;
            if (!string.IsNullOrEmpty(schemeName) && !RoomsHelpers.TryAreaScheme(doc, schemeName, out scheme, out failure)) return failure;
            Level level = null;
            if (!string.IsNullOrEmpty(levelName) && !Lookup.TryLevel(doc, levelName, out level, out failure)) return failure;

            ViewPlan plan = null;
            var createPlan = false;
            if (planId.HasValue || !string.IsNullOrEmpty(planName))
            {
                if (!RoomsHelpers.TryAreaPlan(doc, planId, planName, out plan, out failure)) return failure;
                if (level != null && (plan.GenLevel == null || plan.GenLevel.Id != level.Id))
                    return CommandResult.Fail("LEVEL_MISMATCH: area plan '" + plan.Name + "' is on level '" + plan.GenLevel?.Name + "', not '" + level.Name + "'.");
                if (scheme != null && (plan.AreaScheme == null || plan.AreaScheme.Id != scheme.Id))
                    return CommandResult.Fail("AREA_SCHEME_MISMATCH: area plan '" + plan.Name + "' uses scheme '" + plan.AreaScheme?.Name + "', not '" + scheme.Name + "'.");
            }
            else if (level != null)
            {
                var candidates = RoomsHelpers.AreaPlans(doc)
                    .Where(v => v.GenLevel != null && v.GenLevel.Id == level.Id && (scheme == null || (v.AreaScheme != null && v.AreaScheme.Id == scheme.Id)))
                    .ToList();
                if (candidates.Count > 1)
                    return CommandResult.Fail("AREA_PLAN_AMBIGUOUS: level '" + level.Name + "' has several area plans; pass area_plan_view_id or area_scheme_name.",
                        new JObject { ["valid_area_plans"] = RoomsHelpers.DescribePlans(candidates) });
                plan = candidates.FirstOrDefault();
                if (plan == null)
                {
                    if (!createIfMissing)
                        return CommandResult.Fail("AREA_PLAN_NOT_FOUND: level '" + level.Name + "' has no area plan"
                            + (scheme == null ? "" : " for scheme '" + scheme.Name + "'") + "; pass create_area_plan_if_missing: true to create one.",
                            new JObject { ["valid_area_plans"] = RoomsHelpers.DescribePlans(RoomsHelpers.AreaPlans(doc)) });
                    if (scheme == null)
                    {
                        var schemes = new FilteredElementCollector(doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
                        if (schemes.Count != 1)
                            return CommandResult.Fail("AREA_SCHEME_REQUIRED: " + (schemes.Count == 0
                                    ? "the project has no area scheme."
                                    : "the project has several area schemes; pass area_scheme_name."),
                                new JObject { ["valid_area_schemes"] = new JArray(schemes.Select(s => s.Name).OrderBy(s => s)) });
                        scheme = schemes[0];
                    }
                    createPlan = true;
                }
            }
            else
            {
                plan = app.ActiveUIDocument?.ActiveView as ViewPlan;
                if (plan == null || plan.ViewType != ViewType.AreaPlan
                    || (scheme != null && (plan.AreaScheme == null || plan.AreaScheme.Id != scheme.Id)))
                    return CommandResult.Fail("AREA_PLAN_NOT_FOUND: the active view is not "
                        + (scheme == null ? "an area plan" : "an area plan of scheme '" + scheme.Name + "'")
                        + "; pass area_plan_view_id, area_plan_view_name or level_name.",
                        new JObject { ["valid_area_plans"] = RoomsHelpers.DescribePlans(RoomsHelpers.AreaPlans(doc)) });
            }

            var planLabel = createPlan ? "a new area plan on level '" + level.Name + "'" : "'" + plan.Name + "'";
            using (var tx = new Transaction(doc, "Plinth: Create Area"))
            {
                tx.Start();
                try
                {
                    if (createPlan) plan = ViewPlan.CreateAreaPlan(doc, scheme.Id, level.Id);
                    var area = doc.Create.NewArea(plan, new UV(Mm.ToFeet(xMm), Mm.ToFeet(yMm)));
                    if (area == null) throw new InvalidOperationException("Revit returned no area.");
                    RoomsHelpers.SetNameAndNumber(area, name, number);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");

                    return CommandResult.Ok(new JObject
                    {
                        ["area"] = new JObject
                        {
                            ["element_id"] = RevitCompat.GetId(area.Id),
                            ["name"] = area.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? area.Name,
                            ["number"] = area.Number,
                            ["area_m2"] = RoomsHelpers.M2(area.Area),
                            ["status"] = area.Area > 0.0001 ? "placed" : "not_enclosed"
                        },
                        ["area_plan"] = RoomsHelpers.DescribePlan(plan),
                        ["created_area_plan"] = createPlan,
                        ["location"] = RoomsHelpers.Point((area.Location as LocationPoint)?.Point)
                    });
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("CREATE_FAILED: could not create an area at (" + xMm + ", " + yMm + ") mm in " + planLabel + ": "
                        + ex.Message + " Nothing was created.");
                }
            }
        }
    }
}
