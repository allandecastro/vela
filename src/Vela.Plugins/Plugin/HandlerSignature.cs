using System;
using System.Reflection;

namespace Vela.Plugin;

/// <summary>
/// Shared validation for plugin/Custom API handler method signatures.
/// </summary>
internal static class HandlerSignature
{
    /// <summary>
    /// Determines whether <paramref name="method"/> is a supported handler: it must return <c>void</c>,
    /// have exactly <paramref name="parameterRules"/>.Length parameters, and satisfy each per-position
    /// predicate against the corresponding parameter's <see cref="ParameterInfo.ParameterType"/>.
    /// </summary>
    /// <param name="method">The candidate handler method.</param>
    /// <param name="parameterRules">One predicate per expected parameter, evaluated against that parameter's type.</param>
    public static bool IsSupported(MethodInfo method, params Func<Type, bool>[] parameterRules)
    {
        if (method.ReturnType != typeof(void))
        {
            return false;
        }

        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != parameterRules.Length)
        {
            return false;
        }

        for (int index = 0; index < parameterRules.Length; index++)
        {
            if (!parameterRules[index](parameters[index].ParameterType))
            {
                return false;
            }
        }

        return true;
    }
}
