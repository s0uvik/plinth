// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Documents/SaveCheckpointHandler.cs and src/shared/Documents/CheckpointSpace.cs, CheckpointNaming.cs
// and CheckpointFiles.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: a checkpoint never saves the user's model. rvt-mcp saved a modified model first; Plinth refuses with
// DOCUMENT_MODIFIED and asks for save_document, so only save_document ever writes the model file. The verified copy
// (same size and same save count/version GUID as the source) goes to %LOCALAPPDATA%\Plinth\checkpoints\... with a
// manifest.json, not next to the model; the index lock is not ported.

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Documents
{
    public sealed class SaveCheckpointHandler : IRevitCommand
    {
        public string Name => "save_checkpoint";
        public string Description => "Copy the active model's saved file to a labelled checkpoint under %LOCALAPPDATA%\\Plinth\\checkpoints. "
            + "The model must have no unsaved changes (call save_document first); the model file itself is never written. "
            + "milestone: true protects the checkpoint from pruning. Not for workshared or cloud models.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""label"":{""type"":""string""},
            ""reason"":{""type"":""string""},
            ""milestone"":{""type"":""boolean""}},""required"":[""label""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(DocumentsHelpers.NoDocument);
            var label = p.Value<string>("label")?.Trim();
            var reason = p.Value<string>("reason");
            if (string.IsNullOrEmpty(label) || label.Length > 80) return CommandResult.Fail("INVALID_PARAMS: label must have 1 to 80 characters.");
            if (reason != null && reason.Length > 500) return CommandResult.Fail("INVALID_PARAMS: reason must have at most 500 characters.");
            var modeError = DocumentsHelpers.CheckpointModeError(doc, out _);
            if (modeError != null) return CommandResult.Fail(modeError);

            var source = doc.PathName;
            if (!File.Exists(source)) return CommandResult.Fail("FILE_MISSING_ON_DISK: the model's file is no longer on disk.");
            if (doc.IsModified)
                return CommandResult.Fail("DOCUMENT_MODIFIED: the model has unsaved changes and a checkpoint copies the saved file. "
                    + "Call save_document first (with the user's agreement), then save_checkpoint.");
            var sourceVersion = DocumentsHelpers.DiskVersion(source);
            if (sourceVersion.saves != Document.GetDocumentVersion(doc).NumberOfSaves)
                return CommandResult.Fail("FILE_CHANGED_ON_DISK: the file on disk is not the version open in Revit.");

            var folder = DocumentsHelpers.CheckpointFolder(source);
            JObject manifest;
            try
            {
                manifest = DocumentsHelpers.LoadManifest(folder, source);
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is Newtonsoft.Json.JsonException)
            {
                return CommandResult.Fail("CHECKPOINT_INDEX_UNREADABLE: " + ex.Message);
            }

            var size = new FileInfo(source).Length;
            try
            {
                var free = new DriveInfo(Path.GetPathRoot(folder)).AvailableFreeSpace;
                if ((decimal)free * 10 < (decimal)size * 11) return CommandResult.Fail("INSUFFICIENT_DISK_SPACE: not enough free space for a copy of the model.");
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                return CommandResult.Fail("FREE_SPACE_UNAVAILABLE: " + ex.Message);
            }

            var entries = (JArray)manifest["checkpoints"];
            var created = DateTimeOffset.Now;
            var stem = created.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var extension = Path.GetExtension(source).ToLowerInvariant();
            string id = null, file = null, target = null;
            for (var suffix = 1; suffix < 1000 && target == null; suffix++)
            {
                id = suffix == 1 ? stem : stem + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                file = id + extension;
                var candidate = Path.Combine(folder, file);
                if (entries.Any(e => e.Value<string>("id") == id) || File.Exists(candidate)) continue;
                try
                {
                    File.Copy(source, candidate, false);
                    target = candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // Taken between the check and the copy; try the next name.
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return CommandResult.Fail("CHECKPOINT_COPY_FAILED: " + ex.Message);
                }
            }
            if (target == null) return CommandResult.Fail("CHECKPOINT_COPY_FAILED: no free checkpoint name.");

            try
            {
                var attributes = File.GetAttributes(target);
                if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                var copied = DocumentsHelpers.DiskVersion(target);
                if (new FileInfo(target).Length != size || copied.saves != sourceVersion.saves || copied.guid != sourceVersion.guid)
                    throw new InvalidDataException("the copy does not match the model file");
            }
            catch (Exception ex)
            {
                TryDelete(target);
                return CommandResult.Fail("CHECKPOINT_VERIFY_FAILED: " + ex.Message + " The copy was removed.");
            }

            var entry = new JObject
            {
                ["id"] = id,
                ["file"] = file,
                ["label"] = label,
                ["reason"] = reason,
                ["milestone"] = p.Value<bool?>("milestone") ?? false,
                ["created_utc"] = created.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["bytes"] = size,
                ["number_of_saves"] = sourceVersion.saves,
                ["version_guid"] = sourceVersion.guid.ToString(),
                ["revit_year"] = app.Application.VersionNumber,
                ["user"] = app.Application.Username ?? ""
            };
            entries.Add(entry);
            try
            {
                DocumentsHelpers.SaveManifest(folder, manifest);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                TryDelete(target);
                return CommandResult.Fail("CHECKPOINT_INDEX_WRITE_FAILED: " + ex.Message + " The copy was removed.");
            }

            var nonMilestones = entries.Count(e => !(e.Value<bool?>("milestone") ?? false));
            return CommandResult.Ok(new JObject
            {
                ["checkpoint"] = DocumentsHelpers.Entry(entry, folder),
                ["folder"] = folder,
                ["totals"] = new JObject { ["count"] = entries.Count, ["total_bytes"] = entries.Sum(e => DocumentsHelpers.ToLong(e["bytes"])) },
                ["prune_hint"] = nonMilestones > 5 ? "More than 5 non-milestone checkpoints; preview pruning with manage_checkpoints if the user approves." : null
            });
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Reported by the caller's error; a stray copy is harmless and shows up as untracked in manage_checkpoints.
            }
        }
    }
}
