using Plinth.Revit.Bridge;
using Plinth.Revit.Commands.Annotation;
using Plinth.Revit.Commands.Create;
using Plinth.Revit.Commands.Delete;
using Plinth.Revit.Commands.Export;
using Plinth.Revit.Commands.Lint;
using Plinth.Revit.Commands.Meta;
using Plinth.Revit.Commands.Query;
using Plinth.Revit.Commands.Rooms;
using Plinth.Revit.Commands.Schedule;
using Plinth.Revit.Commands.Sheets;
using Plinth.Revit.Commands.View;

namespace Plinth.Revit.Commands
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
            dispatcher.Register(new ListCommandsHandler(dispatcher));

            // Step 8: the rest of the catalog, one registration file per toolset.
            global::Plinth.Revit.Commands.Query.QueryCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Modify.ModifyCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Create.CreateCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Lint.LintCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Documents.DocumentsCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Families.FamiliesCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Materials.MaterialsCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Parameters.ParametersCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.View.ViewCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Sheets.SheetsCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Schedule.ScheduleCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Annotation.AnnotationCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Export.ExportCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Links.LinksCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Organization.OrganizationCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Geometry.GeometryCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Graphics.GraphicsCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Rooms.RoomsCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Workflows.WorkflowsCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Mep.MepCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Structural.StructuralCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.Meta.MetaCatalog.Register(dispatcher);
            global::Plinth.Revit.Commands.ToolBaker.ToolBakerCatalog.Register(dispatcher);
            return dispatcher;
        }
    }
}
