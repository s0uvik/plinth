// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/ExportFamilyToPathHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the family is saved into a private staging folder and then moved to the exact output path
// (Export/OutputFile), so Revit's backup copies never land next to the target and an existing file is replaced only
// with overwrite_existing; the output folder is created when missing; non-editable families are refused before
// EditFamily; failures are errors instead of success with exported=false; family_id is an integer.

using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Export;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class ExportFamilyToPathHandler : IRevitCommand
    {
        public string Name => "export_family_to_path";
        public string Description =>
            "Save a loadable family from the project as an .rfa file at output_path (absolute; folder created if missing). "
            + "An existing file is replaced only with overwrite_existing: true. The project is not changed.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{" + FamiliesHelpers.FamilySchema + @",
            ""output_path"":{""type"":""string""},
            ""overwrite_existing"":{""type"":""boolean""}},""required"":[""output_path""]}";
        // Writes a file, so not read-only although the model is unchanged.
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var overwrite = p.Value<bool?>("overwrite_existing") ?? false;
            var outputPath = p.Value<string>("output_path");
            var ext = Path.GetExtension(outputPath ?? "");
            if (!string.IsNullOrEmpty(ext) && !string.Equals(ext, ".rfa", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("INVALID_PATH: output_path must end in .rfa.");
            if (!OutputFile.TryResolve(outputPath, null, null, ".rfa", overwrite, out var target, out var failure)) return failure;
            if (!FamiliesHelpers.TryFamily(doc, p.Value<long?>("family_id"), p.Value<string>("family_name"), out var family, out failure))
                return failure;
            if (family.IsInPlace)
                return CommandResult.Fail("FAMILY_IS_INPLACE: '" + family.Name + "' is an in-place family and cannot be saved as an .rfa file.");
            if (!family.IsEditable)
                return CommandResult.Fail("FAMILY_NOT_EDITABLE: Revit does not allow '" + family.Name + "' to be opened for editing, so it cannot be saved.");

            Document familyDoc = null;
            string staging = null;
            try
            {
                familyDoc = doc.EditFamily(family);
                staging = OutputFile.NewStagingFolder(target);
                familyDoc.SaveAs(Path.Combine(staging, Path.GetFileName(target)), new SaveAsOptions { OverwriteExistingFile = false });
                familyDoc.Close(false);
                familyDoc = null;
                OutputFile.Deliver(staging, "*.rfa", target, overwrite);
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("EXPORT_FAILED: could not save family '" + family.Name + "': " + ex.Message);
            }
            finally
            {
                if (familyDoc != null)
                {
                    try { familyDoc.Close(false); }
                    catch { /* best effort: the copy opened by EditFamily is never saved back */ }
                }
                OutputFile.Cleanup(staging);
            }

            return CommandResult.Ok(new JObject
            {
                ["exported"] = true,
                ["family_id"] = RevitCompat.GetId(family.Id),
                ["family_name"] = family.Name,
                ["output_path"] = target,
                ["file_size_bytes"] = new FileInfo(target).Length
            });
        }
    }
}
