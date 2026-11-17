// Derived from rvt-mcp by bimwright (https://github.com/bimwright/rvt-mcp),
// file src/shared/Handlers/Views/GetCurrentViewHandler.cs, commit b44ee0b. Licensed under the Apache License, Version 2.0.
// Modified for Plinth: namespace and command interface only. Plinth clients use it as the
// document-confirm probe: its response carries _target.document_id.

using Autodesk.Revit.UI;
using Plinth.Revit.Bridge;
using Newtonsoft.Json.Linq;

namespace Plinth.Revit.Commands.Query
{
    public sealed class GetCurrentViewInfoHandler : IRevitCommand
    {
        public string Name => "get_current_view_info";
        public string Description => "Read the active view's name, type, level, scale, detail level and display style.";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool ReadOnly => true;

        public CommandResult Execute(UIApplication app, JObject parameters)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) return CommandResult.Fail("NO_DOCUMENT: no document is open in Revit.");

            var view = doc.ActiveView;
            if (view == null) return CommandResult.Fail("NO_ACTIVE_VIEW: the document has no active view.");

            return CommandResult.Ok(new
            {
                viewId = RevitCompat.GetId(view.Id),
                viewName = view.Name,
                viewType = view.ViewType.ToString(),
                levelName = view.GenLevel?.Name,
                levelId = RevitCompat.GetIdOrNull(view.GenLevel?.Id),
                scale = view.Scale,
                detailLevel = view.DetailLevel.ToString(),
                displayStyle = view.DisplayStyle.ToString()
            });
        }
    }
}
