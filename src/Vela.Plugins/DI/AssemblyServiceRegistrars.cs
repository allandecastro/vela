using System;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Xrm.Sdk;

namespace Vela.DI
{
    /// <summary>
    /// Resolves the <see cref="IServiceRegistrar"/> declared by a plugin's assembly through
    /// <see cref="VelaServiceRegistrarAttribute"/>, once per assembly and cached, so the attribute read and the
    /// registrar instantiation do not repeat on the execution hot path. The sandbox reuses a plugin instance across
    /// invocations, so the registrar is created once and its stateless <see cref="IServiceRegistrar.Register"/> is
    /// replayed onto each execution's fresh resolver. Reading an assembly attribute is cheap and needs no type scan.
    /// </summary>
    internal static class AssemblyServiceRegistrars
    {
        private static readonly ConcurrentDictionary<Assembly, IServiceRegistrar?> Cache =
            new ConcurrentDictionary<Assembly, IServiceRegistrar?>();

        /// <summary>The registrar declared by the plugin type's assembly, or null when none is declared.</summary>
        public static IServiceRegistrar? Resolve(Type pluginType)
        {
            pluginType = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
            return Cache.GetOrAdd(
                pluginType.Assembly,
                assembly => CreateRegistrar(assembly.GetCustomAttribute<VelaServiceRegistrarAttribute>()));
        }

        // Instantiates the registrar an attribute points at (null when the attribute is absent). Kept separate from
        // the assembly/attribute read so the validation + instantiation is unit-testable without a real
        // assembly-level attribute.
        internal static IServiceRegistrar? CreateRegistrar(VelaServiceRegistrarAttribute? attribute)
        {
            if (attribute == null)
            {
                return null;
            }

            Type type = attribute.RegistrarType;
            if (!typeof(IServiceRegistrar).IsAssignableFrom(type))
            {
                throw new InvalidPluginExecutionException(
                    $"[assembly: VelaServiceRegistrar] type '{type.FullName}' must implement {nameof(IServiceRegistrar)}.");
            }

            try
            {
                return (IServiceRegistrar)Activator.CreateInstance(type);
            }
            catch (MissingMethodException ex)
            {
                throw new InvalidPluginExecutionException(
                    $"Could not create service registrar '{type.FullName}': it needs a public parameterless constructor.", ex);
            }
        }
    }
}
