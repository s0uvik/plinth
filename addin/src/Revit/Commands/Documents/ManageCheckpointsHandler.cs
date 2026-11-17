// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Documents/ManageCheckpointsHandler.cs and src/shared/Documents/CheckpointStore.cs and
// CheckpointPlanner.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: reads the Plinth checkpoint folder and manifest (see save_checkpoint); keeps list, a dry-run prune
// whose plan_hash must be passed back to delete (deletion re-checks each file's size and time), and restore as a new
// file next to the model that never overwrites anything; the cross-process index lock is not ported; restore's
// next_steps no longer name rvt-mcp tools.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Documents
{
    public sealed class ManageCheckpointsHandler : IRevitCommand
    {
        public string Name => "manage_checkpoints";
        public string Description => "Checkpoints of the active model. action list; prune (keep_last newest non-milestone checkpoints, default 5: "
            + "dry_run defaults to true and returns plan_hash, then call again with dry_run false and that plan_hash to delete); "
            + "restore (checkpoint_id) copies a checkpoint to a new file next to the model, never over an existing one.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""action"":{""type"":""string"",""enum"":[""list"",""prune"",""restore""]},
            ""keep_last"":{""type"":""integer""},
            ""dry_run"":{""type"":""boolean""},
            ""plan_hash"":{""type"":""string""},
            ""checkpoint_id"":{""type"":""string""}},""required"":[""action""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(DocumentsHelpers.NoDocument);
            var modeError = DocumentsHelpers.CheckpointModeError(doc, out _);
            if (modeError != null) return CommandResult.Fail(modeError);

            var model = doc.PathName;
            var folder = DocumentsHelpers.CheckpointFolder(model);
            JObject manifest;
            try
            {
                manifest = DocumentsHelpers.LoadManifest(folder, model);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is Newtonsoft.Json.JsonException)
            {
                return CommandResult.Fail("CHECKPOINT_INDEX_UNREADABLE: " + ex.Message);
            }
            var entries = (JArray)manifest["checkpoints"];
            try
            {
                switch (p.Value<string>("action"))
                {
                    case "list": return List(folder, entries);
                    case "restore": return Restore(model, folder, entries, p.Value<string>("checkpoint_id"));
                    default: return Prune(folder, manifest, entries, p);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return CommandResult.Fail("CHECKPOINT_IO_FAILED: " + ex.Message);
            }
        }

        private static CommandResult List(string folder, JArray entries)
        {
            var tracked = new HashSet<string>(entries.Select(e => e.Value<string>("file")), StringComparer.OrdinalIgnoreCase) { "manifest.json" };
            var untracked = Directory.Exists(folder)
                ? Directory.GetFiles(folder).Select(Path.GetFileName).Where(n => !tracked.Contains(n)).OrderBy(n => n).ToList()
                : new List<string>();
            return CommandResult.Ok(new JObject
            {
                ["folder"] = folder,
                ["checkpoints"] = new JArray(entries.OrderByDescending(e => e.Value<string>("created_utc"), StringComparer.Ordinal)
                    .Select(e => DocumentsHelpers.Entry(e, folder))),
                ["untracked_files"] = new JArray(untracked),
                ["index_status"] = File.Exists(DocumentsHelpers.ManifestPath(folder)) ? "ok" : "not_created"
            });
        }

        private static CommandResult Restore(string model, string folder, JArray entries, string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return CommandResult.Fail("INVALID_PARAMS: restore needs checkpoint_id.");
            var entry = entries.FirstOrDefault(e => e.Value<string>("id") == id);
            if (entry == null)
                return CommandResult.Fail("CHECKPOINT_NOT_FOUND: no checkpoint has id '" + id + "'.",
                    new JObject { ["valid_checkpoint_ids"] = new JArray(entries.Select(e => e.Value<string>("id"))) });
            var source = Path.Combine(folder, entry.Value<string>("file"));
            if (!File.Exists(source)) return CommandResult.Fail("CHECKPOINT_FILE_MISSING: the checkpoint's file is gone.");
            var target = Path.Combine(Path.GetDirectoryName(model),
                Path.GetFileNameWithoutExtension(model) + "-restored-" + id + Path.GetExtension(model));
            if (File.Exists(target) || Directory.Exists(target))
                return CommandResult.Fail("RESTORE_TARGET_EXISTS: " + target + " already exists and is never overwritten.");
            File.Copy(source, target, false);
            return CommandResult.Ok(new JObject
            {
                ["path"] = target,
                ["next_steps"] = new JArray(
                    "The restored copy is a new file; the open model is unchanged.",
                    "To replace the original, the user closes the model in Revit and renames the files by hand.")
            });
        }

        private static CommandResult Prune(string folder, JObject manifest, JArray entries, JObject p)
        {
            var keepLast = p.Value<int?>("keep_last") ?? 5;
            if (keepLast < 1 || keepLast > 100) return CommandResult.Fail("INVALID_PARAMS: keep_last must be between 1 and 100.");
            var plan = Plan(folder, entries, keepLast, out var missing, out var hash);
            if (p.Value<bool?>("dry_run") ?? true)
                return CommandResult.Ok(new JObject
                {
                    ["keep_last"] = keepLast,
                    ["delete"] = new JArray(plan.Select(c => new JObject
                    {
                        ["id"] = c.Id, ["file"] = c.File, ["bytes"] = c.Bytes,
                        ["last_write_utc"] = c.LastWrite.ToString("o", CultureInfo.InvariantCulture)
                    })),
                    ["missing"] = new JArray(missing),
                    ["bytes_to_free"] = plan.Sum(c => c.Bytes),
                    ["plan_hash"] = hash
                });

            var approved = p.Value<string>("plan_hash");
            if (string.IsNullOrWhiteSpace(approved)) return CommandResult.Fail("PLAN_HASH_REQUIRED: run prune with dry_run first and pass its plan_hash.");
            if (!string.Equals(approved, hash, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("PLAN_CHANGED: the checkpoints changed since the dry run; run it again.");

            var deleted = new List<string>();
            var failed = new List<string>();
            foreach (var c in plan)
            {
                var info = new FileInfo(c.Path);
                if (!info.Exists || info.Length != c.Bytes || info.LastWriteTimeUtc != c.LastWrite)
                {
                    failed.Add(c.Id);
                    continue;
                }
                try
                {
                    File.Delete(c.Path);
                    deleted.Add(c.Id);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    failed.Add(c.Id);
                }
            }
            var removed = new HashSet<string>(deleted.Concat(missing));
            foreach (var e in entries.Where(e => removed.Contains(e.Value<string>("id"))).ToList()) e.Remove();
            if (removed.Count > 0) DocumentsHelpers.SaveManifest(folder, manifest);
            var data = new JObject { ["deleted"] = new JArray(deleted), ["missing_removed"] = new JArray(missing), ["failed"] = new JArray(failed) };
            return CommandResult.Ok(data);
        }

        private sealed class Candidate
        {
            public string Id;
            public string File;
            public string Path;
            public long Bytes;
            public DateTime LastWrite;
        }

        private static List<Candidate> Plan(string folder, JArray entries, int keepLast, out List<string> missing, out string hash)
        {
            missing = new List<string>();
            var existing = new List<(JToken entry, Candidate candidate)>();
            foreach (var e in entries)
            {
                var path = Path.Combine(folder, e.Value<string>("file"));
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    missing.Add(e.Value<string>("id"));
                    continue;
                }
                existing.Add((e, new Candidate { Id = e.Value<string>("id"), File = e.Value<string>("file"), Path = path, Bytes = info.Length, LastWrite = info.LastWriteTimeUtc }));
            }
            var plan = existing.Where(x => !(x.entry.Value<bool?>("milestone") ?? false))
                .OrderByDescending(x => x.entry.Value<string>("created_utc"), StringComparer.Ordinal).ThenBy(x => x.candidate.Id, StringComparer.Ordinal)
                .Skip(keepLast).Select(x => x.candidate).ToList();
            var text = new StringBuilder(keepLast.ToString(CultureInfo.InvariantCulture));
            foreach (var c in plan.OrderBy(c => c.Id, StringComparer.Ordinal))
                text.Append('|').Append(c.Id).Append('|').Append(c.Bytes).Append('|').Append(c.LastWrite.Ticks);
            foreach (var id in missing.OrderBy(m => m, StringComparer.Ordinal)) text.Append("|missing:").Append(id);
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
            return plan;
        }
    }
}
