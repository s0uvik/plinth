// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/CreateCameraViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for B2M: the 3D view type is the project's default 3D view type through Lookup.TryViewFamilyType
// (rvt-mcp took the first 3D view type found); B2M error codes; rvt-mcp's SavedViewOrientationRefresh
// (Idling/DocumentChanged hooks that re-sync the temporary camera after Undo of a re-aim) is not ported,
// so after an Undo the open window may keep the new camera until the view is reopened.

using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using B2M.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace B2M.Revit.Commands.View
{
    // B2M.Revit.Commands.View is a namespace; this alias keeps "View" meaning the Revit class.
    using View = Autodesk.Revit.DB.View;

    public sealed class CreateCameraViewHandler : IRevitCommand
    {
        public string Name => "create_camera_view";
        public string Description =>
            "Create a perspective 3D camera view named name, looking from eye to target ([x,y,z] in mm, optional up). "
            + "If a perspective 3D view with that name exists it is re-aimed instead. far_clip (default false) keeps far clipping on.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{
            ""name"":{""type"":""string""},
            ""eye"":{""type"":""array"",""items"":{""type"":""number""}},
            ""target"":{""type"":""array"",""items"":{""type"":""number""}},
            ""up"":{""type"":""array"",""items"":{""type"":""number""}},
            ""far_clip"":{""type"":""boolean""}},
            ""required"":[""name"",""eye"",""target""]}";
        public bool ReadOnly => false;

        public CommandResult Execute(UIApplication app, JObject p)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var name = p.Value<string>("name")?.Trim();
            if (string.IsNullOrWhiteSpace(name)) return CommandResult.Fail("INVALID_PARAMS: name cannot be empty.");
            if (!ViewHelpers.TryReadPointMm(p, "eye", true, out var eye, out var failure)) return failure;
            if (!ViewHelpers.TryReadPointMm(p, "target", true, out var target, out failure)) return failure;
            if (!ViewHelpers.TryReadVector(p, "up", false, "", out var up, out failure)) return failure;
            if (!ViewHelpers.TryMakeOrientation(eye, target, up, out var orientation, out failure)) return failure;
            var keepFarClip = p.Value<bool?>("far_clip") ?? false;

            // Case-insensitive, so "entrance camera" re-aims "Entrance Camera". Only 3D views compete for the name.
            var view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (view != null && !view.IsPerspective)
                return CommandResult.Fail("NAME_IN_USE: 3D view '" + view.Name + "' exists and is not a perspective view; pick another name.",
                    new { existingViewId = RevitCompat.GetId(view.Id) });
            if (view != null && view.IsLocked)
                return CommandResult.Fail("VIEW_LOCKED: view '" + view.Name + "' has a locked orientation; unlock it in Revit first.");
            if (view != null && !view.CanSaveOrientation())
                return CommandResult.Fail("CANNOT_SAVE_ORIENTATION: view '" + view.Name + "' cannot save its orientation; choose another name.");

            ViewFamilyType viewType = null;
            if (view == null && !Lookup.TryViewFamilyType(doc, ViewFamily.ThreeDimensional, ElementTypeGroup.ViewType3D, null, out viewType, out failure))
                return failure;

            var created = view == null;
            var before = view?.GetOrientation();
            using (var tx = new Transaction(doc, "B2M: Create Camera View"))
            {
                tx.Start();
                try
                {
                    if (view == null)
                    {
                        view = View3D.CreatePerspective(doc, viewType.Id);
                        view.Name = name;
                    }
                    view.SetOrientation(orientation);
                    view.SaveOrientation();
                    if (!keepFarClip)
                    {
                        // 3D views expose "Far Clip Active"; VIEWER_BOUND_FAR_CLIPPING is the section/elevation setting.
                        var farClip = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
                        if (farClip != null && !farClip.IsReadOnly) farClip.Set(0);
                    }
                    if (tx.Commit() != TransactionStatus.Committed)
                    {
                        if (!created) ViewHelpers.RestoreCamera(view, before);
                        return CommandResult.Fail("NOT_COMMITTED: Revit rolled the transaction back; see failures. Nothing was changed.");
                    }
                }
                catch (Exception ex)
                {
                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                    if (!created) ViewHelpers.RestoreCamera(view, before);
                    return CommandResult.Fail("CREATE_FAILED: could not create camera view '" + name + "': " + ex.Message + " Nothing was changed.");
                }
            }

            // Reported, not assumed, so a view without the parameter is visible to the caller.
            var farClipActive = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
            return CommandResult.Ok(new
            {
                created,
                view = ViewHelpers.DescribeCamera(view),
                far_clip_active = farClipActive == null ? (bool?)null : farClipActive.AsInteger() != 0
            });
        }
    }
}
