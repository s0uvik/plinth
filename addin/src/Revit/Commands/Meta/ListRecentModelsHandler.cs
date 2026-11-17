// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Documents/ListRecentModelsHandler.cs and src/shared/Documents/RecentFileList.cs, commit b44ee0b.
// Licensed under the Apache License, Version 2.0.
// Modified for Plinth: Revit.ini is found through Application.CurrentUsersDataFolderPath (rvt-mcp built the path from the
// plugin's year); the parser lives in this file; the file is opened read-only and shared so Revit can keep writing it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Meta
{
    public sealed class ListRecentModelsHandler : IRevitCommand
    {
        private const int MaxEntries = 50;
        private const string Section = "[Recent File List]";

        public string Name => "list_recent_models";
        public string Description => "List this Revit's recent models (from Revit.ini, read-only) without opening any. "
            + "Ask the user which one to open, then call open_model with that path and confirmed=true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var iniPath = Path.Combine(app.Application.CurrentUsersDataFolderPath, "Revit.ini");
            var year = app.Application.VersionNumber;
            if (!File.Exists(iniPath))
                return CommandResult.Ok(new JObject
                {
                    ["revit_year"] = year, ["ini_found"] = false, ["count"] = 0, ["files"] = new JArray(),
                    ["instruction"] = "No recent files were found. Ask the user for the full path of the model; do not guess one."
                });

            string text;
            try
            {
                using (var stream = new FileStream(iniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, true))
                    text = reader.ReadToEnd();
            }
            catch (Exception)
            {
                return CommandResult.Fail("RECENT_LIST_UNREADABLE: could not read Revit.ini for Revit " + year + ".");
            }

            var files = new JArray(Parse(text).Select(entry => new JObject
            {
                ["index"] = entry.Key,
                ["path"] = entry.Value,
                ["name"] = SafeFileName(entry.Value),
                ["exists"] = SafeExists(entry.Value)
            }));
            return CommandResult.Ok(new JObject
            {
                ["revit_year"] = year,
                ["ini_found"] = true,
                ["count"] = files.Count,
                ["files"] = files,
                ["instruction"] = files.Count == 0
                    ? "No recent files were found. Ask the user for the full path of the model; do not guess one."
                    : "Ask the user which model to open, then call open_model with its path and confirmed=true. Do not open one before the user chooses."
            });
        }

        /// <summary>Entries File1=..., File2=... under [Recent File List]; gaps allowed; File1 is the most recent.</summary>
        internal static IEnumerable<KeyValuePair<int, string>> Parse(string ini)
        {
            var found = new Dictionary<int, string>();
            var inSection = false;
            using (var reader = new StringReader(ini ?? ""))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed[0] == ';') continue;
                    if (trimmed[0] == '[')
                    {
                        inSection = string.Equals(trimmed, Section, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!inSection) continue;
                    var eq = trimmed.IndexOf('=');
                    if (eq <= 4) continue;
                    var key = trimmed.Substring(0, eq).Trim();
                    var value = trimmed.Substring(eq + 1).Trim();
                    if (value.Length == 0 || !key.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;
                    if (int.TryParse(key.Substring(4), out var index) && index > 0) found[index] = value;
                }
            }
            return found.OrderBy(p => p.Key).Take(MaxEntries).ToList();
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch (ArgumentException) { return path; }
        }

        private static bool SafeExists(string path)
        {
            try { return File.Exists(path); }
            catch { return false; }
        }
    }
}
