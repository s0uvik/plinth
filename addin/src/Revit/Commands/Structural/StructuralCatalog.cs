using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Structural
{
    /// <summary>Registers the structural toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class StructuralCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new CreateStructuralColumnHandler());
            dispatcher.Register(new CreateStructuralBeamHandler());
            dispatcher.Register(new CreateStructuralWallHandler());
            dispatcher.Register(new CreateFoundationIsolatedHandler());
            dispatcher.Register(new CreateFoundationWallHandler());
            dispatcher.Register(new ListRebarHandler());
            dispatcher.Register(new GetStructuralLoadsHandler());
            dispatcher.Register(new SetStructuralLoadHandler());
            dispatcher.Register(new AnalyzeStructuralConnectionsHandler());
            dispatcher.Register(new TagStructuralFramingHandler());
            dispatcher.Register(new CreateRebarSetHandler());
            dispatcher.Register(new CreateRebarStirrupHandler());
        }
    }
}
