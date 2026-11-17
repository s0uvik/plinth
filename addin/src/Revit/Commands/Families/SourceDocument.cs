// New in Plinth (Step 10, F1): list_types_in_file and copy_types_from_file read another Revit project or template (.rvt,
// .rte). The file is opened in the background with Application.OpenDocumentFile, never made active, and always closed
// without saving. A file Revit already has open is used as it is and left open. A workshared file is detached with
// every user workset closed (types live in the always-open workset of standards), so the central is never touched.

using System;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Parameters;
using Plinth.Revit.Core.Families;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Families
{
    internal sealed class SourceDocument : IDisposable
    {
        public Document Doc { get; private set; }
        public string Path { get; private set; }

        /// <summary>True when this call opened the file (it is closed again on Dispose).</summary>
        public bool OpenedHere { get; private set; }

        /// <summary>True when the file is the active document itself.</summary>
        public bool IsActive { get; private set; }

        /// <summary>An absolute path to an existing .rte or .rvt file.</summary>
        public static bool TryResolvePath(string raw, out string fullPath, out FileInfo info, out CommandResult failure)
        {
            fullPath = null;
            info = null;
            failure = null;
            var path = (raw ?? "").Trim().Trim('"');
            if (!ParametersHelpers.IsAbsolute(path))
            {
                failure = CommandResult.Fail("INVALID_PATH: path must be an absolute path to a .rte or .rvt file.");
                return false;
            }
            try { fullPath = System.IO.Path.GetFullPath(path); }
            catch (Exception ex)
            {
                failure = CommandResult.Fail("INVALID_PATH: " + ex.Message);
                return false;
            }
            if (!TemplateFiles.IsProjectFile(fullPath))
            {
                failure = CommandResult.Fail("NOT_A_PROJECT_FILE: '" + System.IO.Path.GetFileName(fullPath) + "' is not a .rte or .rvt file "
                    + "(families, .rfa, are loaded with load_family_from_path).");
                return false;
            }
            info = new FileInfo(fullPath);
            if (info.Exists) return true;
            failure = CommandResult.Fail("FILE_NOT_FOUND: no file at '" + fullPath + "' on the Revit machine (list_template_files lists the installed templates).");
            return false;
        }

        public static bool TryOpen(UIApplication app, string fullPath, out SourceDocument source, out CommandResult failure)
        {
            source = null;
            failure = null;
            var active = app.ActiveUIDocument?.Document;
            foreach (Document open in app.Application.Documents)
            {
                if (open.IsFamilyDocument || !SamePath(open.PathName, fullPath)) continue;
                source = new SourceDocument { Doc = open, Path = fullPath, IsActive = active != null && open.Equals(active) };
                return true;
            }

            var hostYear = app.Application.VersionNumber;
            var options = new OpenOptions { Audit = false };
            try
            {
                var info = BasicFileInfo.Extract(fullPath);
                if (int.TryParse(info.Format, out var savedYear) && int.TryParse(hostYear, out var year) && savedYear > year)
                {
                    failure = CommandResult.Fail("FILE_TOO_NEW: '" + System.IO.Path.GetFileName(fullPath) + "' was saved in Revit " + info.Format
                        + "; Revit " + hostYear + " cannot open it.");
                    return false;
                }
                if (info.IsWorkshared)
                {
                    options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                    options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
                }
            }
            catch (Exception ex)
            {
                failure = CommandResult.Fail("HEADER_UNREADABLE: could not read the header of '" + System.IO.Path.GetFileName(fullPath) + "': " + ex.Message);
                return false;
            }

            try
            {
                var doc = app.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(fullPath), options);
                if (doc == null)
                {
                    failure = CommandResult.Fail("OPEN_FAILED: Revit returned no document for '" + System.IO.Path.GetFileName(fullPath) + "'.");
                    return false;
                }
                source = new SourceDocument { Doc = doc, Path = fullPath, OpenedHere = true };
                return true;
            }
            catch (Exception ex)
            {
                failure = CommandResult.Fail("OPEN_FAILED: Revit could not open '" + System.IO.Path.GetFileName(fullPath) + "' in the background: " + ex.Message);
                return false;
            }
        }

        public JObject Describe() => new JObject
        {
            ["path"] = Path,
            ["file_name"] = System.IO.Path.GetFileName(Path),
            ["opened_in_background"] = OpenedHere
        };

        public void Dispose()
        {
            if (!OpenedHere || Doc == null) return;
            try
            {
                if (Doc.IsValidObject) Doc.Close(false);
            }
            catch (Exception ex)
            {
                Core.Logging.AddinLog.Write("Families", "could not close the background document '" + Path + "': " + ex.Message);
            }
            Doc = null;
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'), System.IO.Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
