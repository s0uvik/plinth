// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Materials/SetMaterialAppearanceHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a pattern id of -1 clears that pattern; a wrong pattern id lists the valid fill patterns of
// the right kind (cut patterns must be drafting patterns); the response reads the material back.
// Duplicate before edit (TypeEditGuard): a material is shared, so it is changed only with duplicate_as (a copy named
// so gets the change; nothing else moves onto it) or confirm_edit_all (= its best-effort user count); otherwise
// MATERIAL_IN_USE before anything changes.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Materials
{
    public sealed class SetMaterialAppearanceHandler : IRevitCommand
    {
        private static readonly string[] PatternFields =
            { "surface_foreground_pattern_id", "surface_background_pattern_id", "cut_foreground_pattern_id", "cut_background_pattern_id" };

        public string Name => "set_material_appearance";
        public string Description =>
            "Set a material's shading: colour (red, green, blue 0-255), transparency 0-100, shininess and smoothness 0-128, "
            + "use_render_appearance_for_shading, and surface/cut fill pattern ids (-1 clears; cut patterns must be drafting patterns)."
            + TypeEditGuard.Rule;
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + MaterialsHelpers.MaterialSchema + @",
            ""red"":{""type"":""integer""},""green"":{""type"":""integer""},""blue"":{""type"":""integer""},
            ""transparency"":{""type"":""integer""},
            ""shininess"":{""type"":""integer""},
            ""smoothness"":{""type"":""integer""},
            ""use_render_appearance_for_shading"":{""type"":""boolean""},
            ""surface_foreground_pattern_id"":{""type"":""integer""},
            ""surface_background_pattern_id"":{""type"":""integer""},
            ""cut_foreground_pattern_id"":{""type"":""integer""},
            ""cut_background_pattern_id"":{""type"":""integer""},
            " + TypeEditGuard.SchemaFields + @"}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!MaterialsHelpers.TryMaterial(doc, p.Value<long?>("material_id"), p.Value<string>("material_name"),
                    "material_id", "material_name", out var mat, out var failure)) return failure;
            if (!MaterialsHelpers.TryColor(p, out var color, out failure)) return failure;
            if (!MaterialsHelpers.TryRange(p, "transparency", 0, 100, out var transparency, out failure)) return failure;
            if (!MaterialsHelpers.TryRange(p, "shininess", 0, 128, out var shininess, out failure)) return failure;
            if (!MaterialsHelpers.TryRange(p, "smoothness", 0, 128, out var smoothness, out failure)) return failure;
            var useRender = p.Value<bool?>("use_render_appearance_for_shading");

            var patterns = new Dictionary<string, ElementId>();
            foreach (var field in PatternFields)
            {
                var id = p.Value<long?>(field);
                if (!id.HasValue) continue;
                if (id.Value == -1)
                {
                    patterns[field] = ElementId.InvalidElementId;
                    continue;
                }
                var cut = field.StartsWith("cut", StringComparison.Ordinal);
                var element = doc.GetElement(RevitCompat.ToElementId(id.Value)) as FillPatternElement;
                if (element == null || (cut && element.GetFillPattern().Target != FillPatternTarget.Drafting))
                {
                    var valid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                        .Where(f => !cut || f.GetFillPattern().Target == FillPatternTarget.Drafting)
                        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(200)
                        .Select(f => new JObject
                        {
                            ["id"] = RevitCompat.GetId(f.Id),
                            ["name"] = f.Name,
                            ["target"] = f.GetFillPattern().Target.ToString()
                        });
                    return CommandResult.Fail((element == null
                            ? "PATTERN_NOT_FOUND: " + field + " " + id.Value + " is not a fill pattern."
                            : "INVALID_PATTERN: " + field + " needs a drafting pattern; '" + element.Name + "' is a model pattern."),
                        new JObject { ["valid_patterns"] = new JArray(valid) });
                }
                patterns[field] = element.Id;
            }

            if (color == null && !transparency.HasValue && !shininess.HasValue && !smoothness.HasValue && !useRender.HasValue && patterns.Count == 0)
                return CommandResult.Fail("INVALID_PARAMS: nothing to change; give at least one appearance field.");

            if (!TypeEditGuard.TryPlan(doc, p, new[] { new SharedEdit(mat) }, out var plan, out failure)) return failure;

            using (var tx = new Transaction(doc, "Plinth: Set material appearance"))
            {
                tx.Start();
                try
                {
                    plan.Apply(doc);
                    mat = (Material)plan.TargetFor(mat);
                    if (color != null) mat.Color = color;
                    if (transparency.HasValue) mat.Transparency = transparency.Value;
                    if (shininess.HasValue) mat.Shininess = shininess.Value;
                    if (smoothness.HasValue) mat.Smoothness = smoothness.Value;
                    if (useRender.HasValue) mat.UseRenderAppearanceForShading = useRender.Value;
                    foreach (var kv in patterns)
                    {
                        switch (kv.Key)
                        {
                            case "surface_foreground_pattern_id": mat.SurfaceForegroundPatternId = kv.Value; break;
                            case "surface_background_pattern_id": mat.SurfaceBackgroundPatternId = kv.Value; break;
                            case "cut_foreground_pattern_id": mat.CutForegroundPatternId = kv.Value; break;
                            case "cut_background_pattern_id": mat.CutBackgroundPatternId = kv.Value; break;
                        }
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("SET_FAILED: could not change the material: " + ex.Message);
                }
            }

            return CommandResult.Ok(new JObject
            {
                ["updated"] = true,
                ["material_id"] = RevitCompat.GetId(mat.Id),
                ["name"] = mat.Name,
                ["color"] = MaterialsHelpers.ColorJson(mat.Color),
                ["transparency"] = mat.Transparency,
                ["shininess"] = mat.Shininess,
                ["smoothness"] = mat.Smoothness,
                ["use_render_appearance_for_shading"] = mat.UseRenderAppearanceForShading,
                ["surface_foreground_pattern_id"] = MaterialsHelpers.IdOrNull(mat.SurfaceForegroundPatternId),
                ["surface_background_pattern_id"] = MaterialsHelpers.IdOrNull(mat.SurfaceBackgroundPatternId),
                ["cut_foreground_pattern_id"] = MaterialsHelpers.IdOrNull(mat.CutForegroundPatternId),
                ["cut_background_pattern_id"] = MaterialsHelpers.IdOrNull(mat.CutBackgroundPatternId),
                ["shared_edits"] = plan.Report()
            });
        }
    }
}
