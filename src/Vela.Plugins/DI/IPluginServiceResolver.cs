using System;

namespace Vela.DI
{
    /// <summary>
    /// Plugin execution service resolver abstraction.
    /// </summary>
    public interface IPluginServiceResolver : IServiceProvider
    {
        /// <summary>
        /// Resolves a typed service.
        /// </summary>
        /// <typeparam name="T">Service type.</typeparam>
        /// <returns>Resolved service instance.</returns>
        T GetRequiredService<T>() where T : class;
    }
}
