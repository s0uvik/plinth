using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Parameters
{
    /// <summary>Registers the parameters toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class ParametersCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new ListSharedParametersHandler());
            dispatcher.Register(new CreateSharedParameterHandler());
            dispatcher.Register(new BindSharedParameterHandler());
            dispatcher.Register(new CreateProjectParameterHandler());
            dispatcher.Register(new ListProjectParameterBindingsHandler());
            dispatcher.Register(new RemoveParameterBindingHandler());
            dispatcher.Register(new ExportSharedParameterFileHandler());
            dispatcher.Register(new SetParameterValueByGuidHandler());
        }
    }
}
