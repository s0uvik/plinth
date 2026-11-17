// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Documents/SaveDocumentHandler.cs, DocumentLifecycleSupport.cs (Prepare, Save, SaveAsNew) and
// src/shared/Documents/DocumentSavePolicy.cs and SaveAsPathPolicy.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: the same safety checks (cloud, central, template, changed on disk, format upgrade, read-only file,
// save-as only for documents without a file and never over an existing file) in one handler; the backup-limit registry
// that re-saved models with 20 backups is not ported, so a normal save keeps the model's own backup setting;
// FailureControl.ThrowIfStopped is replaced by re-reading the save count; family documents may save as .rfa.

using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Documents
{
    public sealed class SaveDocumentHandler : IRevitCommand
    {
        private const int NewFileBackups = 20;

        public string Name => "save_document";
        public string Description => "Save the active document to its own file (local and family files, or a workshared local copy without syncing). "
            + "A document that has no file yet (new, or an opened template) is saved once with save_as_path, which must not exist. "
            + "Saving an older-format file upgrades it, which needs allow_format_upgrade: true.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""allow_format_upgrade"":{""type"":""boolean""},
            ""save_as_path"":{""type"":""string""}}}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail(DocumentsHelpers.NoDocument);
            var mode = DocumentsHelpers.Mode(doc);
            var saveAs = p.Value<string>("save_as_path")?.Trim().Trim('"');
            return saveAs != null ? SaveAsNew(app, doc, mode, saveAs) : Save(app, doc, mode, p.Value<bool?>("allow_format_upgrade") ?? false);
        }

        private static CommandResult Save(UIApplication app, Document doc, string mode, bool allowUpgrade)
        {
            var modeError = DocumentsHelpers.ModeError(mode);
            if (modeError != null) return CommandResult.Fail(modeError);
            var path = doc.PathName;
            if (!File.Exists(path)) return CommandResult.Fail("FILE_MISSING_ON_DISK: the model's file is no longer on disk; nothing was saved.");
            var before = Document.GetDocumentVersion(doc).NumberOfSaves;
            if (!doc.IsModified)
                return CommandResult.Ok(new { saved = false, reason = "no_changes", path, mode, number_of_saves = before });

            var disk = DocumentsHelpers.DiskVersion(path);
            if (disk.saves != before)
                return CommandResult.Fail("FILE_CHANGED_ON_DISK: the file was saved elsewhere after this session opened it; saving would overwrite that work.");
            var upgrade = DocumentsHelpers.NeedsUpgrade(disk.format, app.Application.VersionNumber);
            if (upgrade == null) return CommandResult.Fail("FILE_FORMAT_UNKNOWN: the file's Revit version cannot be read; nothing was saved.");
            if (upgrade.Value && !allowUpgrade)
                return CommandResult.Fail("FORMAT_UPGRADE_NEEDS_USER: the file is in Revit " + disk.format + " format and saving upgrades it to "
                    + app.Application.VersionNumber + " for good. Ask the user, then pass allow_format_upgrade: true.");
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                return CommandResult.Fail("FILE_READ_ONLY: the model's file is read-only; nothing was saved.");

            doc.Save();
            var after = Document.GetDocumentVersion(doc).NumberOfSaves;
            if (after == before)
                return CommandResult.Fail("NOT_SAVED: Revit did not save the document (an add-in or the user may have cancelled the save).");
            return CommandResult.Ok(new
            {
                saved = true,
                path,
                mode,
                upgraded_from = upgrade.Value ? disk.format : null,
                number_of_saves = after,
                note = mode == "workshared_local" ? "Saved the local copy only; changes are not synchronized with central." : null
            });
        }

        private static CommandResult SaveAsNew(UIApplication app, Document doc, string mode, string target)
        {
            if (mode != "unsaved" && mode != "template")
                return CommandResult.Fail("SAVE_AS_NOT_ALLOWED: save_as_path is only for a document that has no file yet; this one saves to its own file. "
                    + "Copies of a saved model are checkpoints (save_checkpoint).");
            var pathError = DocumentsHelpers.LocalPathError(target);
            if (pathError != null) return CommandResult.Fail("SAVE_AS_PATH_INVALID: " + pathError + ".");
            var extension = doc.IsFamilyDocument ? ".rfa" : ".rvt";
            if (!target.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return CommandResult.Fail("SAVE_AS_PATH_INVALID: the file name must end in " + extension + ".");
            var path = Path.GetFullPath(target);
            foreach (Document open in app.Application.Documents)
                if (!open.Equals(doc) && !string.IsNullOrEmpty(open.PathName) && DocumentsHelpers.PathsEqual(open.PathName, path))
                    return CommandResult.Fail("SAVE_AS_PATH_IN_USE: another open document uses that file.");
            if (!Directory.Exists(Path.GetDirectoryName(path)))
                return CommandResult.Fail("SAVE_AS_FOLDER_MISSING: the folder does not exist on the Revit machine.");
            if (File.Exists(path) || Directory.Exists(path))
                return CommandResult.Fail("SAVE_AS_TARGET_EXISTS: something already exists at save_as_path and is never overwritten; choose a new name.");

            using (var options = new SaveAsOptions { OverwriteExistingFile = false, MaximumBackups = NewFileBackups })
                doc.SaveAs(path, options);
            if (!DocumentsHelpers.PathsEqual(doc.PathName ?? "", path))
                return CommandResult.Fail("NOT_SAVED: Revit did not save the document to save_as_path (the save may have been cancelled).");
            return CommandResult.Ok(new
            {
                saved = true,
                saved_as = true,
                source_mode = mode,
                path = doc.PathName,
                mode = DocumentsHelpers.Mode(doc),
                maximum_backups = NewFileBackups,
                number_of_saves = Document.GetDocumentVersion(doc).NumberOfSaves
            });
        }
    }
}
