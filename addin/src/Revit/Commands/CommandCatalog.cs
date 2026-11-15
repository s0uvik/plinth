using B2M.Revit.Bridge;
using B2M.Revit.Commands.Annotation;
using B2M.Revit.Commands.Create;
using B2M.Revit.Commands.Delete;
using B2M.Revit.Commands.Export;
using B2M.Revit.Commands.Lint;
using B2M.Revit.Commands.Meta;
using B2M.Revit.Commands.Query;
using B2M.Revit.Commands.Rooms;
using B2M.Revit.Commands.Schedule;
using B2M.Revit.Commands.Sheets;
using B2M.Revit.Commands.View;

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
            dispatcher.Register(new ListLevelsHandler());
            dispatcher.Register(new ListGridsHandler());
            // create
            dispatcher.Register(new CreateLevelHandler());
            dispatcher.Register(new CreateGridHandler());
            dispatcher.Register(new CreateLineBasedElementHandler());
            dispatcher.Register(new CreateSurfaceBasedElementHandler());
            dispatcher.Register(new CreatePointBasedElementHandler());
            dispatcher.Register(new CreateRoomHandler());
            dispatcher.Register(new CreateFloorOpeningHandler());
            // delete
            dispatcher.Register(new DeleteElementHandler());
            // rooms
            dispatcher.Register(new AutoCreateRoomsFromWallsHandler());
            dispatcher.Register(new ListRoomsHandler());
            dispatcher.Register(new GetRoomBoundariesHandler());
            // view
            dispatcher.Register(new ListViewsHandler());
            dispatcher.Register(new CreateViewHandler());
            dispatcher.Register(new CaptureViewImageHandler());
            // annotation
            dispatcher.Register(new TagAllRoomsHandler());
            // sheets
            dispatcher.Register(new CreateSheetHandler());
            dispatcher.Register(new PlaceViewOnSheetHandler());
            dispatcher.Register(new PlaceScheduleOnSheetHandler());
            dispatcher.Register(new AlignViewportsHandler());
            // schedule
            dispatcher.Register(new CreateScheduleHandler());
            // export
            dispatcher.Register(new ExportPdfHandler());
            // lint
            dispatcher.Register(new GetModelWarningsSummaryHandler());
            // meta
            dispatcher.Register(new BatchExecuteHandler(dispatcher));
            return dispatcher;
        }
    }
}
