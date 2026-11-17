using System;
using System.IO;
using System.Linq;
using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Export
{
    /// <summary>
    /// Exact output paths for Revit exports. Revit names export files itself (it appends view names, or
    /// reuses names already in the folder), so exports run into a private staging folder inside the
    /// target folder and the one expected file is then moved to the exact path asked for. Nothing
    /// outside the target folder is written, and an existing file is replaced only with overwrite.
    /// (Replaces rvt-mcp's CaptureOutputPath / CaptureOutputResolver and its folder before/after diff.)
    /// </summary>
    internal static class OutputFile
    {
        /// <summary>From either an absolute path, or an absolute folder plus a bare file name. Adds the extension when missing.</summary>
        public static bool TryResolve(string path, string folder, string fileName, string extension, bool overwrite,
            out string fullPath, out CommandResult failure)
        {
            fullPath = null;
            failure = null;
            var hasPath = !string.IsNullOrEmpty(path);
            var hasFolder = !string.IsNullOrEmpty(folder) || !string.IsNullOrEmpty(fileName);
            if (hasPath == hasFolder)
                return Fail("INVALID_PARAMS: give either output_path, or output_folder and file_name.", out failure);
            if (hasFolder && (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(fileName)))
                return Fail("INVALID_PARAMS: output_folder and file_name go together.", out failure);
            if (hasPath)
            {
                folder = Path.GetDirectoryName(path);
                fileName = Path.GetFileName(path);
            }
            if (!IsAbsolute(folder))
                return Fail("INVALID_PATH: the output folder must be an absolute path such as C:\\out.", out failure);
            if (!IsBareFileName(fileName))
                return Fail("INVALID_PATH: the file name must be a plain name without folders, '..' or characters Windows forbids.", out failure);
            if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) fileName += extension;

            try
            {
                fullPath = Path.Combine(Path.GetFullPath(folder), fileName);
            }
            catch (Exception ex)
            {
                return Fail("INVALID_PATH: " + ex.Message, out failure);
            }
            if (Directory.Exists(fullPath))
                return Fail("INVALID_PATH: " + fullPath + " is a folder.", out failure);
            if (File.Exists(fullPath) && !overwrite)
                return Fail("FILE_EXISTS: " + fullPath + " already exists; pass overwrite: true to replace it.", out failure);
            return true;
        }

        /// <summary>A new empty staging folder next to the target (the target folder is created when missing).</summary>
        public static string NewStagingFolder(string target)
        {
            var folder = Path.GetDirectoryName(target);
            Directory.CreateDirectory(folder);
            var staging = Path.Combine(folder, ".plinth-staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            return staging;
        }

        /// <summary>Moves the single file Revit wrote into the staging folder to the target path.</summary>
        public static void Deliver(string staging, string pattern, string target, bool overwrite)
        {
            var produced = Directory.GetFiles(staging, pattern, SearchOption.TopDirectoryOnly);
            if (produced.Length != 1)
                throw new InvalidOperationException("Revit wrote " + produced.Length + " file(s) where one was expected"
                    + (produced.Length == 0 ? "." : ": " + string.Join(", ", produced.Select(Path.GetFileName)) + "."));
            if (File.Exists(target))
            {
                if (!overwrite) throw new IOException(target + " appeared during the export; it was left alone.");
                File.Delete(target);
            }
            File.Move(produced[0], target);
        }

        public static void Cleanup(string staging)
        {
            try
            {
                if (staging != null && Directory.Exists(staging)) Directory.Delete(staging, true);
            }
            catch
            {
                // A leftover staging folder is harmless; the next export uses a new one.
            }
        }

        private static bool IsAbsolute(string folder) =>
            !string.IsNullOrEmpty(folder)
            && ((folder.Length >= 3 && char.IsLetter(folder[0]) && folder[1] == ':' && (folder[2] == '\\' || folder[2] == '/'))
                || folder.StartsWith(@"\\", StringComparison.Ordinal));

        private static bool IsBareFileName(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && name != "." && name != ".."
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0
            && name.Trim() == name
            && !name.EndsWith(".", StringComparison.Ordinal);

        private static bool Fail(string message, out CommandResult failure)
        {
            failure = CommandResult.Fail(message);
            return false;
        }
    }
}
