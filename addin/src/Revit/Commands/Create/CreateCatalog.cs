using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Create
{
    /// <summary>Registers the create toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class CreateCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new CreateGroupFromElementsHandler());
            // New in Plinth: stairs, railings and roofs.
            dispatcher.Register(new CreateStairHandler());
            dispatcher.Register(new CreateRailingHandler());
            dispatcher.Register(new CreateRoofHandler());
            // New in Plinth (Step 9 T7c): structural foundation slabs from a foundation slab type (ensure_floor_type foundation: true).
            dispatcher.Register(new CreateFoundationSlabHandler());
        }
    }
}
