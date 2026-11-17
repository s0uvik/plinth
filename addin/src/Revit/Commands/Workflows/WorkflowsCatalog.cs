using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Workflows
{
    /// <summary>Registers the workflows toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class WorkflowsCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new WorkflowClashReviewHandler());
            dispatcher.Register(new WorkflowModelAuditHandler());
            dispatcher.Register(new WorkflowRoomDocumentationHandler());
            dispatcher.Register(new WorkflowSheetSetHandler());
            dispatcher.Register(new WorkflowDataRoundtripHandler());
            dispatcher.Register(new WorkflowViewCleanupHandler());
            dispatcher.Register(new WorkflowNamingNormalizationHandler());
            dispatcher.Register(new WorkflowTakeoffReportHandler());
        }
    }
}
