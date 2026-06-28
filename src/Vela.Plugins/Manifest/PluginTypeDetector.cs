using System;
using System.Reflection;
using Vela.CustomApi;
using Vela.Plugin;

namespace Vela.Manifest;

/// <summary>
/// Shared reflection helpers for detecting framework plugin/Custom API types and instantiating them
/// via the standard <c>(string unsecure, string secure)</c> constructor.
/// </summary>
internal static class PluginTypeDetector
{
    /// <summary>
    /// Determines whether the type is a concrete <see cref="EntityPluginBase{TEntity}"/> derivative.
    /// </summary>
    public static bool IsEntityPlugin(Type type) => IsDerivedFrom(type, typeof(EntityPluginBase<>));

    /// <summary>
    /// Determines whether the type is a concrete <see cref="CustomApiBase"/> derivative
    /// (including the generic <see cref="CustomApiBase{TRequest, TResponse}"/>).
    /// </summary>
    public static bool IsCustomApi(Type type) => IsDerivedFrom(type, typeof(CustomApiBase));

    /// <summary>
    /// Finds the standard <c>(string, string)</c> constructor for the type.
    /// Returns <c>null</c> when no such constructor exists, so callers can decide whether to skip or throw.
    /// </summary>
    public static ConstructorInfo? GetStandardConstructor(Type type)
        => type.GetConstructor(new[] { typeof(string), typeof(string) });

    /// <summary>
    /// Instantiates the type using its standard <c>(string, string)</c> constructor, passing
    /// <c>(null, null)</c>. Throws <see cref="InvalidOperationException"/> when the constructor is missing.
    /// </summary>
    public static object CreateInstance(Type type)
    {
        ConstructorInfo? constructor = GetStandardConstructor(type);
        if (constructor == null)
        {
            throw new InvalidOperationException("Expected a (string, string) constructor.");
        }

        return constructor.Invoke(new object?[] { null, null });
    }

    private static bool IsDerivedFrom(Type type, Type baseType)
    {
        if (type.IsAbstract || !type.IsClass)
        {
            return false;
        }

        Type? current = type.BaseType;
        while (current != null)
        {
            if (current == baseType)
            {
                return true;
            }

            if (baseType.IsGenericTypeDefinition && current.IsGenericType && current.GetGenericTypeDefinition() == baseType)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }
}
