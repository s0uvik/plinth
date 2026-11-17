using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Sheets
{
    /// <summary>Registers the sheets toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class SheetsCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new DuplicateSheetHandler());
            dispatcher.Register(new CreatePlaceholderSheetHandler());
            dispatcher.Register(new ListSheetsHandler());
            dispatcher.Register(new SetTitleblockParametersHandler());
            dispatcher.Register(new GetTitleblockParametersHandler());
            dispatcher.Register(new ListTitleblocksHandler());
            dispatcher.Register(new CreateRevisionHandler());
            dispatcher.Register(new AssignRevisionToSheetHandler());
            dispatcher.Register(new ListRevisionsHandler());
            dispatcher.Register(new RenumberSheetsHandler());
            dispatcher.Register(new GetViewportGeometryHandler());
        }
    }
}
