// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Families/LoadFamilyFromPathHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: failures are errors ("CODE: message") instead of success with loaded=false; the path must be
// an absolute .rfa path; overwrite_existing now defaults to false, so a family already in the project is reported
// as FAMILY_ALREADY_LOADED with its id instead of being silently reloaded; ids are integers; the reflection
// fallback for Family.Symbols is gone.
// Duplicate before edit (TypeEditGuard): reloading a loaded family replaces every one of its types, so
// overwrite_existing also needs confirm_edit_all = the family's exact number of placed instances (FAMILY_IN_USE
// otherwise, also when it has none: then confirm_edit_all: 0). No copy is offered: load the file under another name.
// Overwrite is only passed to Revit for the family matched by name and confirmed; any other family Revit would
// overwrite is refused as already loaded.

using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using B2M.Revit.Commands.Parameters;
using B2M.Revit.Core.Safety;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Families
{
    public sealed class LoadFamilyFromPathHandler : IRevitCommand
    {
        public string Name => "load_family_from_path";
        public string Description =>
            "Load an .rfa family file (absolute path) into the active document. A family already loaded is reloaded only with "
            + "overwrite_existing: true (overwrite_parameter_values also resets instance values). Returns the family id and its types. "
            + "Reloading changes every type of the loaded family, so overwrite_existing also needs confirm_edit_all: N, N being the exact "
            + "number of its placed instances (the FAMILY_IN_USE refusal reports it).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""path"":{""type"":""string""},
            ""overwrite_existing"":{""type"":""boolean""},
            ""overwrite_parameter_values"":{""type"":""boolean""},
            ""include_symbols"":{""type"":""boolean""},
            ""max_symbol_results"":{""type"":""integer""},
            " + SharedEditRule.ConfirmOnlySchemaField + @"},""required"":[""path""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var path = p.Value<string>("path");
            var overwrite = p.Value<bool?>("overwrite_existing") ?? false;
            var overwriteValues = p.Value<bool?>("overwrite_parameter_values") ?? false;
            var includeSymbols = p.Value<bool?>("include_symbols") ?? false;
            var maxSymbols = p.Value<int?>("max_symbol_results") ?? 200;
            if (maxSymbols < 1 || maxSymbols > 1000) return CommandResult.Fail("INVALID_PARAMS: max_symbol_results must be 1 to 1000.");
            if (!ParametersHelpers.IsAbsolute(path) || !string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("INVALID_PATH: path must be an absolute path to a .rfa file such as C:\\families\\Door.rfa.");
            try { path = Path.GetFullPath(path); }
            catch (Exception ex) { return CommandResult.Fail("INVALID_PATH: " + ex.Message); }
            if (!File.Exists(path)) return CommandResult.Fail("FILE_NOT_FOUND: " + path + " does not exist.");

            // A reload changes every type of the loaded family: it needs the exact instance count, checked before anything changes.
            var loaded = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase));
            int? reloadedInstances = null;
            if (overwrite && loaded != null)
            {
                if (!TypeEditGuard.TryConfirmFamilyReload(doc, p, loaded, out var count, out var refusal)) return refusal;
                reloadedInstances = count;
            }
            else if (p[SharedEditRule.ConfirmField] != null)
                return CommandResult.Fail("INVALID_PARAMS: confirm_edit_all applies only to reloading a loaded family (overwrite_existing: true). Nothing was changed.");

            var options = new FamilyLoadOptions(overwrite && reloadedInstances.HasValue, overwriteValues);
            Family family = null;
            using (var tx = new Transaction(doc, "B2M: Load family"))
            {
                tx.Start();
                try
                {
                    var ok = doc.LoadFamily(path, options, out family);
                    if (!ok || family == null)
                    {
                        if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                        var existing = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                            .FirstOrDefault(f => string.Equals(f.Name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase));
                        if (options.FamilyWasFound || existing != null)
                            return CommandResult.Fail("FAMILY_ALREADY_LOADED: the family is already in the project"
                                + (reloadedInstances.HasValue ? " and Revit found nothing to reload." : "; pass overwrite_existing: true with confirm_edit_all (its instance count) to reload it."),
                                existing == null ? null : new JObject { ["family_id"] = RevitCompat.GetId(existing.Id), ["family_name"] = existing.Name });
                        return CommandResult.Fail("LOAD_FAILED: Revit refused to load " + path + " (not a family file, or saved in a newer Revit).");
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures.");
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    return CommandResult.Fail("LOAD_FAILED: could not load the family: " + ex.Message);
                }
            }

            var symbols = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>()
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var result = new JObject
            {
                ["loaded"] = true,
                ["family_id"] = RevitCompat.GetId(family.Id),
                ["family_name"] = family.Name,
                ["category"] = family.FamilyCategory?.Name,
                ["builtInCategory"] = Lookup.BuiltInName(family.FamilyCategory),
                ["kind"] = FamiliesHelpers.Kind(family),
                ["symbol_count"] = symbols.Count,
                ["was_overwrite"] = options.FamilyWasFound,
                ["reloaded_instance_count"] = reloadedInstances
            };
            if (includeSymbols)
            {
                result["symbols_truncated"] = symbols.Count > maxSymbols;
                result["symbols"] = new JArray(symbols.Take(maxSymbols).Select(s => new JObject
                {
                    ["id"] = RevitCompat.GetId(s.Id),
                    ["name"] = s.Name
                }));
            }
            return CommandResult.Ok(result);
        }
    }
}
