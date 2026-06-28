using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

namespace Vela.Plugin
{
    /// <summary>
    /// Caches handler-method reflection per plugin type so that step / Custom API dispatch does not
    /// re-enumerate the type's methods on every execution. The set of methods on a type is fixed for
    /// the lifetime of the process, so caching by (type, method name) is safe.
    /// </summary>
    internal static class HandlerResolver
    {
        private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, MethodInfo[]>> Cache =
            new ConcurrentDictionary<Type, ConcurrentDictionary<string, MethodInfo[]>>();

        /// <summary>
        /// Returns all instance methods (public or non-public) on <paramref name="type"/> whose name
        /// matches <paramref name="methodName"/> ordinally. The result is cached.
        /// </summary>
        public static MethodInfo[] GetNamedMethods(Type type, string methodName)
        {
            ConcurrentDictionary<string, MethodInfo[]> byName =
                Cache.GetOrAdd(type, _ => new ConcurrentDictionary<string, MethodInfo[]>(StringComparer.Ordinal));

            return byName.GetOrAdd(methodName, name => type
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
                .ToArray());
        }
    }
}
