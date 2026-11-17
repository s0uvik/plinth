// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/SetProjectBasePointHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: point_kind is required; fields carry their unit (east_west_mm ... angle_to_true_north_deg);
// an omitted field keeps its current value (rvt-mcp silently reset elevation and angle to 0); an angle for the
// survey point is an error instead of being ignored; the whole coordinate system is returned before and after
// with a "changes" list and summary; a partial apply is a NOT_FULLY_APPLIED error that says the model changed;
// transaction "Plinth: ..." rolled back only while open.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Links
{
    public sealed class SetProjectBasePointHandler : IRevitCommand
    {
        private const double LengthTolerance = 1e-6; // feet
        private const double AngleTolerance = 1e-8;  // radians

        public string Name => "set_project_base_point";
        public string Description =>
            "DESTRUCTIVE: set the shared-coordinate values shown on the project base point or the survey point (point_kind): east_west_mm, "
            + "north_south_mm, elevation_mm and, for the project base point only, angle_to_true_north_deg. Omitted fields keep their value. "
            + "This redefines the project's shared coordinates (and true north for the angle); dry_run: true only reports the change. "
            + "Returns the coordinate system before and after and a list of what changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""point_kind"":{""type"":""string"",""enum"":[""project_base_point"",""survey_point""]},
            ""east_west_mm"":{""type"":""number""},
            ""north_south_mm"":{""type"":""number""},
            ""elevation_mm"":{""type"":""number""},
            ""angle_to_true_north_deg"":{""type"":""number""},
            ""dry_run"":{""type"":""boolean""}},
            ""required"":[""point_kind""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: base points exist only in project documents.");

            var kind = p.Value<string>("point_kind");
            var isSurvey = kind == "survey_point";
            var ew = p.Value<double?>("east_west_mm");
            var ns = p.Value<double?>("north_south_mm");
            var elev = p.Value<double?>("elevation_mm");
            var angle = p.Value<double?>("angle_to_true_north_deg");
            var dryRun = p.Value<bool?>("dry_run") ?? false;

            if (ew == null && ns == null && elev == null && angle == null)
                return CommandResult.Fail("INVALID_PARAMS: give at least one of east_west_mm, north_south_mm, elevation_mm, angle_to_true_north_deg.");
            foreach (var v in new[] { ew, ns, elev, angle })
                if (v.HasValue && (double.IsNaN(v.Value) || double.IsInfinity(v.Value)))
                    return CommandResult.Fail("INVALID_PARAMS: coordinates and angle must be finite numbers.");
            if (isSurvey && angle.HasValue)
                return CommandResult.Fail("INVALID_PARAMS: the survey point has no angle to true north; set it on the project base point. Nothing changed.");

            BasePoint point;
            try
            {
                point = isSurvey ? BasePoint.GetSurveyPoint(doc) : BasePoint.GetProjectBasePoint(doc);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("POINT_NOT_FOUND: could not read the " + kind.Replace('_', ' ') + ": " + ex.Message);
            }
            if (point == null) return CommandResult.Fail("POINT_NOT_FOUND: the project has no " + kind.Replace('_', ' ') + ".");

            // Requested values in Revit units, each only when given.
            var requested = new List<(BuiltInParameter id, string field, double value, bool isAngle)>();
            if (ew.HasValue) requested.Add((BuiltInParameter.BASEPOINT_EASTWEST_PARAM, "east_west_mm", Mm.ToFeet(ew.Value), false));
            if (ns.HasValue) requested.Add((BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM, "north_south_mm", Mm.ToFeet(ns.Value), false));
            if (elev.HasValue) requested.Add((BuiltInParameter.BASEPOINT_ELEVATION_PARAM, "elevation_mm", Mm.ToFeet(elev.Value), false));
            if (angle.HasValue) requested.Add((BuiltInParameter.BASEPOINT_ANGLETON_PARAM, "angle_to_true_north_deg", LinksHelpers.ToRadians(angle.Value), true));

            foreach (var r in requested)
            {
                var param = point.get_Parameter(r.id);
                if (param == null) return CommandResult.Fail("PARAMETER_UNAVAILABLE: the " + kind.Replace('_', ' ') + " has no " + r.field + " value. Nothing changed.");
                if (param.IsReadOnly) return CommandResult.Fail("PARAMETER_READ_ONLY: " + r.field + " cannot be set on the " + kind.Replace('_', ' ') + " right now. Nothing changed.");
            }

            var warnings = new List<string>();
            var pointBefore = LinksHelpers.BasePointValues(point);
            var requestedDto = new JObject();
            foreach (var r in requested) requestedDto[r.field] = r.isAngle ? angle.Value : r.field == "east_west_mm" ? ew.Value : r.field == "north_south_mm" ? ns.Value : elev.Value;

            if (dryRun)
                return CommandResult.Ok(new JObject
                {
                    ["dry_run"] = true,
                    ["model_changed"] = false,
                    ["changed"] = "Nothing changed (dry run).",
                    ["point_kind"] = kind,
                    ["element_id"] = RevitCompat.GetId(point.Id),
                    ["before"] = pointBefore,
                    ["requested"] = requestedDto
                });

            var before = LinksHelpers.CoordinateSystem(doc, warnings);
            using (var tx = new Transaction(doc, "Plinth: Set " + kind.Replace('_', ' ')))
            {
                tx.Start();
                try
                {
                    foreach (var r in requested) point.get_Parameter(r.id).Set(r.value);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the change back; see failures. Nothing changed.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: could not set the " + kind.Replace('_', ' ') + ": " + ex.Message + " Nothing changed.");
                }
            }

            var after = LinksHelpers.CoordinateSystem(doc, warnings);
            var changes = LinksHelpers.Differences(before, after);
            var applied = new JObject();
            var allApplied = true;
            foreach (var r in requested)
            {
                var ok = Math.Abs(point.get_Parameter(r.id).AsDouble() - r.value) <= (r.isAngle ? AngleTolerance : LengthTolerance);
                applied[r.field] = ok;
                allApplied &= ok;
            }

            var result = new JObject
            {
                ["dry_run"] = false,
                ["model_changed"] = changes.Count > 0,
                ["changed"] = changes.Count == 0
                    ? "The " + kind.Replace('_', ' ') + " already had these values; nothing changed."
                    : "The " + kind.Replace('_', ' ') + " values were set, redefining the project's shared coordinates (" + changes.Count
                      + " value(s) changed; model geometry does not move relative to the internal origin). Save the project to keep it.",
                ["point_kind"] = kind,
                ["element_id"] = RevitCompat.GetId(point.Id),
                ["point_before"] = pointBefore,
                ["requested"] = requestedDto,
                ["point_after"] = LinksHelpers.BasePointValues(point),
                ["applied_fields"] = applied,
                ["changes"] = new JArray(changes),
                ["before"] = before,
                ["after"] = after,
                ["warnings"] = new JArray(warnings)
            };
            return allApplied
                ? CommandResult.Ok(result)
                : CommandResult.Fail("NOT_FULLY_APPLIED: Revit did not apply every requested value"
                    + (changes.Count > 0 ? "; the model WAS changed, see point_after and changes." : "; nothing changed."), result);
        }
    }
}
