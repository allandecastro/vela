namespace Vela.DI
{
    // Registrations that replace a plug-in's own. Resolved from the plug-in's service provider, where only the
    // Vela.Testing harnesses register one (Dataverse never does), so a test can hand a plug-in a fake service.
    // PluginBase applies it right after the assembly registrar and ConfigureServices, so its registrations win.
    internal interface IServiceOverrides
    {
        void Apply(SimpleServiceResolver services);
    }
}
