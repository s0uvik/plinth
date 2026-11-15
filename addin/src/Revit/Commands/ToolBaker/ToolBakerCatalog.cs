using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.ToolBaker
{
    /// <summary>Registers the toolbaker toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class ToolBakerCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new ListBakedToolsHandler());
            dispatcher.Register(new RunBakedToolHandler());
        }
    }
}
