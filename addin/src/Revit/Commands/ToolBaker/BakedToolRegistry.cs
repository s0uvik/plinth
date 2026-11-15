// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/ToolBaker/BakedToolRegistry.cs and src/shared/ToolBaker/BakedToolRecord.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for B2M: the registry is a folder of JSON files (%LOCALAPPDATA%\B2M\baked-tools\<name>.json), one per
// tool, in the format the B2M service writes when a bake suggestion is accepted (name, description, parameters_schema,
// code, source, created_utc). It is read fresh on every call. SQLite storage, compiled DLL caching, usage stats,
// lifecycle states, compat maps and version history are not ported; suggestions live in the service.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using B2M.Revit.Core;
using B2M.Revit.Core.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.ToolBaker
{
    internal sealed class BakedTool
    {
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>JSON Schema subset checked by SchemaValidator against run_baked_tool's params.</summary>
        public JObject ParametersSchema { get; set; }

        public string Code { get; set; }
        public string FileName { get; set; }

        /// <summary>Where the tool came from, e.g. "suggestion 1a2b3c4d5e6f" (written by the service); may be null.</summary>
        public string Source { get; set; }

        public string CreatedUtc { get; set; }

        /// <summary>Null when the file is usable; otherwise why it is not.</summary>
        public string Problem { get; set; }
    }

    /// <summary>
    /// File format, one tool per file named after the tool (written by service/b2m/gateway.py or by hand):
    /// {"name": "tag_doors", "description": "...", "parameters_schema": {"type": "object", "properties": {...}},
    ///  "code": "...", "source": "...", "created_utc": "..."}. The code follows the send_code_to_revit contract.
    /// The service's bookkeeping files in the same folder (suggested-*.json, .dismissed.json) are not tools.
    /// </summary>
    internal static class BakedToolRegistry
    {
        public const string FolderHint = "%LOCALAPPDATA%\\B2M\\baked-tools";
        private const long MaxFileBytes = 1024 * 1024;
        private static readonly Regex ValidName = new Regex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

        public static string Folder => Path.Combine(B2MPaths.Root, "baked-tools");

        public static bool IsValidName(string name) => name != null && ValidName.IsMatch(name);

        public static IReadOnlyList<BakedTool> LoadAll()
        {
            if (!Directory.Exists(Folder)) return Array.Empty<BakedTool>();
            return Directory.GetFiles(Folder, "*.json")
                .Where(p => IsToolFile(Path.GetFileName(p)))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(Read)
                .ToList();
        }

        private static bool IsToolFile(string fileName) =>
            !fileName.StartsWith(".", StringComparison.Ordinal) && !fileName.StartsWith("suggested-", StringComparison.OrdinalIgnoreCase);

        /// <summary>The tool named <paramref name="name"/>, or null if no file holds it.</summary>
        public static BakedTool Find(string name)
        {
            if (!IsValidName(name)) return null;
            var path = Path.Combine(Folder, name + ".json");
            return File.Exists(path) ? Read(path) : null;
        }

        private static BakedTool Read(string path)
        {
            var fileName = Path.GetFileName(path);
            var expected = Path.GetFileNameWithoutExtension(path);
            var tool = new BakedTool { Name = expected, FileName = fileName };
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) return Broken(tool, "file is over 1 MB.");
                var json = ParseObject(File.ReadAllText(path));
                tool.Name = json.Value<string>("name") ?? expected;
                tool.Description = json.Value<string>("description") ?? "";
                tool.Code = json.Value<string>("code");
                tool.ParametersSchema = json["parameters_schema"] as JObject ?? new JObject { ["type"] = "object" };
                tool.Source = json["source"]?.Type == JTokenType.String ? json.Value<string>("source") : null;
                tool.CreatedUtc = json["created_utc"]?.Type == JTokenType.String ? json.Value<string>("created_utc") : null;

                if (!IsValidName(tool.Name)) return Broken(tool, "name must be 1-64 letters, digits or underscores.");
                if (!string.Equals(tool.Name, expected, StringComparison.OrdinalIgnoreCase)) return Broken(tool, "name does not match the file name.");
                if (string.IsNullOrWhiteSpace(tool.Code)) return Broken(tool, "code is missing.");
                if (json["parameters_schema"] != null && !(json["parameters_schema"] is JObject)) return Broken(tool, "parameters_schema must be an object.");
                return tool;
            }
            catch (JsonException ex)
            {
                return Broken(tool, "not valid JSON: " + ex.Message);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Broken(tool, "could not be read: " + ErrorSanitizer.Sanitize(ex.Message));
            }
        }

        /// <summary>Parses without turning ISO date strings (created_utc) into DateTime tokens.</summary>
        internal static JObject ParseObject(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
            {
                var token = JToken.ReadFrom(reader);
                return token as JObject ?? throw new JsonReaderException("the file must hold one JSON object.");
            }
        }

        private static BakedTool Broken(BakedTool tool, string problem)
        {
            tool.Problem = problem;
            return tool;
        }
    }
}
