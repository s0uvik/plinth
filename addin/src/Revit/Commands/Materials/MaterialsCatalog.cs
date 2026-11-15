using B2M.Revit.Bridge;

namespace B2M.Revit.Commands.Materials
{
    /// <summary>Registers the materials toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class MaterialsCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new ListMaterialsHandler());
            dispatcher.Register(new GetMaterialPropertiesHandler());
            dispatcher.Register(new CreateMaterialHandler());
            dispatcher.Register(new DuplicateMaterialHandler());
            dispatcher.Register(new SetMaterialAppearanceHandler());
            dispatcher.Register(new SetMaterialIdentityHandler());
            dispatcher.Register(new SetMaterialStructuralAssetHandler());
            dispatcher.Register(new SetMaterialThermalAssetHandler());
            dispatcher.Register(new AssignMaterialToElementHandler());
            dispatcher.Register(new GetMaterialTakeoffHandler());
        }
    }
}
