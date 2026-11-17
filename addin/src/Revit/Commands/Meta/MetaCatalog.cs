using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Meta
{
    /// <summary>Registers the meta toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class MetaCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new GetDocumentStateHandler());
            dispatcher.Register(new ListRecentModelsHandler());
            dispatcher.Register(new OpenModelHandler());
            dispatcher.Register(new ShowMessageHandler());
            dispatcher.Register(new SetProjectInfoHandler());
            dispatcher.Register(new PurgeUnusedHandler());
            dispatcher.Register(new ReportTaskResultHandler());
            dispatcher.Register(new GetReportHistoryHandler());
            dispatcher.Register(new SendCodeToRevitHandler());
        }
    }
}
