// New in Plinth (Step 10, F1): Revit 2026 installs no local family library, so the fit-out's families and MEP system
// types come from the project templates Revit installs. This lists them for the running Revit year (or another
// installed year), per language folder, so the caller can pick one for list_types_in_file. It only reads the disk.

using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    public sealed class ListTemplateFilesHandler : IRevitCommand
    {
        public string Name => "list_template_files";
        public string Description =>
            "List the project templates (.rte) Revit installed under %PROGRAMDATA%\\Autodesk\\RVT <year>\\Templates, per language folder "
            + "(e.g. English: Systems-Default_Metric.rte, Plumbing-, Electrical-, Mechanical-Default_Metric.rte, DefaultMetric.rte). "
            + "year defaults to the running Revit; language filters to one folder. Pass a file's path to list_types_in_file to see its types.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""year"":{""type"":""integer"",""description"":""Revit year, default the running one""},
            ""language"":{""type"":""string"",""description"":""Language folder such as English or English-Imperial; omit for all""}}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var year = p.Value<int?>("year")?.ToString() ?? app.Application.VersionNumber;
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var root = TemplateFiles.Root(programData, year);
            var files = TemplateFiles.List(root, p.Value<string>("language"));

            var installedYears = new JArray();
            try
            {
                var autodesk = Path.Combine(programData, "Autodesk");
                if (Directory.Exists(autodesk))
                    foreach (var dir in Directory.GetDirectories(autodesk, "RVT *").OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                        if (Directory.Exists(Path.Combine(dir, "Templates"))) installedYears.Add(Path.GetFileName(dir).Substring(4));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // The year list is a convenience; the requested year's listing stands on its own.
            }

            return CommandResult.Ok(new JObject
            {
                ["year"] = year,
                ["running_year"] = app.Application.VersionNumber,
                ["templates_folder"] = root,
                ["exists"] = Directory.Exists(root),
                ["installed_years"] = installedYears,
                ["languages"] = new JArray(files.Select(f => f.Language).Distinct().ToArray()),
                ["count"] = files.Count,
                ["files"] = new JArray(files.Select(f => new JObject
                {
                    ["name"] = f.Name,
                    ["path"] = f.Path,
                    ["language"] = f.Language,
                    ["size_bytes"] = f.SizeBytes,
                    ["modified_utc"] = f.ModifiedUtc.ToString("o")
                }))
            });
        }
    }
}
