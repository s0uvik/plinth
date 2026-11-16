// New in B2M: the ADAPT step of ensure_family_type. When no family has the asked name, a related family of the same
// category is used instead: one whose name has every word group of the recipe (structural_framing: Concrete +
// Rectangular/Rect + Beam; structural_column: Concrete + Rectangular/Rect + Column), loaded in the project first,
// then on disk in search order (metric M_ names first). A loaded one whose types already carry b/h (or Width/Depth)
// as length type parameters just gets the new type. A file on disk is first opened with OpenDocumentFile to confirm
// its category and parameters, closed without saving, then loaded. When the family itself would need editing (b/h
// or the material are instance parameters), the original is never modified: a copy (EditFamily for a loaded one,
// the opened file otherwise) has those parameters made type parameters and is saved as <family_name>.rfa in
// %LOCALAPPDATA%\B2M\families, closed, and loaded. Every family document opened here is closed again.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Core.Logging;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    internal static class FamilyAdapter
    {
        /// <summary>Opening a family file is slow; at most this many disk candidates are opened per call.</summary>
        private const int MaxFilesOpened = 5;

        private sealed class Inspection
        {
            public string Problem;
            public readonly List<FamilyParameter> ToMakeType = new List<FamilyParameter>();
            public readonly List<string> Edits = new List<string>();
            public bool NeedsEdit => ToMakeType.Count > 0;
        }

        /// <summary>The adapted result, a failure that must stop the command, or null when no related family fits.</summary>
        public static CommandResult TryAdapt(UIApplication app, EnsureRequest r, List<FamilyFileSearch.Hit> files, JArray rejected)
        {
            var families = new FilteredElementCollector(r.Doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var related = families.Where(f => !f.IsInPlace && f.FamilyCategory != null && f.FamilyCategory.BuiltInCategory == r.Recipe.Category
                    && r.Recipe.IsRelatedName(f.Name))
                .OrderBy(f => Rank(f.Name)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var family in related)
            {
                var result = TryLoaded(r, family, rejected);
                if (result != null) return result;
            }

            var loadedNames = new HashSet<string>(families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            var rootOrder = files.Select(h => h.Root).Distinct().ToList();
            var candidates = files.Select((hit, index) => new { hit, index })
                .Where(x => r.Recipe.IsRelatedName(x.hit.FamilyName) && !loadedNames.Contains(x.hit.FamilyName))
                .OrderBy(x => rootOrder.IndexOf(x.hit.Root)).ThenBy(x => Rank(x.hit.FamilyName)).ThenBy(x => x.index)
                .Select(x => x.hit).ToList();
            for (var i = 0; i < candidates.Count; i++)
            {
                if (i >= MaxFilesOpened)
                {
                    foreach (var skipped in candidates.Skip(i)) Reject(rejected, skipped.FamilyName, skipped, "not opened: at most " + MaxFilesOpened
                        + " family files are opened per call");
                    break;
                }
                var result = TryDocument(r, candidates[i].FamilyName, () => app.Application.OpenDocumentFile(candidates[i].Path), candidates[i], rejected);
                if (result != null) return result;
            }
            return null;
        }

        /// <summary>Metric (M_) names first, then the shorter (plainer) name.</summary>
        private static int Rank(string name) => (name.StartsWith("M_", StringComparison.OrdinalIgnoreCase) ? 0 : 100000) + name.Length;

        private static CommandResult TryLoaded(EnsureRequest r, Family family, JArray rejected)
        {
            var first = EnsureFamilyTypeHandler.TypesOf(family).FirstOrDefault();
            if (first == null)
            {
                Reject(rejected, family.Name, null, "it has no types");
                return null;
            }
            if (EnsureFamilyTypeHandler.TypeCanCarry(r, first))
                return EnsureFamilyTypeHandler.EnsureInLoaded(r, family, "project", Extra(r, family.Name, null));
            if (!family.IsEditable)
            {
                Reject(rejected, family.Name, null, "its types have no b/h (or material) type parameters and the family cannot be edited");
                return null;
            }
            return TryDocument(r, family.Name, () => r.Doc.EditFamily(family), null, rejected);
        }

        /// <summary>
        /// Opens the candidate (a copy of a loaded family, or a file), checks it, and when it needs editing saves an edited
        /// copy as family_name. hit is null for a loaded family. The document is always closed before anything is loaded.
        /// </summary>
        private static CommandResult TryDocument(EnsureRequest r, string sourceName, Func<Document> open, FamilyFileSearch.Hit hit, JArray rejected)
        {
            Document doc = null;
            Inspection inspection;
            string copyPath = null;
            try
            {
                try
                {
                    doc = open();
                }
                catch (Exception ex)
                {
                    Reject(rejected, sourceName, hit, "Revit could not open it: " + ex.Message);
                    return null;
                }
                inspection = Inspect(doc, r);
                if (inspection.Problem == null && hit == null && !inspection.NeedsEdit)
                    inspection.Problem = "its b/h (or material) parameters are not visible on its types in the project";
                if (inspection.Problem != null)
                {
                    Reject(rejected, sourceName, hit, inspection.Problem);
                    return null;
                }
                if (inspection.NeedsEdit && !r.DryRun)
                {
                    var failure = SaveEditedCopy(doc, inspection, r, sourceName, out copyPath);
                    if (failure != null) return failure;
                }
            }
            finally
            {
                Close(doc);
            }

            var folder = FamilyFileSearch.B2MFamiliesFolder;
            var extra = Extra(r, sourceName, inspection.NeedsEdit ? inspection.Edits : null);
            if (inspection.NeedsEdit)
            {
                if (r.DryRun)
                    return CommandResult.Ok(Merge(EnsureFamilyTypeHandler.Result(r, "adapted", "copy", null, null, null, r.FamilyName + ".rfa", folder,
                        "duplicated"), extra));
                return EnsureFamilyTypeHandler.LoadOwnFile(r, copyPath, "adapted", "copy", folder, extra);
            }
            if (r.DryRun)
            {
                extra["family_name"] = hit.FamilyName;
                return CommandResult.Ok(Merge(EnsureFamilyTypeHandler.Result(r, "adapted", "search", null, null, null, hit.FileName, hit.Folder,
                    "duplicated"), extra));
            }
            return EnsureFamilyTypeHandler.LoadAndEnsure(r, hit.Path, "adapted", "search", hit.FileName, hit.Folder, false, extra);
        }

        /// <summary>Category, b/h-like length parameters and (if asked) a material parameter; which of them are instance parameters.</summary>
        private static Inspection Inspect(Document doc, EnsureRequest r)
        {
            var inspection = new Inspection();
            if (!doc.IsFamilyDocument)
            {
                inspection.Problem = "it is not a family";
                return inspection;
            }
            var category = doc.OwnerFamily?.FamilyCategory;
            if (category == null || category.BuiltInCategory != r.Recipe.Category)
            {
                inspection.Problem = "its category is " + (category?.Name ?? "none") + ", not " + r.Recipe.Key;
                return inspection;
            }
            var parameters = doc.FamilyManager.Parameters.Cast<FamilyParameter>().Where(p => p.Definition != null).ToList();
            foreach (var d in r.Recipe.Dimensions)
            {
                var parameter = d.Names.Select(n => Find(parameters, n)).FirstOrDefault(p => p != null && FamilyBuilder.IsLength(p));
                if (parameter == null)
                {
                    inspection.Problem = "it has no length parameter named " + string.Join(" or ", d.Names);
                    return inspection;
                }
                if (!string.IsNullOrEmpty(parameter.Formula))
                {
                    inspection.Problem = "its parameter " + parameter.Definition.Name + " is computed by a formula";
                    return inspection;
                }
                if (!MakeTypeIfInstance(inspection, parameter)) return inspection;
            }
            if (r.Material == null) return inspection;
            var materials = parameters.Where(p => SpecTypeId.Reference.Material.Equals(p.Definition.GetDataType())).ToList();
            var material = Find(materials, FamilyFactory.MaterialParameterName) ?? (materials.Count == 1 ? materials[0] : null);
            if (material == null)
            {
                inspection.Problem = materials.Count == 0 ? "it has no material parameter for material_name"
                    : "it has several material parameters and none is named " + FamilyFactory.MaterialParameterName;
                return inspection;
            }
            MakeTypeIfInstance(inspection, material);
            return inspection;
        }

        private static bool MakeTypeIfInstance(Inspection inspection, FamilyParameter parameter)
        {
            if (!parameter.IsInstance) return true;
            if (FamilyBuilder.IsBuiltIn(parameter))
            {
                inspection.Problem = "its parameter " + parameter.Definition.Name + " is a built-in instance parameter";
                return false;
            }
            inspection.ToMakeType.Add(parameter);
            inspection.Edits.Add(parameter.Definition.Name + ": instance parameter made a type parameter");
            return true;
        }

        private static FamilyParameter Find(List<FamilyParameter> parameters, string name) =>
            parameters.FirstOrDefault(p => p.Definition.Name == name)
            ?? parameters.FirstOrDefault(p => string.Equals(p.Definition.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Edits the opened copy and saves it as family_name in B2M's folder. Returns a failure, or null with the path.</summary>
        private static CommandResult SaveEditedCopy(Document doc, Inspection inspection, EnsureRequest r, string sourceName, out string path)
        {
            var fileName = r.FamilyName + ".rfa";
            path = Path.Combine(FamilyFileSearch.B2MFamiliesFolder, fileName);
            if (File.Exists(path))
            {
                path = null;
                return CommandResult.Fail("FILE_EXISTS: " + fileName + " already exists in the B2M families folder but was not found by the search "
                    + "(the folder scan was cut short). Load it with load_family_from_path or remove it." + EnsureFamilyTypeHandler.Unchanged);
            }
            var step = "edit the copy";
            try
            {
                using (var tx = new Transaction(doc, "B2M: Adapt family copy"))
                {
                    tx.Start();
                    foreach (var parameter in inspection.ToMakeType) doc.FamilyManager.MakeType(parameter);
                    if (tx.Commit() != TransactionStatus.Committed)
                        return Failed(sourceName, step, "Revit rolled the edit back (see failures)", ref path);
                }
                step = "save the copy";
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = false });
                return null;
            }
            catch (Exception ex)
            {
                return Failed(sourceName, step, ex.Message, ref path);
            }
        }

        private static CommandResult Failed(string sourceName, string step, string message, ref string path)
        {
            var kept = path != null && !EnsureFamilyTypeHandler.DeleteQuietly(path);
            path = null;
            return CommandResult.Fail("ADAPT_FAILED: making a copy of '" + sourceName + "' failed at step '" + step + "': " + message + "."
                + EnsureFamilyTypeHandler.Unchanged + (kept ? " A partial copy could not be deleted from the B2M families folder." : ""),
                new JObject { ["step"] = step, ["adapted_from"] = sourceName });
        }

        private static void Close(Document doc)
        {
            if (doc == null) return;
            try
            {
                if (doc.IsValidObject && !doc.Close(false)) AddinLog.Write("Families", "Revit refused to close family document " + doc.Title + ".");
            }
            catch (Exception ex)
            {
                AddinLog.Write("Families", "Could not close a family document: " + ex.Message);
            }
        }

        private static JObject Extra(EnsureRequest r, string sourceName, List<string> edits) => new JObject
        {
            ["adapted_from"] = sourceName,
            ["requested_family_name"] = r.FamilyName,
            ["copied"] = edits != null,
            ["copy_edits"] = edits == null ? null : new JArray(edits)
        };

        private static JObject Merge(JObject target, JObject extra)
        {
            foreach (var prop in extra.Properties()) target[prop.Name] = prop.Value.DeepClone();
            return target;
        }

        private static void Reject(JArray rejected, string familyName, FamilyFileSearch.Hit hit, string reason) =>
            rejected.Add(new JObject
            {
                ["family_name"] = familyName,
                ["source"] = hit == null ? "project" : "disk",
                ["file_name"] = hit?.FileName,
                ["folder"] = hit?.Folder,
                ["reason"] = reason
            });
    }
}
