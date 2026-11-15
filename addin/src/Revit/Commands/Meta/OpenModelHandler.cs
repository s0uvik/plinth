// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Documents/OpenModelHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: opens only an absolute path the user confirmed (confirmed=true); errors name the file, never the
// full path. A workshared central model, a file whose header cannot be read, or a workshared model from another Revit
// year is refused instead of creating a new local copy (rvt-mcp's Create New Local, Revit.ini ProjectPath lookup and
// local-file renaming are not ported; the user opens or creates the local in Revit). The worksets option is not ported.
// Cannot run inside batch_execute (BatchExecutor). Opening never saves the model.

using System;
using System.IO;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.Meta
{
    public sealed class OpenModelHandler : IRevitCommand
    {
        private static readonly Regex Absolute = new Regex(@"^(?:[A-Za-z]:\\|\\\\[^\\]+\\[^\\]+\\)", RegexOptions.CultureInvariant);

        public string Name => "open_model";
        public string Description => "Open a Revit model (.rvt), template (.rte) or family (.rfa) by absolute path, after the user "
            + "confirmed that exact file (confirmed=true). Workshared central models are refused: open a local copy instead. Never saves.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""path"":{""type"":""string"",""description"":""Absolute path on the machine Revit runs on""},
            ""confirmed"":{""type"":""boolean"",""description"":""True only when the user chose this exact file""},
            ""activate"":{""type"":""boolean"",""description"":""Make it the active document (default true)""},
            ""audit"":{""type"":""boolean"",""description"":""Audit while opening; slow (default false)""}},
            ""required"":[""path"",""confirmed""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            if (parameters.Value<bool>("confirmed") != true)
                return CommandResult.Fail("USER_CONFIRMATION_REQUIRED: ask the user which file to open (list_recent_models helps), "
                    + "then call again with confirmed=true. Nothing was opened.");

            var path = (parameters.Value<string>("path") ?? "").Trim().Trim('"');
            var activate = parameters.Value<bool?>("activate") ?? true;
            var audit = parameters.Value<bool?>("audit") ?? false;

            if (!Absolute.IsMatch(path))
                return CommandResult.Fail("INVALID_PATH: path must be absolute (C:\\... or \\\\server\\share\\...). Nothing was opened.");
            string fileName;
            try
            {
                path = Path.GetFullPath(path);
                fileName = Path.GetFileName(path);
            }
            catch (Exception)
            {
                return CommandResult.Fail("INVALID_PATH: the path is not a valid file path. Nothing was opened.");
            }

            var extension = Path.GetExtension(path);
            if (!IsRevitFile(extension))
                return CommandResult.Fail("NOT_A_REVIT_FILE: '" + fileName + "' is not .rvt, .rte or .rfa. Nothing was opened.");
            if (!File.Exists(path))
                return CommandResult.Fail("FILE_NOT_FOUND: no file '" + fileName + "' at that path, as seen from the Revit machine. Nothing was opened.");

            BasicFileInfo info;
            try
            {
                info = BasicFileInfo.Extract(path);
            }
            catch (Exception)
            {
                return CommandResult.Fail("HEADER_UNREADABLE: could not read the header of '" + fileName + "', so whether it is workshared is unknown. "
                    + "It was left closed; ask the user to open it in Revit.");
            }

            var hostYear = app.Application.VersionNumber;
            var savedIn = info.Format;
            if (info.IsWorkshared)
            {
                if (info.IsCentral || !info.IsLocal)
                    return CommandResult.Fail("WORKSHARED_CENTRAL: '" + fileName + "' is a workshared central model. B2M never opens a central "
                        + "directly; ask the user to create or open their local copy, then open that file. Nothing was opened.");
                if (!string.IsNullOrEmpty(savedIn) && savedIn != hostYear)
                    return CommandResult.Fail("WORKSHARED_UPGRADE_NEEDS_USER: '" + fileName + "' is a workshared local saved in Revit " + savedIn
                        + "; opening it in Revit " + hostYear + " would upgrade it. Ask the user. Nothing was opened.");
            }

            foreach (Document open in app.Application.Documents)
            {
                if (!SamePath(open.PathName, path)) continue;
                if (open.IsLinked)
                    return CommandResult.Fail("LOADED_AS_LINK: '" + fileName + "' is loaded as a link in an open document, so Revit cannot open it "
                        + "on its own. Unload the link or close the host first.");
                return AlreadyOpen(app, open, activate, savedIn);
            }

            try
            {
                var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                var options = new OpenOptions { Audit = audit };
                Document document;
                string activeView = null;
                if (activate)
                {
                    var uidoc = app.OpenAndActivateDocument(modelPath, options, false);
                    document = uidoc?.Document;
                    activeView = uidoc?.ActiveView?.Name;
                }
                else
                {
                    document = app.Application.OpenDocumentFile(modelPath, options);
                }
                if (document == null)
                    return CommandResult.Fail("OPEN_CANCELLED: Revit did not return an opened document for '" + fileName + "'.");

                return CommandResult.Ok(new JObject
                {
                    ["opened"] = true,
                    ["was_already_open"] = false,
                    ["title"] = document.Title,
                    ["file_name"] = fileName,
                    ["saved_in_version"] = savedIn,
                    ["upgraded_from"] = !string.IsNullOrEmpty(savedIn) && savedIn != hostYear ? savedIn : null,
                    ["is_workshared"] = document.IsWorkshared,
                    ["is_family"] = document.IsFamilyDocument,
                    ["activated"] = activate,
                    ["active_view"] = activeView,
                    ["note"] = activate
                        ? "This is now the active document. Confirm it (its document_id is in _target of the next read) before writing."
                        : "Opened in the background with no view; it is NOT the active document, so commands will not see it."
                });
            }
            catch (Exception ex)
            {
                return CommandResult.Fail("OPEN_FAILED: Revit could not open '" + fileName + "': " + ex.Message);
            }
        }

        private static CommandResult AlreadyOpen(UIApplication app, Document open, bool activate, string savedIn)
        {
            var fileName = Path.GetFileName(open.PathName);
            var activated = false;
            if (activate)
            {
                activated = SamePath(app.ActiveUIDocument?.Document?.PathName, open.PathName);
                if (!activated)
                {
                    try
                    {
                        // Re-opening an open document only activates it.
                        var uidoc = app.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(open.PathName), new OpenOptions(), false);
                        activated = uidoc?.Document != null && SamePath(uidoc.Document.PathName, open.PathName);
                    }
                    catch (Exception ex)
                    {
                        return CommandResult.Fail("ACTIVATE_FAILED: '" + fileName + "' is already open but could not be activated: " + ex.Message);
                    }
                    if (!activated)
                        return CommandResult.Fail("ACTIVATE_FAILED: Revit did not activate the already-open '" + fileName + "'.");
                }
            }
            return CommandResult.Ok(new JObject
            {
                ["opened"] = false,
                ["was_already_open"] = true,
                ["activated"] = activated,
                ["title"] = open.Title,
                ["file_name"] = fileName,
                ["saved_in_version"] = savedIn,
                ["is_workshared"] = open.IsWorkshared,
                ["is_family"] = open.IsFamilyDocument
            });
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsRevitFile(string extension) =>
            string.Equals(extension, ".rvt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".rte", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".rfa", StringComparison.OrdinalIgnoreCase);
    }
}
