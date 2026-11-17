using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Geometry
{
    /// <summary>Registers the geometry toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class GeometryCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new GetElementBoundingBoxHandler());
            dispatcher.Register(new GetElementGeometryHandler());
            dispatcher.Register(new MeasureDistanceBetweenElementsHandler());
            dispatcher.Register(new ClashDetectionHandler());
            dispatcher.Register(new RaycastFromPointHandler());
            dispatcher.Register(new FindElementsInVolumeHandler());
            dispatcher.Register(new ComputeElementVolumeHandler());
            dispatcher.Register(new ComputeElementAreaHandler());
            dispatcher.Register(new ProjectPointOntoFaceHandler());
            dispatcher.Register(new FindOverlappingElementsHandler());
            dispatcher.Register(new GetElementCentroidHandler());
            dispatcher.Register(new AnalyzeGeometryComplexityHandler());
            dispatcher.Register(new CreateMeshShapesHandler());
        }
    }
}
