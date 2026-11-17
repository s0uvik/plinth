using Plinth.Revit.Bridge;

namespace Plinth.Revit.Commands.Lint
{
    /// <summary>Registers the lint toolset's commands ported in Step 8 (the earlier ones are in CommandCatalog).</summary>
    internal static class LintCatalog
    {
        public static void Register(CommandDispatcher dispatcher)
        {
            dispatcher.Register(new AnalyzeViewNamingPatternsHandler());
            dispatcher.Register(new SuggestViewNameCorrectionsHandler());
            dispatcher.Register(new DetectFirmProfileHandler());
        }
    }
}
