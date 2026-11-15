using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Schedule
{
    /// <summary>Registers the schedule toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class ScheduleCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new ListSchedulesHandler());
            dispatcher.Register(new GetScheduleDefinitionHandler());
            dispatcher.Register(new GetScheduleDataHandler());
            dispatcher.Register(new GetScheduleFormulasHandler());
            dispatcher.Register(new GetSchedulableFieldsHandler());
            dispatcher.Register(new FindScheduleElementsHandler());
            dispatcher.Register(new AddScheduleFieldHandler());
            dispatcher.Register(new UpdateScheduleFieldHandler());
            dispatcher.Register(new ApplyScheduleFilterSortHandler());
        }
    }
}
