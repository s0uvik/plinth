using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Mep
{
    /// <summary>Registers the mep toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class MepCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new DetectSystemElementsHandler());
            dispatcher.Register(new CreateDuctHandler());
            dispatcher.Register(new CreatePipeHandler());
            dispatcher.Register(new CreateCableTrayHandler());
            dispatcher.Register(new CreateConduitHandler());
            dispatcher.Register(new CreateAirTerminalHandler());
            dispatcher.Register(new CreateLightingFixtureHandler());
            dispatcher.Register(new ListMepSystemsHandler());
            dispatcher.Register(new GetSystemInventoryHandler());
            dispatcher.Register(new GetMepElementConnectorsHandler());
            dispatcher.Register(new ConnectMepElementsHandler());
            dispatcher.Register(new CreateMepFittingHandler());
            dispatcher.Register(new SetSystemClassificationHandler());
            dispatcher.Register(new GetPanelScheduleHandler());
            dispatcher.Register(new FindMepDisconnectsHandler());
            dispatcher.Register(new AnalyzeMepNetworkHandler());
            dispatcher.Register(new CreateMepRunHandler());
            dispatcher.Register(new CreateElectricalCircuitHandler());
        }
    }
}
