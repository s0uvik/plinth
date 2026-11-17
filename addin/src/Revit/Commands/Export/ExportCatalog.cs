using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Export
{
    /// <summary>Registers the export toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class ExportCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new ExportRoomDataHandler());
            dispatcher.Register(new ExportDwgHandler());
            dispatcher.Register(new ExportDgnHandler());
            dispatcher.Register(new ExportDwfHandler());
            dispatcher.Register(new ExportIfcHandler());
            dispatcher.Register(new ExportNwcHandler());
            dispatcher.Register(new ExportFbxHandler());
            dispatcher.Register(new ExportGbxmlHandler());
            dispatcher.Register(new ExportImageHandler());
            dispatcher.Register(new ExportScheduleCsvHandler());
            dispatcher.Register(new ExportElementsDataHandler());
            dispatcher.Register(new BatchExportSheetsHandler());
            dispatcher.Register(new ListExportSettingsHandler());
            dispatcher.Register(new CreateViewSheetSetHandler());
            dispatcher.Register(new GetPrintSettingsHandler());
        }
    }
}
