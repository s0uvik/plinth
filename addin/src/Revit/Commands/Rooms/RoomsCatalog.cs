using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Rooms
{
    /// <summary>Registers the rooms toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class RoomsCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new GetRoomOpeningsHandler());
            dispatcher.Register(new CreateRoomSeparatorHandler());
            dispatcher.Register(new CreateAreaHandler());
            dispatcher.Register(new CreateSpaceHandler());
            dispatcher.Register(new ListAreasHandler());
            dispatcher.Register(new ComputeRoomFinishesHandler());
            dispatcher.Register(new TagAllAreasHandler());
        }
    }
}
