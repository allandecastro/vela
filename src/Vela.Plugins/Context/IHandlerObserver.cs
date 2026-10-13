namespace Vela.Context
{
    // Told when Vela's runtime calls a step's handler, or decides not to (a ForRelationship scope, SkipWhenDepthAbove,
    // the depth guard). Resolved from the plug-in's service provider, where only the Vela.Testing pipeline registers one
    // (Dataverse never does), so its execution log can tell a step that was invoked from one whose handler ran.
    internal interface IHandlerObserver
    {
        void HandlerInvoked();

        void HandlerSkipped(string reason);
    }
}
