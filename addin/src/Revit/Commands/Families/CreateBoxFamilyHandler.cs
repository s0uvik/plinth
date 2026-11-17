// New in Plinth (Step 10, fit-out, 2026-11-17): create_box_family. Families are found, adapted (a copy) and only created
// with the user's OK; when the user answers that a fit-out item gets a plain box (their bed set fitted no bedroom),
// Plinth makes one: FamilyFactory.CreateBox builds it from Revit's metric template for the category (Metric Furniture for
// furniture), or from Metric Generic Model with the category set when the category's template is missing, saves it to a
// temporary folder and it is loaded into the project as a NEW family, in one transaction. A family of that name already
// in the project is never overwritten nor edited: it is returned as already_there (nothing created). created_ids lists
// what the load added (the family, its type, and any material or pattern it brought along) so a run can remove exactly
// that. The family's origin is the middle of its back edge (back on +Y, front facing -Y) and its base is at the level, so
// a caller places it with no need to measure it. It opens, saves and closes a family document, so it is refused inside
// batch_execute and must be called directly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    public sealed class CreateBoxFamilyHandler : IRevitCommand
    {
        private const string Origin = "middle of the back edge (back on +Y, front facing -Y), base at the level";

        public string Name => "create_box_family";
        public string Description =>
            "Create a plain box family for a fit-out item (only when the user agreed: families are found, adapted, and created only "
            + "with the user's OK) and load it into the project as a NEW family: one extrusion width_mm x depth_mm x height_mm with "
            + "type parameters Width, Depth, Height and Material, origin at the middle of its back edge (back on +Y, front facing -Y), "
            + "base at the level, one type (type_name, default '<w> x <d> x <h>mm (Plinth)'). category: a built-in category (default "
            + "OST_Furniture); the category's metric template is used (Metric Furniture...), else Metric Generic Model with the "
            + "category set. A family named family_name already in the project is never overwritten: action already_there with its "
            + "family_id and type (created_ids empty). action created: family_id, type_id and created_ids (the family, its type and "
            + "anything the load added, e.g. its material) so a run can remove them. dry_run decides and changes nothing. Call directly, "
            + "not inside batch_execute.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""family_name"":{""type"":""string"",""description"":""the new family's name, e.g. Plinth Bed (also a file name)""},
            ""type_name"":{""type"":""string"",""description"":""default '<w> x <d> x <h>mm (Plinth)'""},
            ""category"":{""type"":""string"",""description"":""built-in category, default OST_Furniture""},
            ""width_mm"":{""type"":""number""},""depth_mm"":{""type"":""number""},""height_mm"":{""type"":""number""},
            ""material_name"":{""type"":""string"",""description"":""the Material parameter's material (default Plinth Fit-out)""},
            ""template_path"":{""type"":""string"",""description"":""an .rft to use instead of the installed metric template""},
            ""dry_run"":{""type"":""boolean""}},""required"":[""family_name"",""width_mm"",""depth_mm"",""height_mm""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: create_box_family works on a project document, not a family.");

            var name = p.Value<string>("family_name") ?? "";
            var nameError = BoxFamily.NameError(name);
            if (nameError != null) return CommandResult.Fail("INVALID_PARAMS: " + nameError + ".");
            double w = Number(p["width_mm"]), d = Number(p["depth_mm"]), h = Number(p["height_mm"]);
            var sizeError = BoxFamily.SizeError(w, d, h);
            if (sizeError != null) return CommandResult.Fail("INVALID_PARAMS: " + sizeError + ".");
            var typeName = (p.Value<string>("type_name") ?? "").Trim();
            if (p["type_name"] != null && p["type_name"].Type != JTokenType.Null && typeName.Length == 0)
                return CommandResult.Fail("INVALID_PARAMS: type_name must not be empty (omit it for the default).");
            if (typeName.Length == 0) typeName = BoxFamily.DefaultTypeName(w, d, h);
            var categoryName = p.Value<string>("category") ?? "OST_Furniture";
            if (!Lookup.TryBuiltInCategory(categoryName, out var category, out var failure)) return failure;
            var materialName = p.Value<string>("material_name");
            var dryRun = p.Value<bool?>("dry_run") ?? false;

            // A family of that name is never overwritten: returned as it is.
            var existing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Name == name ? 0 : 1).FirstOrDefault();
            if (existing != null) return AlreadyThere(existing, typeName, category, w, d, h, dryRun);

            var year = app.Application.VersionNumber;
            if (!TryTemplate(p.Value<string>("template_path"), categoryName, year, out var templatePath, out var fallback, out failure)) return failure;
            var result = new JObject
            {
                ["action"] = "created",
                ["dry_run"] = dryRun,
                ["family_name"] = name,
                ["type_name"] = typeName,
                ["category"] = category.ToString(),
                ["template"] = Path.GetFileName(templatePath),
                ["template_fallback"] = fallback,
                ["placement"] = "OneLevelBased",
                ["origin"] = Origin,
                ["dimensions_mm"] = Dimensions(w, d, h),
                ["box_mm"] = Box(w, d, h),
                ["parameters"] = new JArray("Width", "Depth", "Height", FamilyFactory.BoxMaterialParameterName),
                ["family_id"] = null,
                ["type_id"] = null,
                ["created_ids"] = new JArray()
            };
            if (dryRun) return CommandResult.Ok(result);
            if (doc.IsReadOnly) return CommandResult.Fail("READ_ONLY_DOCUMENT: the document cannot be changed.");
            if (doc.IsModifiable) return CommandResult.Fail("TRANSACTION_OPEN: another transaction is open; call create_box_family on its own.");

            var folder = Path.Combine(Path.GetTempPath(), "Plinth", "box-families", Guid.NewGuid().ToString("N"));
            var savePath = Path.Combine(folder, name + ".rfa");
            try
            {
                try
                {
                    result["build"] = FamilyFactory.CreateBox(app.Application, templatePath, category, savePath, typeName,
                        Mm.ToFeet(w), Mm.ToFeet(d), Mm.ToFeet(h), materialName);
                }
                catch (FamilyBuildException ex)
                {
                    return CommandResult.Fail("CREATE_FAILED: building box family '" + name + "' failed at step '" + ex.Step + "': " + ex.Message
                        + ". Nothing was changed in the project.", new JObject { ["step"] = ex.Step, ["template"] = Path.GetFileName(templatePath) });
                }
                return Load(doc, savePath, name, typeName, category, result);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A temporary file left behind is harmless; the project is what matters.
                }
            }
        }

        private static double Number(JToken token) =>
            token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? token.Value<double>() : double.NaN;

        private static JObject Dimensions(double w, double d, double h) => new JObject { ["Width"] = w, ["Depth"] = d, ["Height"] = h };

        private static JObject Box(double w, double d, double h)
        {
            var box = BoxFamily.Box(w, d, h);
            return new JObject { ["min"] = new JArray(box[0]), ["max"] = new JArray(box[1]) };
        }

        /// <summary>The given .rft, else the category's metric template, else the generic model's (the category is then set).</summary>
        private static bool TryTemplate(string given, string category, string year, out string path, out bool fallback, out CommandResult failure)
        {
            failure = null;
            fallback = false;
            if (!string.IsNullOrWhiteSpace(given))
            {
                path = given.Trim();
                if (Path.IsPathRooted(path) && File.Exists(path) && string.Equals(Path.GetExtension(path), ".rft", StringComparison.OrdinalIgnoreCase))
                    return true;
                failure = CommandResult.Fail("TEMPLATE_NOT_FOUND: template_path must be an existing family template (.rft) given by its full path.");
                return false;
            }
            var folder = FamilyFileSearch.TemplatesFolder(year);
            var tried = BoxFamily.Templates(category).Select(t => Path.Combine(folder, t)).ToList();
            path = tried.FirstOrDefault(File.Exists);
            if (path != null)
            {
                fallback = Path.GetFileName(path) == BoxFamily.GenericTemplate && category != "OST_GenericModel";
                return true;
            }
            failure = CommandResult.Fail("TEMPLATE_NOT_FOUND: none of Revit's family templates for a box is installed (see tried). Install the Revit "
                + year + " English family templates, or give template_path.", new JObject { ["tried"] = new JArray(tried) });
            return false;
        }

        private static CommandResult AlreadyThere(Family family, string typeName, BuiltInCategory category, double w, double d, double h, bool dryRun)
        {
            var types = EnsureFamilyTypeHandler.TypesOf(family);
            var type = types.FirstOrDefault(t => t.Name == typeName)
                ?? types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase)) ?? types.FirstOrDefault();
            var notes = new JArray();
            if (family.FamilyCategory == null || family.FamilyCategory.BuiltInCategory != category)
                notes.Add("the family is " + (family.FamilyCategory?.Name ?? "of no category") + ", not " + category);
            if (type == null) notes.Add("the family has no types");
            else if (!string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                notes.Add("it has no type '" + typeName + "'; its type '" + type.Name + "' is returned");
            return CommandResult.Ok(new JObject
            {
                ["action"] = "already_there",
                ["dry_run"] = dryRun,
                ["family_name"] = family.Name,
                ["type_name"] = type?.Name,
                ["category"] = Lookup.BuiltInName(family.FamilyCategory),
                ["placement"] = family.FamilyPlacementType.ToString(),
                ["family_id"] = RevitCompat.GetId(family.Id),
                ["type_id"] = type == null ? null : (JToken)RevitCompat.GetId(type.Id),
                ["types"] = new JArray(types.Select(t => new JObject { ["type_id"] = RevitCompat.GetId(t.Id), ["type_name"] = t.Name })),
                ["requested_mm"] = Dimensions(w, d, h),
                ["notes"] = notes,
                ["created_ids"] = new JArray()
            });
        }

        /// <summary>Elements a family load may bring along besides the family and its types (removed with them).</summary>
        private static HashSet<long> Brought(Document doc) => new HashSet<long>(
            new FilteredElementCollector(doc).OfClass(typeof(Material)).ToElementIds()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).ToElementIds())
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).ToElementIds())
                .Select(RevitCompat.GetId));

        private static CommandResult Load(Document doc, string path, string name, string typeName, BuiltInCategory category, JObject result)
        {
            const string unchanged = " Nothing was changed in the project.";
            using (var tx = new Transaction(doc, "Plinth: Load box family " + name))
            {
                tx.Start();
                try
                {
                    var before = Brought(doc);
                    if (!doc.LoadFamily(path, new FamilyLoadOptions(false, false), out var family) || family == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("LOAD_FAILED: Revit refused to load the new family '" + name + "'." + unchanged);
                    }
                    if (family.FamilyCategory == null || family.FamilyCategory.BuiltInCategory != category)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("LOAD_FAILED: the new family loaded as " + (family.FamilyCategory?.Name ?? "no category") + ", not "
                            + category + "." + unchanged);
                    }
                    var types = EnsureFamilyTypeHandler.TypesOf(family);
                    var symbol = types.FirstOrDefault(t => t.Name == typeName);
                    if (symbol == null)
                    {
                        tx.RollBack();
                        return CommandResult.Fail("LOAD_FAILED: the new family has no type '" + typeName + "' (it has " + string.Join(", ", types.Select(t => t.Name))
                            + ")." + unchanged);
                    }
                    var brought = Brought(doc).Where(id => !before.Contains(id)).OrderBy(id => id).ToList();
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the family load back; see failures." + unchanged);
                    result["family_name"] = family.Name;
                    result["family_id"] = RevitCompat.GetId(family.Id);
                    result["type_id"] = RevitCompat.GetId(symbol.Id);
                    result["placement"] = family.FamilyPlacementType.ToString();
                    var created = new List<long> { RevitCompat.GetId(family.Id) };
                    created.AddRange(types.Select(t => RevitCompat.GetId(t.Id)));
                    created.AddRange(brought);
                    result["created_ids"] = new JArray(created.Distinct());
                    result["brought_along_ids"] = new JArray(brought);
                    return CommandResult.Ok(result);
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("LOAD_FAILED: could not load the new family '" + name + "': " + ex.Message + unchanged);
                }
            }
        }
    }
}
