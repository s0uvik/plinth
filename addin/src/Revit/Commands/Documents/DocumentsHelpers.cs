// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp), files
// src/shared/Handlers/Documents/DocumentLifecycleSupport.cs and src/shared/Documents/DocumentModeClassifier.cs,
// FileVersionStatus.cs, DocumentFormatPolicy.cs, SaveAsPathPolicy.cs, CheckpointIndex.cs, CheckpointPlanner.cs,
// CheckpointNaming.cs and CheckpointFiles.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: one small helper instead of the checkpoint index/lock/planner/registry classes. Checkpoints live
// under %LOCALAPPDATA%\Plinth\checkpoints\<model>-<path hash> with a manifest.json (rvt-mcp wrote a <model>.checkpoints
// folder next to the model); the backup-limit registry and the cross-process index lock are not ported (the add-in
// serves one Revit process and runs commands one at a time on its main thread).

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Plinth.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Documents
{
    internal static class DocumentsHelpers
    {
        public const string NoDocument = "NO_DOCUMENT: no document is open in Revit.";

        /// <summary>cloud, unsaved, family, template, local_file, workshared_local, workshared_central, or null when a workshared central path is unknown.</summary>
        public static string Mode(Document doc)
        {
            if (doc.IsModelInCloud) return "cloud";
            var path = doc.PathName;
            if (string.IsNullOrWhiteSpace(path)) return "unsaved";
            if (doc.IsFamilyDocument) return "family";
            if (path.EndsWith(".rte", StringComparison.OrdinalIgnoreCase)) return "template";
            if (!doc.IsWorkshared) return "local_file";
            string central = null;
            try
            {
                var model = doc.GetWorksharingCentralModelPath();
                if (model != null && !model.Empty) central = ModelPathUtils.ConvertModelPathToUserVisiblePath(model);
            }
            catch
            {
                // Unknown central: handled below.
            }
            if (string.IsNullOrWhiteSpace(central)) return null;
            return PathsEqual(path, central) ? "workshared_central" : "workshared_local";
        }

        public static string ModeError(string mode)
        {
            switch (mode)
            {
                case null: return "WORKSHARED_CENTRAL_UNKNOWN: the central model of this workshared document cannot be determined.";
                case "cloud": return "CLOUD_MODEL_UNSUPPORTED: cloud models are saved and versioned by Autodesk Docs, not by Plinth.";
                case "unsaved": return "DOCUMENT_NEVER_SAVED: this document has no file yet; save it once with save_document and save_as_path.";
                case "template": return "TEMPLATE_SAVE_AS_REQUIRED: this is an opened template (.rte); save it to a new .rvt with save_as_path.";
                case "workshared_central": return "CENTRAL_OPEN_DIRECTLY: this is a central model opened directly; Plinth never saves a central file. Work in a local copy.";
                default: return null;
            }
        }

        public static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static (int saves, Guid guid, string format) DiskVersion(string path)
        {
            using (var info = BasicFileInfo.Extract(path))
            {
                var version = info.GetDocumentVersion();
                return (version.NumberOfSaves, version.VersionGUID, info.Format);
            }
        }

        /// <summary>True when the file on disk was saved by someone else after this session last saved or opened it.</summary>
        public static bool ChangedOnDisk(Document doc, string path) =>
            DiskVersion(path).saves != Document.GetDocumentVersion(doc).NumberOfSaves;

        /// <summary>True, false, or null when the file's format year is unreadable.</summary>
        public static bool? NeedsUpgrade(string format, string hostVersion)
        {
            if (!int.TryParse(format, NumberStyles.None, CultureInfo.InvariantCulture, out var saved)
                || !int.TryParse(hostVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var host)
                || saved < 2000 || saved > 2100) return null;
            return saved < host;
        }

        /// <summary>Null when the path is an absolute path on a local drive letter, else the reason.</summary>
        public static string LocalPathError(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "the path is empty";
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                return "network (UNC) paths are not written";
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
                return "give a full path that starts with a drive letter, such as C:\\Projects\\model.rvt";
            try
            {
                var drive = new DriveInfo(path.Substring(0, 1));
                if (drive.DriveType == DriveType.Network) return "the path is on a mapped network drive";
            }
            catch
            {
                return "the drive does not exist";
            }
            return null;
        }

        public static string CheckpointFolder(string modelPath)
        {
            var full = Path.GetFullPath(modelPath);
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
            var stem = new string(Path.GetFileNameWithoutExtension(full).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return Path.Combine(PlinthPaths.Root, "checkpoints", stem + "-" + hash);
        }

        public static string ManifestPath(string folder) => Path.Combine(folder, "manifest.json");

        public static JObject LoadManifest(string folder, string modelPath)
        {
            var path = ManifestPath(folder);
            if (!File.Exists(path)) return new JObject { ["model_path"] = modelPath, ["checkpoints"] = new JArray() };
            var manifest = JObject.Parse(File.ReadAllText(path));
            if (!(manifest["checkpoints"] is JArray entries)) throw new InvalidDataException("manifest has no checkpoints list");
            foreach (var entry in entries)
            {
                var file = entry.Value<string>("file");
                if (string.IsNullOrEmpty(entry.Value<string>("id")) || string.IsNullOrEmpty(file)
                    || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file == "." || file == "..")
                    throw new InvalidDataException("manifest has an invalid checkpoint entry");
            }
            return manifest;
        }

        /// <summary>Writes the manifest through a temporary file so a crash never leaves half a manifest.</summary>
        public static void SaveManifest(string folder, JObject manifest)
        {
            Directory.CreateDirectory(folder);
            var path = ManifestPath(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, manifest.ToString(Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        /// <summary>Checks that a document can have checkpoints; returns the error or null.</summary>
        public static string CheckpointModeError(Document doc, out string mode)
        {
            mode = Mode(doc);
            if (mode == "workshared_local" || mode == "workshared_central")
                return "CHECKPOINT_WORKSHARED_UNSUPPORTED: checkpoints are for non-workshared models; workshared models keep history in central.";
            if (mode == "template") return "CHECKPOINT_UNSUPPORTED: save the template to a .rvt first.";
            return ModeError(mode);
        }

        public static JObject Entry(JToken entry, string folder)
        {
            var copy = (JObject)entry.DeepClone();
            var file = Path.Combine(folder, entry.Value<string>("file"));
            copy["exists"] = File.Exists(file);
            copy["path"] = file;
            return copy;
        }

        public static long ToLong(JToken token) => token?.Type == JTokenType.Integer ? token.Value<long>() : 0;
    }
}
