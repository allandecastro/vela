using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
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
        // A Lazy per assembly: ConcurrentDictionary.GetOrAdd may run its value factory on several threads for the
        // same key, so caching the registrar directly could instantiate it more than once. Caching a Lazy with
        // ExecutionAndPublication evaluates CreateRegistrar exactly once per assembly even under concurrent first
        // executions (mirrors the plugin cache), so constructor side effects never duplicate.
        private static readonly ConcurrentDictionary<Assembly, Lazy<IServiceRegistrar?>> Cache =
            new ConcurrentDictionary<Assembly, Lazy<IServiceRegistrar?>>();

        /// <summary>The registrar declared by the plugin type's assembly, or null when none is declared.</summary>
        public static IServiceRegistrar? Resolve(Type pluginType)
        {
            pluginType = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
            return Cache.GetOrAdd(
                pluginType.Assembly,
                assembly => new Lazy<IServiceRegistrar?>(
                    () => CreateRegistrar(assembly.GetCustomAttribute<VelaServiceRegistrarAttribute>()),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
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

            // An abstract/interface/open-generic type can implement the interface yet is not instantiable;
            // Activator.CreateInstance would throw an opaque MemberAccessException/ArgumentException, so reject it
            // up front with the same clear message.
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                throw new InvalidPluginExecutionException(
                    $"[assembly: VelaServiceRegistrar] type '{type.FullName}' must be a concrete, non-generic class.");
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
