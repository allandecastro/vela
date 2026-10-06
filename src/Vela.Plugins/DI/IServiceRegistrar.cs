using System;
using Vela.Context;

namespace Vela.DI
{
    /// <summary>
    /// Registers shared services for every plugin and Custom API in an assembly, once, instead of repeating
    /// <c>ConfigureServices</c> in each class (or introducing a shared base type just to carry it). Point the
    /// assembly at an implementation with <c>[assembly: VelaServiceRegistrar(typeof(MyRegistrar))]</c>; its
    /// <see cref="Register"/> runs before each plugin's <c>ConfigureServices</c>, so a plugin can still override a
    /// shared registration. The type must be stateless and have a public parameterless constructor (the sandbox
    /// reuses and concurrently shares instances), like a plugin itself.
    /// </summary>
    public interface IServiceRegistrar
    {
        /// <summary>Adds the assembly-wide services to the current execution's resolver.</summary>
        /// <param name="services">The per-execution service resolver (same one <c>ConfigureServices</c> receives).</param>
        /// <param name="context">The live plugin context.</param>
        void Register(SimpleServiceResolver services, ILocalPluginContext context);
    }

    /// <summary>
    /// Declares the assembly's <see cref="IServiceRegistrar"/>. Apply it once at assembly scope:
    /// <c>[assembly: VelaServiceRegistrar(typeof(MyRegistrar))]</c>. The referenced type must implement
    /// <see cref="IServiceRegistrar"/> and have a public parameterless constructor.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public sealed class VelaServiceRegistrarAttribute : Attribute
    {
        /// <summary>Creates the attribute for the given registrar type.</summary>
        /// <param name="registrarType">A type implementing <see cref="IServiceRegistrar"/>.</param>
        public VelaServiceRegistrarAttribute(Type registrarType)
        {
            RegistrarType = registrarType ?? throw new ArgumentNullException(nameof(registrarType));
        }

        /// <summary>The declared registrar type (implements <see cref="IServiceRegistrar"/>).</summary>
        public Type RegistrarType { get; }
    }
}
