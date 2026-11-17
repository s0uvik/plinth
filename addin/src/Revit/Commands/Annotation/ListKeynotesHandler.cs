// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Annotations/ListKeynotesHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: keynotes are read from the keynote table Revit has loaded (KeynoteTable.GetKeyBasedTreeEntries)
// instead of re-reading and parsing the keynote file from disk, so no file path is touched or echoed back; when
// the table is empty the keynote values set on family types are listed, as before, marked by source; limit must be
// 1 to 1000.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Annotation
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class ListKeynotesHandler : IRevitCommand
    {
        public string Name => "list_keynotes";
        public string Description =>
            "List keynotes from the project's loaded keynote table (key, text, parent key, full path). When no table is loaded, "
            + "lists keynote values set on family types (source tells which). Optional key_prefix, search (key or text), limit (default 200, max 1000).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""key_prefix"":{""type"":""string""},
            ""search"":{""type"":""string""},
            ""limit"":{""type"":""integer""}}}";
        public bool ReadOnly => true;

        private sealed class Entry
        {
            public string Key;
            public string Text;
            public string ParentKey;
        }

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");
            if (!AnnotationHelpers.TryLimit(p, 200, 1000, out var limit, out var failure)) return failure;
            var keyPrefix = p.Value<string>("key_prefix") ?? "";
            var search = p.Value<string>("search") ?? "";

            var entries = new List<Entry>();
            var warnings = new List<string>();
            var source = "keynote_table";
            long? tableId = null;
            try
            {
                var table = KeynoteTable.GetKeynoteTable(doc);
                if (table != null)
                {
                    tableId = RevitCompat.GetId(table.Id);
                    foreach (var entry in table.GetKeyBasedTreeEntries())
                    {
                        if (string.IsNullOrEmpty(entry.Key)) continue;
                        entries.Add(new Entry
                        {
                            Key = entry.Key,
                            Text = (entry as KeynoteEntry)?.KeynoteText ?? "",
                            ParentKey = entry.ParentKey ?? ""
                        });
                    }
                    if (entries.Count == 0) warnings.Add("The keynote table is empty or its file is not loaded.");
                }
                else warnings.Add("No keynote table is loaded in the document.");
            }
            catch (Exception ex)
            {
                warnings.Add("The keynote table could not be read: " + ex.Message);
            }

            if (entries.Count == 0)
            {
                source = "family_type_keynote_values";
                var seen = new HashSet<string>();
                foreach (var symbol in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
                {
                    var value = symbol.get_Parameter(BuiltInParameter.KEYNOTE_PARAM)?.AsString()?.Trim();
                    if (!string.IsNullOrEmpty(value) && seen.Add(value))
                        entries.Add(new Entry { Key = value, Text = symbol.FamilyName + ": " + symbol.Name, ParentKey = "" });
                }
            }

            var parents = new Dictionary<string, string>();
            foreach (var e in entries) parents[e.Key] = e.ParentKey;

            var matches = entries
                .Where(e => keyPrefix.Length == 0 || e.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(e => search.Length == 0 || e.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                    || e.Text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            return CommandResult.Ok(new JObject
            {
                ["source"] = source,
                ["keynote_table_id"] = tableId,
                ["total_matches"] = matches.Count,
                ["returned"] = Math.Min(matches.Count, limit),
                ["truncated"] = matches.Count > limit,
                ["keynotes"] = new JArray(matches.Take(limit).Select(e => new JObject
                {
                    ["key"] = e.Key,
                    ["text"] = e.Text,
                    ["parent_key"] = e.ParentKey,
                    ["full_path"] = new JArray(FullPath(e.Key, parents))
                })),
                ["warnings"] = new JArray(warnings)
            });
        }

        private static List<string> FullPath(string key, Dictionary<string, string> parents)
        {
            var path = new List<string>();
            var current = key;
            for (var guard = 0; guard < 15 && !string.IsNullOrEmpty(current); guard++)
            {
                path.Insert(0, current);
                if (!parents.TryGetValue(current, out var parent) || string.IsNullOrEmpty(parent) || parent == current) break;
                current = parent;
            }
            return path;
        }
    }
}
