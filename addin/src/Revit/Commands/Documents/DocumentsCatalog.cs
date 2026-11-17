using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Documents
{
    /// <summary>Registers the documents toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class DocumentsCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new SaveDocumentHandler());
            dispatcher.Register(new SaveCheckpointHandler());
            dispatcher.Register(new ManageCheckpointsHandler());
        }
    }
}
