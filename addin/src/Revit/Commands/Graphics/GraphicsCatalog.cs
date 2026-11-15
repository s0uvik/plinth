using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Graphics
{
    /// <summary>Registers the graphics toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class GraphicsCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            // view filters
            dispatcher.Register(new CreateViewFilterHandler());
            dispatcher.Register(new ApplyFilterToViewHandler());
            dispatcher.Register(new SetFilterOverridesHandler());
            dispatcher.Register(new ListViewFiltersHandler());
            dispatcher.Register(new RemoveFilterFromViewHandler());
            // element overrides and visibility
            dispatcher.Register(new OverrideElementGraphicsHandler());
            dispatcher.Register(new ClearElementOverridesHandler());
            dispatcher.Register(new GetViewVisibilityHandler());
            dispatcher.Register(new SetCategoryVisibilityHandler());
            // phases
            dispatcher.Register(new ListPhasesHandler());
            dispatcher.Register(new SetViewPhaseHandler());
            dispatcher.Register(new SetElementPhaseHandler());
        }
    }
}
