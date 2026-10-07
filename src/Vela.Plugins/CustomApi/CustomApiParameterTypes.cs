using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace Vela.CustomApi
{
    /// <summary>
    /// Maps a Custom API parameter's CLR type to the type name <c>vela apply</c> registers. The single source shared by
    /// every builder (typed request/response properties, <c>Request(...)</c>/<c>Response(...)</c> and the lightweight
    /// builder). Only types with an exact Dataverse Custom API equivalent are accepted: any other type (long, float, an
    /// enum, byte[], List&lt;string&gt;, ...) fails when the step is built, instead of being registered as another type
    /// and then read back as its default value.
    /// </summary>
    internal static class CustomApiParameterTypes
    {
        private static readonly Dictionary<Type, string> Scalars = new Dictionary<Type, string>
        {
            [typeof(bool)] = typeof(bool).FullName!,
            [typeof(DateTime)] = typeof(DateTime).FullName!,
            [typeof(decimal)] = typeof(decimal).FullName!,
            [typeof(double)] = typeof(double).FullName!,
            [typeof(int)] = typeof(int).FullName!,
            [typeof(Guid)] = typeof(Guid).FullName!,
            [typeof(string)] = typeof(string).FullName!,
            [typeof(Money)] = typeof(Money).FullName!,
            [typeof(OptionSetValue)] = typeof(OptionSetValue).FullName!,
            [typeof(EntityReference)] = typeof(EntityReference).FullName!,
            [typeof(EntityCollection)] = typeof(EntityCollection).FullName!,
        };

        internal const string Supported =
            "bool, int, decimal, double, DateTime, Guid, string, string[], Money, OptionSetValue, EntityReference, Entity (or an early-bound entity), EntityCollection";

        /// <summary>The Dataverse Custom API type label of a described parameter (Entity, StringArray, Integer...).</summary>
        internal static string Label(string typeName, bool isCollection)
        {
            if (isCollection)
            {
                return typeName == typeof(string).FullName ? "StringArray" : "EntityCollection";
            }

            if (typeName == typeof(Entity).FullName) return "Entity";
            if (typeName == typeof(EntityCollection).FullName) return "EntityCollection";
            if (typeName == typeof(EntityReference).FullName) return "EntityReference";
            int dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }

        /// <summary>The logical name of an early-bound entity type (its <c>[EntityLogicalName]</c>), else null.</summary>
        internal static string? EarlyBoundLogicalName(Type clrType)
        {
            Type type = Nullable.GetUnderlyingType(clrType) ?? clrType;
            return type != typeof(Entity) && typeof(Entity).IsAssignableFrom(type)
                ? System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.Xrm.Sdk.Client.EntityLogicalNameAttribute>(type)?.LogicalName
                : null;
        }

        /// <summary>Returns the registered type name and collection flag for <paramref name="clrType"/>, or throws.</summary>
        internal static (string TypeName, bool IsCollection) Describe(Type clrType, string parameterName)
        {
            clrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
            Type type = Nullable.GetUnderlyingType(clrType) ?? clrType;

            if (type == typeof(string[]))
            {
                return (typeof(string).FullName!, true); // StringArray
            }

            if (typeof(Entity).IsAssignableFrom(type))
            {
                return (typeof(Entity).FullName!, false); // an early-bound entity travels as Entity
            }

            if (Scalars.TryGetValue(type, out string? name))
            {
                return (name, false);
            }

            throw new NotSupportedException(
                $"Custom API parameter '{parameterName}' is a {type.FullName}, which has no Dataverse Custom API type. Use one of: {Supported}." +
                (type.IsEnum ? " For a choice, use OptionSetValue (or int)." : string.Empty) +
                (type == typeof(long) || type == typeof(short) || type == typeof(byte) ? " For a whole number, use int." : string.Empty) +
                (type == typeof(float) ? " For a floating-point number, use double." : string.Empty));
        }
    }
}
