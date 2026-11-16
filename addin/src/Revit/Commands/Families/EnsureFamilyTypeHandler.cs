// New in B2M: rvt-mcp needs every family type to exist before it can place anything. ensure_family_type makes sure a
// loadable family type exists in the active project, doing only what is missing. The order is FIND, then ADAPT A
// COPY, then ASK; it never builds a family unless the caller passes allow_create: true. The result says what ran:
//   existing   - the family has the type: its b/h are checked (within 0.5 mm) and it is returned; never edited.
//   duplicated - the family is loaded but lacks the type: one of its types is duplicated to a NEW type with b/h set.
//   loaded     - the family is not loaded: <family_name>.rfa is searched for (search_folders, %LOCALAPPDATA%\B2M\families,
//                every language folder of the year's content library), loaded, then checked or duplicated as above.
//   adapted    - no exact match: a related family of the same category (FamilyAdapter: e.g. M_Concrete-Rectangular Beam)
//                gets the new type; when the family itself would need editing, an edited copy saved as <family_name>.rfa
//                is loaded instead and the original is never touched.
//   created    - only with allow_create: FamilyFactory builds the family from Revit's template, saves it in
//                %LOCALAPPDATA%\B2M\families (so later projects find it by name) and loads it with the type.
// With no match and no allow_create it fails with FAMILY_NOT_FOUND so the caller can ask the user. Each project change
// is one transaction; any failure leaves the project unchanged (a file this command saved is deleted again).
// It opens, saves and closes separate family documents and may write files, which no TransactionGroup rollback can
// undo, so it is refused inside batch_execute (BatchExecutor) and must be called directly.
// Duplicate before edit (see TypeEditGuard): it only ever creates types (duplicated, loaded or built); a type that already
// exists is checked and returned, never edited, even for material_name.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Materials;
using B2M.Revit.Commands.Parameters;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    /// <summary>One ensure_family_type call, validated.</summary>
    internal sealed class EnsureRequest
    {
        public Document Doc;
        public FamilyRecipe Recipe;
        public string FamilyName;
        public string TypeName;
        public Dictionary<SectionDimension, double> Mm;
        public Material Material;
        public bool DryRun;
        public bool AllowCreate;
    }

    public sealed class EnsureFamilyTypeHandler : IRevitCommand
    {
        private const double MatchToleranceMm = 0.5;
        private const double MaxSectionMm = 10000;
        private const int MaxOptions = 100;
        private const int MaxClosest = 10;
        internal const string Unchanged = " Nothing was changed.";

        public string Name => "ensure_family_type";
        public string Description =>
            "Make sure a loadable family type exists in the project. category: structural_column or structural_framing; dimensions: "
            + "{b_mm (width), h_mm (depth)}. Order: FIND (existing: type found, b/h must match within 0.5 mm, never edited; duplicated: "
            + "family loaded, a new type is made; loaded: <family_name>.rfa found in search_folders, %LOCALAPPDATA%\\B2M\\families or "
            + "the Revit library), then ADAPT (adapted: a related family such as M_Concrete-Rectangular Beam gets the new type; the "
            + "result's family_name is that family), then ASK (FAMILY_NOT_FOUND: ask the user). allow_create: true builds the family "
            + "from Revit's template instead of asking (created). material_name sets the type's Structural Material. dry_run "
            + "reports what would happen. Call directly, not inside batch_execute.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""category"":{""type"":""string"",""enum"":[""structural_column"",""structural_framing""]},
            ""family_name"":{""type"":""string""},
            ""type_name"":{""type"":""string""},
            ""dimensions"":{""type"":""object"",""description"":""{b_mm, h_mm}: b = width, h = depth""},
            ""material_name"":{""type"":""string""},
            " + FamilyFileSearch.SearchFoldersSchema + @",
            ""allow_create"":{""type"":""boolean"",""description"":""build the family from Revit's template when nothing is found (default false)""},
            ""dry_run"":{""type"":""boolean""}},""required"":[""category"",""family_name"",""type_name"",""dimensions""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (doc.IsFamilyDocument) return CommandResult.Fail("NOT_A_PROJECT: ensure_family_type works on a project document, not a family.");

            var recipe = FamilyRecipe.Find(p.Value<string>("category"));
            if (recipe == null)
                return CommandResult.Fail("INVALID_PARAMS: category must be one of " + string.Join(", ", FamilyRecipe.All.Select(x => x.Key)) + ".");
            var r = new EnsureRequest
            {
                Doc = doc,
                Recipe = recipe,
                FamilyName = (p.Value<string>("family_name") ?? "").Trim(),
                TypeName = (p.Value<string>("type_name") ?? "").Trim(),
                DryRun = p.Value<bool?>("dry_run") ?? false,
                AllowCreate = p.Value<bool?>("allow_create") ?? false
            };
            if (r.FamilyName.Length == 0 || r.FamilyName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || r.FamilyName.EndsWith(".", StringComparison.Ordinal))
                return CommandResult.Fail("INVALID_PARAMS: family_name must be a non-empty name that is also a valid file name (no \\ / : * ? \" < > |).");
            if (r.TypeName.Length == 0) return CommandResult.Fail("INVALID_PARAMS: type_name must not be empty.");
            if (!TryDimensions(p["dimensions"] as JObject, recipe, out r.Mm, out var failure)) return failure;
            var materialName = p.Value<string>("material_name");
            if (!string.IsNullOrWhiteSpace(materialName)
                && !MaterialsHelpers.TryMaterial(doc, null, materialName.Trim(), "material_id", "material_name", out r.Material, out failure)) return failure;
            var year = app.Application.VersionNumber;
            if (!FamilyFileSearch.TryRoots(p["search_folders"], year, out var roots, out failure)) return failure;

            // FIND (a)/(b): the family is already in the project.
            if (!TryLoadedFamily(doc, r.FamilyName, out var family, out failure)) return failure;
            if (family != null)
            {
                if (!CheckFamily(family, recipe, out failure)) return failure;
                return EnsureInLoaded(r, family, "project", null);
            }

            // FIND (c): <family_name>.rfa on disk.
            var files = FamilyFileSearch.ListAll(roots);
            var exact = FamilyFileSearch.FindExact(files, r.FamilyName + ".rfa");
            if (exact != null)
            {
                if (r.DryRun) return CommandResult.Ok(Result(r, "loaded", "search", null, null, null, exact.FileName, exact.Folder, null));
                return LoadAndEnsure(r, exact.Path, "loaded", "search", exact.FileName, exact.Folder, false, null);
            }

            // ADAPT a related family.
            var rejected = new JArray();
            var adapted = FamilyAdapter.TryAdapt(app, r, files, rejected);
            if (adapted != null) return adapted;

            // ASK, unless the caller already has permission to create.
            var templatePath = Path.Combine(FamilyFileSearch.TemplatesFolder(year), recipe.TemplateFileName);
            if (!r.AllowCreate) return NotFound(r, roots, files, rejected, templatePath, year);
            return Create(app, r, templatePath, year);
        }

        // ---- input ------------------------------------------------------------------------

        private static bool TryDimensions(JObject dims, FamilyRecipe recipe, out Dictionary<SectionDimension, double> mm, out CommandResult failure)
        {
            mm = new Dictionary<SectionDimension, double>();
            failure = null;
            var keys = string.Join(", ", recipe.Dimensions.Select(d => d.Key));
            if (dims == null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: dimensions must be an object {" + keys + "} in mm.");
                return false;
            }
            var unknown = dims.Properties().Select(prop => prop.Name).FirstOrDefault(n => recipe.Dimensions.All(d => d.Key != n));
            if (unknown != null)
            {
                failure = CommandResult.Fail("INVALID_PARAMS: dimensions has unknown field '" + unknown + "'. Valid fields: " + keys + ".");
                return false;
            }
            foreach (var d in recipe.Dimensions)
            {
                var token = dims[d.Key];
                var value = token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) ? token.Value<double>() : double.NaN;
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > MaxSectionMm)
                {
                    failure = CommandResult.Fail("INVALID_PARAMS: dimensions." + d.Key + " is required for " + recipe.Key
                        + " and must be a number of mm above 0 and at most " + MaxSectionMm + ".");
                    return false;
                }
                mm[d] = value;
            }
            return true;
        }

        // ---- families and types in the project ----------------------------------------------

        private static bool TryLoadedFamily(Document doc, string name, out Family family, out CommandResult failure)
        {
            failure = null;
            var matches = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            family = matches.FirstOrDefault(f => f.Name == name) ?? (matches.Count == 1 ? matches[0] : null);
            if (family != null || matches.Count == 0) return true;
            failure = CommandResult.Fail("FAMILY_AMBIGUOUS: " + matches.Count + " families are named '" + name + "' apart from letter case; "
                + "give the exact name.", new JObject { ["valid_families"] = FamiliesHelpers.Describe(matches) });
            return false;
        }

        internal static bool CheckFamily(Family family, FamilyRecipe recipe, out CommandResult failure)
        {
            failure = null;
            if (family.IsInPlace)
                failure = CommandResult.Fail("FAMILY_IN_PLACE: '" + family.Name + "' is an in-place family; ensure_family_type needs a loadable family.");
            else if (family.FamilyCategory == null || family.FamilyCategory.BuiltInCategory != recipe.Category)
                failure = CommandResult.Fail("FAMILY_CATEGORY_MISMATCH: family '" + family.Name + "' is " + (family.FamilyCategory?.Name ?? "of no category")
                    + ", not " + recipe.Key + ".", new JObject
                    {
                        ["family_id"] = RevitCompat.GetId(family.Id),
                        ["builtInCategory"] = Lookup.BuiltInName(family.FamilyCategory),
                        ["expected_builtInCategory"] = recipe.Category.ToString()
                    });
            return failure == null;
        }

        internal static List<FamilySymbol> TypesOf(Family family) =>
            family.GetFamilySymbolIds().Select(family.Document.GetElement).OfType<FamilySymbol>()
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

        private static FamilySymbol TypeIn(Family family, string typeName)
        {
            var types = TypesOf(family);
            return types.FirstOrDefault(s => s.Name == typeName)
                ?? types.FirstOrDefault(s => string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The type parameter for each section size (b or Width, h or Depth): a length stored on the type.</summary>
        private static bool TryDimensionParameters(EnsureRequest r, FamilySymbol symbol, out Dictionary<SectionDimension, Parameter> found,
            out CommandResult failure)
        {
            found = new Dictionary<SectionDimension, Parameter>();
            failure = null;
            foreach (var d in r.Recipe.Dimensions)
            {
                var parameter = d.Names.Select(n => ParametersHelpers.FindByName(symbol, n)).FirstOrDefault(IsLength);
                if (parameter == null)
                {
                    failure = CommandResult.Fail("PARAMETER_NOT_FOUND: family '" + symbol.FamilyName + "' has no length type parameter named "
                        + string.Join(" or ", d.Names.Select(n => "'" + n + "'")) + " for " + d.Key + ". Its numeric type parameters are listed.",
                        new JObject { ["numeric_type_parameters"] = NumericParameters(r.Doc, symbol) });
                    return false;
                }
                found[d] = parameter;
            }
            return true;
        }

        private static bool IsLength(Parameter p)
        {
            if (p == null || p.StorageType != StorageType.Double) return false;
            try { return SpecTypeId.Length.Equals(p.Definition?.GetDataType()); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
        }

        private static JArray NumericParameters(Document doc, Element type) =>
            new JArray(type.Parameters.Cast<Parameter>()
                .Where(p => p?.Definition?.Name != null && p.StorageType == StorageType.Double)
                .OrderBy(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase).Take(MaxOptions)
                .Select(p =>
                {
                    var value = ParametersHelpers.Read(doc, p);
                    return new JObject { ["name"] = p.Definition.Name, ["value"] = value["value"], ["unit"] = value["unit"], ["read_only"] = p.IsReadOnly };
                }));

        /// <summary>The type's material parameter: "Structural Material", else its only material type parameter.</summary>
        private static bool TryMaterialParameter(FamilySymbol symbol, out Parameter parameter, out CommandResult failure)
        {
            failure = null;
            var candidates = symbol.Parameters.Cast<Parameter>().Where(IsMaterial).ToList();
            parameter = candidates.FirstOrDefault(p => string.Equals(p.Definition.Name, FamilyFactory.MaterialParameterName, StringComparison.OrdinalIgnoreCase))
                ?? (candidates.Count == 1 ? candidates[0] : null);
            if (parameter != null) return true;
            failure = CommandResult.Fail((candidates.Count == 0
                    ? "MATERIAL_PARAMETER_NOT_FOUND: family '" + symbol.FamilyName + "' has no material type parameter"
                    : "MATERIAL_PARAMETER_AMBIGUOUS: family '" + symbol.FamilyName + "' has several material type parameters and none is named '"
                        + FamilyFactory.MaterialParameterName + "'")
                + ", so material_name cannot be applied to the type (it may be an instance parameter in this family).",
                new JObject { ["material_type_parameters"] = new JArray(candidates.Select(p => p.Definition.Name)) });
            return false;
        }

        private static bool IsMaterial(Parameter p)
        {
            if (p?.Definition == null || p.StorageType != StorageType.ElementId) return false;
            try { return SpecTypeId.Reference.Material.Equals(p.Definition.GetDataType()); }
            catch (Autodesk.Revit.Exceptions.ApplicationException) { return false; }
        }

        /// <summary>True when a type of this family can carry the requested size (and material) without editing the family.</summary>
        internal static bool TypeCanCarry(EnsureRequest r, FamilySymbol symbol) =>
            TryDimensionParameters(r, symbol, out _, out _) && (r.Material == null || TryMaterialParameter(symbol, out _, out _));

        /// <summary>Step (a): the type exists; its size (and material, if asked) must already be right.</summary>
        private static bool CheckExisting(EnsureRequest r, FamilySymbol symbol, out JObject dims, out CommandResult failure)
        {
            if (!CheckSize(r, symbol, true, out dims, out failure)) return false;
            if (r.Material == null) return true;
            if (!TryMaterialParameter(symbol, out var materialParameter, out failure)) return false;
            if (materialParameter.AsElementId() == r.Material.Id) return true;
            var actual = r.Doc.GetElement(materialParameter.AsElementId());
            failure = CommandResult.Fail("TYPE_MATERIAL_MISMATCH: type '" + symbol.Name + "' exists with " + materialParameter.Definition.Name + " = "
                + (actual?.Name ?? "<By Category>") + ", not '" + r.Material.Name + "'. Existing types are never edited; use another type_name.",
                new JObject { ["type_id"] = RevitCompat.GetId(symbol.Id), ["actual_material"] = actual?.Name });
            return false;
        }

        /// <summary>
        /// The type's b and h must be within 0.5 mm of the request. existing: the type was already there (never edited);
        /// otherwise this command just set them, and a difference means the family refused the size.
        /// </summary>
        private static bool CheckSize(EnsureRequest r, FamilySymbol symbol, bool existing, out JObject dims, out CommandResult failure)
        {
            dims = null;
            if (!TryDimensionParameters(r, symbol, out var parameters, out failure)) return false;
            dims = Dimensions(r, parameters);
            if (r.Recipe.Dimensions.All(d => Math.Abs(Mm.FromFeet(parameters[d].AsDouble(), 3) - r.Mm[d]) <= MatchToleranceMm)) return true;
            var actual = string.Join(", ", r.Recipe.Dimensions.Select(d => d.Parameter + " = " + Mm.FromFeet(parameters[d].AsDouble(), 1) + " mm"));
            var requested = string.Join(", ", r.Recipe.Dimensions.Select(d => d.Parameter + " = " + r.Mm[d] + " mm"));
            var data = new JObject { ["type_id"] = RevitCompat.GetId(symbol.Id), ["actual_mm"] = dims, ["requested_mm"] = Requested(r) };
            failure = existing
                ? CommandResult.Fail("TYPE_DIMENSION_MISMATCH: type '" + symbol.Name + "' of family '" + symbol.FamilyName + "' exists with " + actual
                    + ", not the requested " + requested + ". Existing types are never edited; use another type_name.", data)
                : CommandResult.Fail("SIZE_NOT_APPLIED: family '" + symbol.FamilyName + "' did not take " + requested + " (it reads " + actual
                    + "); a formula or constraint in the family drives these parameters.", data);
            return false;
        }

        /// <summary>The overrides that give a new type the requested size and material, keyed by this family's parameter names.</summary>
        private static bool TryOverrides(EnsureRequest r, FamilySymbol source, out JObject overrides, out CommandResult failure)
        {
            overrides = new JObject();
            if (!TryDimensionParameters(r, source, out var parameters, out failure)) return false;
            foreach (var d in r.Recipe.Dimensions) overrides[parameters[d].Definition.Name] = r.Mm[d];
            if (r.Material == null) return true;
            if (!TryMaterialParameter(source, out var materialParameter, out failure)) return false;
            overrides[materialParameter.Definition.Name] = RevitCompat.GetId(r.Material.Id);
            return true;
        }

        /// <summary>Inside an open project transaction: a NEW type named type_name, from the family's first type.</summary>
        private static bool DuplicateInTransaction(EnsureRequest r, Family family, out FamilySymbol created, out JObject dims, out CommandResult failure)
        {
            created = null;
            dims = null;
            var source = TypesOf(family).FirstOrDefault();
            if (source == null)
            {
                failure = CommandResult.Fail("FAMILY_HAS_NO_TYPES: family '" + family.Name + "' has no types to duplicate.");
                return false;
            }
            if (!TryOverrides(r, source, out var overrides, out failure)) return false;
            created = source.Duplicate(r.TypeName) as FamilySymbol;
            if (created == null)
            {
                failure = CommandResult.Fail("DUPLICATE_FAILED: Revit did not duplicate '" + source.Name + "'.");
                return false;
            }
            if (!FamiliesHelpers.TrySetTypeParameters(r.Doc, created, overrides, out _, out var error, out var errorData))
            {
                failure = CommandResult.Fail(error, errorData);
                return false;
            }
            r.Doc.Regenerate();
            // Read back: a family formula or constraint can refuse a size silently.
            return CheckSize(r, created, false, out dims, out failure);
        }

        /// <summary>
        /// Steps (a) and (b) on a family already in the project: return the type if it exists, else make it in one
        /// transaction. extra marks an adapted family (action "adapted").
        /// </summary>
        internal static CommandResult EnsureInLoaded(EnsureRequest r, Family family, string source, JObject extra)
        {
            var adapted = extra != null;
            var symbol = TypeIn(family, r.TypeName);
            if (symbol != null)
            {
                if (!CheckExisting(r, symbol, out var existingDims, out var failure)) return failure;
                return CommandResult.Ok(With(Result(r, adapted ? "adapted" : "existing", source, family, symbol, existingDims, null, null, "existing"), extra));
            }
            if (r.DryRun)
            {
                var first = TypesOf(family).FirstOrDefault();
                if (first == null) return CommandResult.Fail("FAMILY_HAS_NO_TYPES: family '" + family.Name + "' has no types to duplicate.");
                if (!TryOverrides(r, first, out _, out var failure)) return failure;
                return CommandResult.Ok(With(Result(r, adapted ? "adapted" : "duplicated", source, family, null, null, null, null, "duplicated"), extra));
            }
            using (var tx = new Transaction(r.Doc, "B2M: Ensure family type"))
            {
                tx.Start();
                try
                {
                    if (!DuplicateInTransaction(r, family, out var created, out var dims, out var failure)) return RolledBack(tx, failure, Unchanged);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the new type back; see failures." + Unchanged);
                    return CommandResult.Ok(With(Result(r, adapted ? "adapted" : "duplicated", source, family, created, dims, null, null, "duplicated"), extra));
                }
                catch (Exception ex)
                {
                    return RolledBack(tx, CommandResult.Fail("DUPLICATE_FAILED: could not make type '" + r.TypeName + "': " + ex.Message), Unchanged);
                }
            }
        }

        /// <summary>
        /// Loads the file and makes sure the type exists, all in one project transaction. ownTypes: this command made the
        /// family file, so an existing type of that name was made for this request and may take the material.
        /// </summary>
        internal static CommandResult LoadAndEnsure(EnsureRequest r, string path, string action, string source, string fileName, string folder,
            bool ownTypes, JObject extra)
        {
            var doc = r.Doc;
            var note = ownTypes ? " Nothing was changed in the project." : Unchanged;
            using (var tx = new Transaction(doc, "B2M: Ensure family type"))
            {
                tx.Start();
                try
                {
                    if (!doc.LoadFamily(path, new FamilyLoadOptions(false, false), out var family) || family == null)
                        return RolledBack(tx, CommandResult.Fail("LOAD_FAILED: Revit refused to load " + fileName
                            + " (not a family file, saved in a newer Revit, or a family of that name is already loaded)."), note);
                    if (!CheckFamily(family, r.Recipe, out var failure)) return RolledBack(tx, failure, note);

                    FamilySymbol symbol;
                    JObject dims;
                    var existing = TypeIn(family, r.TypeName);
                    // Duplicate before edit holds: with ownTypes the family was made by this call and loaded in this transaction (LoadFamily
                    // never overwrites), so this type is new and unused; an existing project type is never edited (CheckExisting refuses).
                    if (existing != null && ownTypes)
                    {
                        symbol = existing;
                        if (r.Material != null)
                        {
                            if (!TryMaterialParameter(symbol, out var materialParameter, out failure)) return RolledBack(tx, failure, note);
                            if (!FamiliesHelpers.TrySetTypeParameters(doc, symbol,
                                    new JObject { [materialParameter.Definition.Name] = RevitCompat.GetId(r.Material.Id) }, out _, out var error, out var errorData))
                                return RolledBack(tx, CommandResult.Fail(error, errorData), note);
                        }
                        if (!CheckSize(r, symbol, false, out dims, out failure)) return RolledBack(tx, failure, note);
                    }
                    else if (existing != null)
                    {
                        symbol = existing;
                        if (!CheckExisting(r, symbol, out dims, out failure)) return RolledBack(tx, failure, note);
                    }
                    else if (!DuplicateInTransaction(r, family, out symbol, out dims, out failure))
                        return RolledBack(tx, failure, note);

                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the family load back; see failures." + note);
                    return CommandResult.Ok(With(Result(r, action, source, family, symbol, dims, fileName, folder, existing != null ? "existing" : "duplicated"), extra));
                }
                catch (Exception ex)
                {
                    return RolledBack(tx, CommandResult.Fail("LOAD_FAILED: could not load " + fileName + ": " + ex.Message), note);
                }
            }
        }

        // ---- ASK and CREATE -------------------------------------------------------------------

        private static CommandResult NotFound(EnsureRequest r, List<FamilyFileSearch.Root> roots, List<FamilyFileSearch.Hit> files, JArray rejected,
            string templatePath, string year)
        {
            var words = Words(r.FamilyName).Concat(r.Recipe.RelatedNameWords.Select(g => g[0])).Distinct().ToList();
            int Score(string name) => words.Count(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
            var loaded = new FilteredElementCollector(r.Doc).OfClass(typeof(Family)).Cast<Family>().Where(f => !f.IsInPlace)
                .Select(f => new { Name = f.Name, Score = Score(f.Name), Json = new JObject
                {
                    ["family_name"] = f.Name,
                    ["source"] = "project",
                    ["builtInCategory"] = Lookup.BuiltInName(f.FamilyCategory)
                } });
            var onDisk = files.Select(h => new { Name = h.FamilyName, Score = Score(h.FamilyName), Json = With(h.ToJson(), new JObject { ["source"] = "disk" }) });
            var closest = loaded.Concat(onDisk).Where(c => c.Score > 0).OrderByDescending(c => c.Score)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(MaxClosest).Select(c => c.Json);
            var canCreate = File.Exists(templatePath);
            var searched = string.Join(", ", roots.Select(root => root.Kind == "given" ? "search folder " + Path.GetFileName(root.Folder.TrimEnd('\\', '/'))
                : root.Kind == "b2m" ? "the B2M families folder" : "the Revit " + year + " content library"));
            return CommandResult.Fail("FAMILY_NOT_FOUND: no " + r.Recipe.Key + " family named '" + r.FamilyName + "' or similar was found in the "
                + "project or in " + searched + ". Ask the user whether B2M may create one from Revit's family template (call again with "
                + "allow_create: true" + (canCreate ? "" : ", which needs the template that is missing here") + "), or which family file to use "
                + "(give its folder in search_folders).", new JObject
                {
                    ["searched_roots"] = new JArray(roots.Select(root => root.ToJson())),
                    ["candidates"] = new JArray(closest),
                    ["rejected_related_families"] = rejected,
                    ["can_create"] = canCreate,
                    ["template"] = r.Recipe.TemplateFileName,
                    ["dry_run"] = r.DryRun
                });
        }

        private static IEnumerable<string> Words(string name) =>
            System.Text.RegularExpressions.Regex.Split(name.ToLowerInvariant(), "[^a-z0-9]+").Where(w => w.Length >= 2);

        /// <summary>CREATE (allow_create only): build the family from Revit's template, save it in B2M's folder and load it.</summary>
        private static CommandResult Create(UIApplication app, EnsureRequest r, string templatePath, string year)
        {
            if (!File.Exists(templatePath))
                return CommandResult.Fail("TEMPLATE_NOT_FOUND: Revit's family template '" + r.Recipe.TemplateFileName + "' is not installed where "
                    + "expected (see expected_path), so family '" + r.FamilyName + "' cannot be created. Install the Revit " + year
                    + " English family templates, or put " + r.FamilyName + ".rfa in a folder given in search_folders.",
                    new JObject { ["expected_path"] = templatePath });
            var folder = FamilyFileSearch.B2MFamiliesFolder;
            var fileName = r.FamilyName + ".rfa";
            var savePath = Path.Combine(folder, fileName);
            var extra = new JObject { ["template"] = r.Recipe.TemplateFileName };
            if (r.DryRun) return CommandResult.Ok(With(Result(r, "created", "created", null, null, null, fileName, folder, "existing"), extra));
            if (File.Exists(savePath))
                return CommandResult.Fail("FILE_EXISTS: " + fileName + " already exists in the B2M families folder but was not found by the search "
                    + "(the folder scan was cut short). Load it with load_family_from_path or remove it.", new JObject { ["folder"] = folder });

            try
            {
                extra["build"] = FamilyFactory.Create(app.Application, r.Recipe, templatePath, savePath, r.TypeName,
                    Mm.ToFeet(r.Mm[FamilyRecipe.B]), Mm.ToFeet(r.Mm[FamilyRecipe.H]));
            }
            catch (FamilyBuildException ex)
            {
                var kept = !DeleteQuietly(savePath);
                return CommandResult.Fail("CREATE_FAILED: building family '" + r.FamilyName + "' failed at step '" + ex.Step + "': " + ex.Message
                    + ". Nothing was changed in the project" + (kept ? "; the partial file " + fileName + " could not be deleted." : " and no file was kept."),
                    new JObject { ["step"] = ex.Step, ["template"] = r.Recipe.TemplateFileName });
            }
            return LoadOwnFile(r, savePath, "created", "created", folder, extra);
        }

        /// <summary>Loads a file this command just saved; on failure the file is deleted so no half-made family is left behind.</summary>
        internal static CommandResult LoadOwnFile(EnsureRequest r, string path, string action, string source, string folder, JObject extra)
        {
            var fileName = Path.GetFileName(path);
            var loaded = LoadAndEnsure(r, path, action, source, fileName, folder, true, extra);
            if (loaded.Success) return loaded;
            var kept = !DeleteQuietly(path);
            return CommandResult.Fail(loaded.Error + (kept ? " The new file " + fileName + " could not be deleted; remove it before retrying." : " The new file was deleted."),
                loaded.Data ?? new JObject { ["step"] = "load the new family" });
        }

        /// <summary>True when the file is gone afterwards.</summary>
        internal static bool DeleteQuietly(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return !File.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // ---- results ------------------------------------------------------------------------

        private static CommandResult RolledBack(Transaction tx, CommandResult failure, string note)
        {
            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
            return failure.Error.EndsWith(note, StringComparison.Ordinal) ? failure : CommandResult.Fail(failure.Error + note, failure.Data);
        }

        private static JObject Dimensions(EnsureRequest r, Dictionary<SectionDimension, Parameter> parameters) =>
            new JObject(r.Recipe.Dimensions.Select(d => new JProperty(d.Parameter, Mm.FromFeet(parameters[d].AsDouble(), 1))));

        private static JObject Requested(EnsureRequest r) => new JObject(r.Recipe.Dimensions.Select(d => new JProperty(d.Parameter, r.Mm[d])));

        private static JObject With(JObject target, JObject extra)
        {
            if (extra != null) foreach (var prop in extra.Properties()) target[prop.Name] = prop.Value.DeepClone();
            return target;
        }

        /// <summary>The wire result. typeStep: whether the type was found ("existing") or made ("duplicated").</summary>
        internal static JObject Result(EnsureRequest r, string action, string source, Family family, FamilySymbol symbol, JObject dims, string fileName,
            string folder, string typeStep) => new JObject
            {
                ["action"] = action,
                ["dry_run"] = r.DryRun,
                ["category"] = r.Recipe.Key,
                ["family_name"] = family?.Name ?? r.FamilyName,
                ["type_name"] = symbol?.Name ?? r.TypeName,
                ["type_id"] = symbol == null ? null : (JToken)RevitCompat.GetId(symbol.Id),
                ["family_id"] = family == null ? null : (JToken)RevitCompat.GetId(family.Id),
                ["source"] = source,
                ["file"] = fileName,
                ["folder"] = folder,
                ["dimensions_mm"] = dims ?? Requested(r),
                ["material"] = r.Material?.Name,
                ["type_step"] = typeStep
            };
    }
}
