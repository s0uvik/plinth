using B2M.Revit.Bridge;
using B2M.Revit.Commands.Create;
using B2M.Revit.Commands.Delete;
using B2M.Revit.Commands.Lint;
using B2M.Revit.Commands.Meta;
using B2M.Revit.Commands.Query;
using B2M.Revit.Commands.Rooms;

namespace B2M.Revit.Commands
{
    /// <summary>Every command the add-in serves, registered in one place, grouped by catalog toolset.</summary>
    internal static class CommandCatalog
    {
        public static CommandDispatcher Create()
        {
            var dispatcher = new CommandDispatcher();
            // query
            dispatcher.Register(new GetCurrentViewInfoHandler());
            dispatcher.Register(new GetAvailableFamilyTypesHandler());
            dispatcher.Register(new AnalyzeModelStatisticsHandler());
            // create
            dispatcher.Register(new CreateLevelHandler());
            dispatcher.Register(new CreateGridHandler());
            dispatcher.Register(new CreateLineBasedElementHandler());
            dispatcher.Register(new CreateSurfaceBasedElementHandler());
            dispatcher.Register(new CreatePointBasedElementHandler());
            dispatcher.Register(new CreateRoomHandler());
            // delete
            dispatcher.Register(new DeleteElementHandler());
            // rooms
            dispatcher.Register(new AutoCreateRoomsFromWallsHandler());
            dispatcher.Register(new ListRoomsHandler());
            dispatcher.Register(new GetRoomBoundariesHandler());
            // lint
            dispatcher.Register(new GetModelWarningsSummaryHandler());
            // meta
            dispatcher.Register(new BatchExecuteHandler(dispatcher));
            return dispatcher;
        }
    }
}
