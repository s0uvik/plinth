// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/SetViewOrientationHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: view lookup through ViewHelpers (a shared name is an error with the options); Plinth error
// codes; rvt-mcp's SavedViewOrientationRefresh (Idling/DocumentChanged hooks that re-sync Revit's temporary
// camera after Undo/Redo of a saved orientation) is not ported, so after an Undo the open window may keep
// showing the new camera until the view is reopened; the saved orientation itself is undone correctly.

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.View
{
    // Plinth.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class SetViewOrientationHandler : IRevitCommand
    {
        public string Name => "set_view_orientation";
        public string Description =>
            "Aim a 3D view's camera (view_id or view_name, default the active view) from eye to target, both [x,y,z] in mm, "
            + "with optional up [x,y,z] (default +Z). perspective true/false toggles the projection where Revit allows. "
            + "fit (default true) frames the model: the crop box is turned off and the open window zooms to fit. "
            + "On the default {3D} view only the temporary camera moves (not saved, not undoable).";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""view_id"":{""type"":""integer""},
            ""view_name"":{""type"":""string""},
            ""eye"":{""type"":""array"",""items"":{""type"":""number""}},
            ""target"":{""type"":""array"",""items"":{""type"":""number""}},
            ""up"":{""type"":""array"",""items"":{""type"":""number""}},
            ""perspective"":{""type"":""boolean""},
            ""fit"":{""type"":""boolean""}},
            ""required"":[""eye"",""target""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var uidoc = app.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            if (!ViewHelpers.TryResolveView(uidoc, p.Value<long?>("view_id"), p.Value<string>("view_name"), true, out var resolved, out var failure))
                return failure;
            var view = (View3D)resolved;
            if (view.IsLocked)
                return CommandResult.Fail("VIEW_LOCKED: view '" + view.Name + "' has a locked orientation; unlock it in Revit first.");

            if (!ViewHelpers.TryReadPointMm(p, "eye", true, out var eye, out failure)) return failure;
            if (!ViewHelpers.TryReadPointMm(p, "target", true, out var target, out failure)) return failure;
            if (!ViewHelpers.TryReadVector(p, "up", false, "", out var up, out failure)) return failure;
            if (!ViewHelpers.TryMakeOrientation(eye, target, up, out var orientation, out failure)) return failure;

            // Revit keeps no saved orientation for the default 3D view ({3D}, or {3D - user} when workshared):
            // only its temporary camera moves, like an orbit, and Undo has nothing to restore.
            var saveOrientation = view.CanSaveOrientation();
            var perspective = p.Value<bool?>("perspective");
            var fitArg = p.Value<bool?>("fit");
            var fit = fitArg ?? true;
            // A named view may be on a sheet, where turning its crop box off resizes the viewport. Without an
            // explicit fit, only the default 3D view loses its crop box.
            var cropOff = fitArg ?? !saveOrientation;
            var notes = new List<string>();
            var before = view.GetOrientation();

            using (var tx = new Transaction(doc, "Plinth: Set View Orientation"))
            {
                tx.Start();
                try
                {
                    if (perspective == true && !view.IsPerspective)
                    {
                        if (!view.CanToggleBetweenPerspectiveAndIsometric())
                        {
                            tx.RollBack();
                            return CommandResult.Fail("CANNOT_TOGGLE: view '" + view.Name + "' cannot be toggled to perspective; use create_camera_view instead.");
                        }
                        view.ToggleToPerspective();
                    }
                    else if (perspective == false && view.IsPerspective)
                    {
                        if (view.CanToggleBetweenPerspectiveAndIsometric()) view.ToggleToIsometric();
                        else notes.Add("The view stays perspective (Revit cannot toggle it to isometric).");
                    }

                    if (saveOrientation)
                    {
                        view.SetOrientation(orientation);
                        view.SaveOrientation();
                    }
                    if (!view.IsPerspective && view.CropBoxActive)
                    {
                        if (cropOff)
                        {
                            // A crop box sized for the earlier orientation would clip the new framing.
                            view.CropBoxActive = false;
                            notes.Add("Crop box turned off so the whole model is framed.");
                        }
                        else if (fitArg == null)
                        {
                            notes.Add("Crop box kept on this named view; pass fit=true to turn it off and frame the whole model.");
                        }
                    }

                    if (tx.Commit() != TransactionStatus.Committed)
                    {
                        if (saveOrientation) ViewHelpers.RestoreCamera(view, before);
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. The orientation was not changed.");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    if (saveOrientation) ViewHelpers.RestoreCamera(view, before);
                    return CommandResult.Fail("ORIENT_FAILED: could not orient '" + view.Name + "': " + ex.Message + " Nothing was changed.");
                }
            }

            if (!saveOrientation)
            {
                // Moved only after the commit, so a rolled-back toggle or crop change never leaves it aimed.
                view.SetOrientation(orientation);
                notes.Add("Default 3D view: only the temporary camera moved. It is not saved in the model and Undo does not restore it; "
                    + "use a named 3D view or create_camera_view to keep a camera.");
            }
            var zoomed = fit && !view.IsPerspective && ViewHelpers.TryZoomToFit(uidoc, view);
            if (fit && !view.IsPerspective && !zoomed)
                notes.Add("The view is not open in Revit's window, so zoom to fit was skipped; an image export of an uncropped 3D view still fits the model.");
            return CommandResult.Ok(new
            {
                view = ViewHelpers.DescribeCamera(view),
                orientation_saved = saveOrientation,
                zoomed_to_fit = zoomed,
                notes
            });
        }
    }
}
