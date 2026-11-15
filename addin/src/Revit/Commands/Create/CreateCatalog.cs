using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Create
{
    /// <summary>Registers the create toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class CreateCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new CreateGroupFromElementsHandler());
            // New in B2M: stairs, railings and roofs.
            dispatcher.Register(new CreateStairHandler());
            dispatcher.Register(new CreateRailingHandler());
            dispatcher.Register(new CreateRoofHandler());
        }
    }
}
