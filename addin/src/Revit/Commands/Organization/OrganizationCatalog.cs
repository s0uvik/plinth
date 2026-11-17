using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Organization
{
    /// <summary>Registers the organization toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class OrganizationCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            // view templates
            dispatcher.Register(new ListViewTemplatesHandler());
            dispatcher.Register(new CreateViewTemplateFromViewHandler());
            dispatcher.Register(new ApplyViewTemplateHandler());
            dispatcher.Register(new DuplicateViewTemplateHandler());
            dispatcher.Register(new DeleteViewTemplateHandler());
            // saved selections and UI selection
            dispatcher.Register(new SaveSelectionHandler());
            dispatcher.Register(new LoadSelectionHandler());
            dispatcher.Register(new ListSavedSelectionsHandler());
            dispatcher.Register(new DeleteSavedSelectionHandler());
            dispatcher.Register(new SelectElementsHandler());
        }
    }
}
