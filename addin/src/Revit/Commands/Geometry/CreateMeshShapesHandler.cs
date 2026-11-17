// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Geometry/CreateMeshShapesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: transaction "Plinth: Create mesh shapes", rolled back only while still open; our
// FailureScope rolls it back on any Revit warning; DirectShapes are tagged with application id
// "Plinth.MeshShapes" (replace_existing only deletes shapes this add-in made); a category not allowed
// for DirectShape is an error instead of a silent switch to Generic Models; vertex mm go through Mm;
// "Nothing was changed" outcomes are "CODE: message" errors.
// Duplicate before edit (TypeEditGuard): update_materials changes existing materials named in the file, which changes
// every element using them, so it also needs confirm_edit_all (= each material's best-effort user count; several:
// {"<materialId>": N}); without it the command refuses with MATERIAL_IN_USE before anything changes. No copy is
// offered: to get separate materials, give them new names in the file. Without update_materials, existing materials
// are only reused, never changed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Safety;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Geometry
{
    public sealed class CreateMeshShapesHandler : IRevitCommand
    {
        public const string AppId = "Plinth.MeshShapes";
        private const int MaxListed = 200;

        public string Name => "create_mesh_shapes";
        public string Description =>
            "Create DirectShapes with materials from a local JSON mesh file {materials:[{name,color:[r,g,b],transparency,shininess}], "
            + "shapes:[{name,category,material,vertices:[[x,y,z] mm],faces:[[i,j,k(,l)] 0-based]}]}. Up to 500000 faces and 64 MB per file. "
            + "Existing materials of the same name are reused unchanged; update_materials changes them in place, which needs confirm_edit_all "
            + "(the exact user count from the MATERIAL_IN_USE refusal; several: {\"<materialId>\": N}).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""file"":{""type"":""string""},
            ""replace_existing"":{""type"":""boolean""},
            ""update_materials"":{""type"":""boolean""},
            ""mesh_only"":{""type"":""boolean""},
            " + SharedEditRule.ConfirmOnlySchemaField + @"},""required"":[""file""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("FAMILY_DOCUMENT: open a project; DirectShapes are created in projects.");

            var file = p.Value<string>("file")?.Trim().Trim('"');
            var replace = p.Value<bool?>("replace_existing") ?? false;
            var updateMaterials = p.Value<bool?>("update_materials") ?? false;
            var meshOnly = p.Value<bool?>("mesh_only") ?? false;
            var pathError = MeshShapeFile.ValidatePath(file);
            if (pathError != null) return CommandResult.Fail("INVALID_PATH: " + pathError);
            if (!File.Exists(file)) return CommandResult.Fail("FILE_NOT_FOUND: " + file);

            var clock = Stopwatch.StartNew();
            MeshShapeFileSpec spec;
            var prepared = new List<PreparedMeshShape>();
            try
            {
                spec = MeshShapeFile.Load(file);
                for (var i = 0; i < spec.Materials.Count; i++)
                {
                    var error = MeshShapeFile.ValidateMaterial(spec.Materials[i], i);
                    if (error != null) return CommandResult.Fail("INVALID_MESH_FILE: " + error + " Nothing was changed.");
                }
                var totalFaces = spec.Shapes.Sum(s => (long)(s?.Faces?.Length ?? 0));
                if (totalFaces > MeshShapeFile.MaxTotalFaces)
                    return CommandResult.Fail("INVALID_MESH_FILE: the file has " + totalFaces + " faces; the limit per call is "
                        + MeshShapeFile.MaxTotalFaces + ". Split it into several files. Nothing was changed.");
                for (var i = 0; i < spec.Shapes.Count; i++) prepared.Add(MeshShapeFile.Prepare(spec.Shapes[i], i));
                spec.Shapes = null; // The prepared loops hold what is needed.
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("INVALID_MESH_FILE: could not read the mesh file: " + ex.Message + " Nothing was changed.");
            }
            if (prepared.Count == 0) return CommandResult.Fail("INVALID_MESH_FILE: the file has no shapes. Nothing was changed.");
            var parseMs = clock.ElapsedMilliseconds;

            var categoryIds = new Dictionary<string, ElementId>();
            foreach (var category in prepared.Select(s => s.Category).Distinct())
            {
                var id = new ElementId(MapCategory(category));
                if (!DirectShape.IsValidCategoryId(id, doc))
                    return CommandResult.Fail("INVALID_CATEGORY: category '" + category + "' cannot hold DirectShapes in this document. Nothing was changed.",
                        new JObject { ["valid_categories"] = new JArray(MeshShapeFile.Categories.Where(c => DirectShape.IsValidCategoryId(new ElementId(MapCategory(c)), doc))) });
                categoryIds[category] = id;
            }

            // update_materials edits existing materials in place: the shared-edit rule applies (confirm only).
            var toUpdate = new List<Material>();
            if (updateMaterials)
            {
                var byName = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var material in spec.Materials)
                    if (byName.TryGetValue(material.Name, out var existing) && !toUpdate.Contains(existing)) toUpdate.Add(existing);
            }
            else if (p[SharedEditRule.ConfirmField] != null)
                return CommandResult.Fail("INVALID_PARAMS: confirm_edit_all applies only with update_materials: true. Nothing was changed.");
            if (!TypeEditGuard.TryPlan(doc, p, toUpdate.Select(m => new SharedEdit(m)), out var plan, out var refusal, copyAllowed: false))
                return refusal;

            var warnings = new JArray();
            var duplicateNames = prepared.GroupBy(s => s.Name).Where(g => g.Count() > 1).Select(g => g.Key).Take(10).ToList();
            if (duplicateNames.Count > 0)
                warnings.Add("Duplicate shape names in the file (each still gets its own element): " + string.Join(", ", duplicateNames));

            int created = 0, updated = 0, reused = 0, deleted = 0;
            var createdIds = new List<KeyValuePair<string, long>>();
            var outcomes = new JObject();
            string current = null;
            using (var tx = new Transaction(doc, "Plinth: Create mesh shapes"))
            {
                tx.Start();
                try
                {
                    var materialIds = PrepareMaterials(doc, spec.Materials, prepared, updateMaterials, ref created, ref updated, ref reused, warnings);
                    if (replace) deleted = DeletePreviousShapes(doc, prepared);
                    foreach (var shape in prepared)
                    {
                        current = shape.Name;
                        if (shape.Loops.Count == 0)
                        {
                            warnings.Add("Shape '" + shape.Name + "' has no usable faces; skipped.");
                            continue;
                        }
                        var materialId = ElementId.InvalidElementId;
                        if (!string.IsNullOrWhiteSpace(shape.Material) && materialIds.TryGetValue(shape.Material, out var mid)) materialId = mid;
                        var objects = Build(shape, materialId, meshOnly, out var outcome);
                        if (objects == null || objects.Count == 0)
                            throw new InvalidOperationException("TessellatedShapeBuilder produced no geometry (outcome " + outcome + ")");
                        outcomes[outcome] = (outcomes.Value<int?>(outcome) ?? 0) + 1;
                        var directShape = DirectShape.CreateElement(doc, categoryIds[shape.Category]);
                        directShape.ApplicationId = AppId;
                        directShape.ApplicationDataId = shape.Name;
                        directShape.SetShape(objects);
                        directShape.SetName(shape.Name);
                        createdIds.Add(new KeyValuePair<string, long>(shape.Name, RevitCompat.GetId(directShape.Id)));
                    }
                    current = null;
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was created.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    var where = current != null ? "shape '" + current + "'" : "the materials or cleanup step";
                    return CommandResult.Fail("CREATE_FAILED: failed at " + where + ": " + ex.Message + ". Nothing was changed.");
                }
            }

            var listed = new JObject();
            foreach (var pair in createdIds.Take(MaxListed))
                if (listed[pair.Key] == null) listed[pair.Key] = pair.Value;
            return CommandResult.Ok(new JObject
            {
                ["file"] = file,
                ["shapes_in_file"] = prepared.Count,
                ["shapes_created"] = createdIds.Count,
                ["deleted_existing"] = deleted,
                ["materials_created"] = created,
                ["materials_updated"] = updated,
                ["materials_reused"] = reused,
                ["shared_edits"] = plan.Report(),
                ["input_faces"] = prepared.Sum(s => s.InputFaces),
                ["faces_built"] = prepared.Sum(s => s.Loops.Count),
                ["skipped_degenerate_faces"] = prepared.Sum(s => s.SkippedDegenerate),
                ["split_non_planar_quads"] = prepared.Sum(s => s.SplitQuads),
                ["closed_shapes"] = prepared.Count(s => s.IsClosed),
                ["geometry_outcomes"] = outcomes,
                ["element_ids"] = listed,
                ["element_ids_truncated"] = createdIds.Count > MaxListed,
                ["warnings"] = warnings,
                ["parse_ms"] = parseMs,
                ["total_ms"] = clock.ElapsedMilliseconds
            });
        }

        /// <summary>Creates the file's missing materials; returns name to id for every material the shapes use.</summary>
        private static Dictionary<string, ElementId> PrepareMaterials(Document doc, IList<MeshMaterialSpec> specs, IList<PreparedMeshShape> shapes,
            bool update, ref int created, ref int updated, ref int reused, JArray warnings)
        {
            var ids = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var spec in specs)
            {
                if (existing.TryGetValue(spec.Name, out var material))
                {
                    ids[spec.Name] = material.Id;
                    if (!update)
                    {
                        reused++;
                        continue;
                    }
                    updated++;
                }
                else
                {
                    material = (Material)doc.GetElement(Material.Create(doc, spec.Name));
                    existing[spec.Name] = material;
                    created++;
                }
                material.Color = new Color((byte)spec.Color[0], (byte)spec.Color[1], (byte)spec.Color[2]);
                if (spec.Transparency.HasValue) material.Transparency = spec.Transparency.Value;
                if (spec.Shininess.HasValue) material.Shininess = spec.Shininess.Value;
                ids[spec.Name] = material.Id;
            }
            foreach (var name in shapes.Select(s => s.Material).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct())
            {
                if (ids.ContainsKey(name)) continue;
                if (existing.TryGetValue(name, out var material)) ids[name] = material.Id;
                else warnings.Add("Material '" + name + "' not found; shapes using it get no material.");
            }
            return ids;
        }

        private static int DeletePreviousShapes(Document doc, IEnumerable<PreparedMeshShape> shapes)
        {
            var names = new HashSet<string>(shapes.Select(s => s.Name), StringComparer.Ordinal);
            var old = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                .Where(d => d.ApplicationId == AppId && names.Contains(d.ApplicationDataId ?? ""))
                .Select(d => d.Id)
                .ToList();
            if (old.Count > 0) doc.Delete(old);
            return old.Count;
        }

        private static IList<GeometryObject> Build(PreparedMeshShape shape, ElementId materialId, bool meshOnly, out string outcome)
        {
            var builder = new TessellatedShapeBuilder();
            builder.OpenConnectedFaceSet(shape.IsClosed && !meshOnly);
            foreach (var loop in shape.Loops)
            {
                var points = new List<XYZ>(loop.Length);
                foreach (var pt in loop) points.Add(new XYZ(Mm.ToFeet(pt[0]), Mm.ToFeet(pt[1]), Mm.ToFeet(pt[2])));
                builder.AddFace(new TessellatedFace(points, materialId));
            }
            builder.CloseConnectedFaceSet();
            builder.Target = meshOnly ? TessellatedShapeBuilderTarget.Mesh : TessellatedShapeBuilderTarget.AnyGeometry;
            builder.Fallback = meshOnly ? TessellatedShapeBuilderFallback.Salvage : TessellatedShapeBuilderFallback.Mesh;
            builder.Build();
            var result = builder.GetBuildResult();
            outcome = result.Outcome.ToString();
            return result.AreObjectsAvailable ? result.GetGeometricalObjects() : null;
        }

        private static BuiltInCategory MapCategory(string category)
        {
            switch (category)
            {
                case "Mass": return BuiltInCategory.OST_Mass;
                case "Planting": return BuiltInCategory.OST_Planting;
                case "Furniture": return BuiltInCategory.OST_Furniture;
                case "SpecialtyEquipment": return BuiltInCategory.OST_SpecialityEquipment;
                case "StructuralColumns": return BuiltInCategory.OST_StructuralColumns;
                case "StructuralFraming": return BuiltInCategory.OST_StructuralFraming;
                case "Roofs": return BuiltInCategory.OST_Roofs;
                case "Floors": return BuiltInCategory.OST_Floors;
                case "Walls": return BuiltInCategory.OST_Walls;
                case "Site": return BuiltInCategory.OST_Site;
                case "Entourage": return BuiltInCategory.OST_Entourage;
                default: return BuiltInCategory.OST_GenericModel;
            }
        }
    }
}
