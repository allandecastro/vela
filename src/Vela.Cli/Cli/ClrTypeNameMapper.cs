using System;
using System.Collections.Generic;
using System.Linq;

namespace Vela.Cli;

/// <summary>
/// Maps an assembly-qualified CLR type name (as stored in <c>sdkmessagerequestfield.clrparser</c> /
/// <c>sdkmessageresponsefield.clrformatter</c>, e.g.
/// <c>"Microsoft.Xrm.Sdk.EntityReference, Microsoft.Xrm.Sdk, Version=..."</c>) to a short C# type string usable
/// in generated code. It understands the SDK contract types, .NET primitives, arrays, <c>Nullable&lt;T&gt;</c> and
/// the common generic collections, and defensively falls back to <c>object</c> for anything unknown, empty or
/// malformed.
/// </summary>
public static class ClrTypeNameMapper
{
    // Generic collections whose C# form we can reproduce faithfully (single element type argument).
    private static readonly HashSet<string> ListLikeCollections = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List",
        "System.Collections.Generic.IList",
        "System.Collections.Generic.ICollection",
        "System.Collections.Generic.IEnumerable",
        "System.Collections.Generic.IReadOnlyList",
        "System.Collections.Generic.IReadOnlyCollection",
    };

    /// <summary>Maps an assembly-qualified CLR type name to a short C# type; <c>object</c> when unknown/empty.</summary>
    public static string Map(string? assemblyQualifiedTypeName)
    {
        if (string.IsNullOrWhiteSpace(assemblyQualifiedTypeName))
        {
            return "object";
        }

        try
        {
            return MapTypeName(TopLevelTypeName(assemblyQualifiedTypeName.Trim()));
        }
        catch
        {
            // Anything malformed maps to object rather than throwing into the generator.
            return "object";
        }
    }

    // The type-name portion of an assembly-qualified name: everything before the first depth-0 comma (generic
    // arguments live inside [[...]] and their commas are ignored).
    private static string TopLevelTypeName(string raw)
    {
        int depth = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                return raw.Substring(0, i).Trim();
            }
        }

        return raw.Trim();
    }

    private static string MapTypeName(string typeName)
    {
        typeName = typeName.Trim();
        if (typeName.Length == 0)
        {
            return "object";
        }

        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            return MapTypeName(typeName.Substring(0, typeName.Length - 2)) + "[]";
        }

        int tick = typeName.IndexOf('`');
        if (tick >= 0)
        {
            return MapGeneric(typeName.Substring(0, tick), ParseGenericArgs(typeName, tick));
        }

        return MapSimple(typeName);
    }

    private static string MapGeneric(string baseName, IReadOnlyList<string> args)
    {
        if (baseName == "System.Nullable" && args.Count == 1)
        {
            return MapTypeName(args[0]) + "?";
        }

        if (ListLikeCollections.Contains(baseName) && args.Count == 1)
        {
            return $"{baseName}<{MapTypeName(args[0])}>";
        }

        if ((baseName == "System.Collections.Generic.Dictionary" || baseName == "System.Collections.Generic.IDictionary") && args.Count == 2)
        {
            return $"{baseName}<{MapTypeName(args[0])}, {MapTypeName(args[1])}>";
        }

        return "object";
    }

    // The generic argument type names between the outer [ ... ] following the `N arity marker, each stripped of
    // its own [ ] wrapper and reduced to its type-name portion.
    private static IReadOnlyList<string> ParseGenericArgs(string typeName, int tick)
    {
        int open = typeName.IndexOf('[', tick);
        if (open < 0)
        {
            return Array.Empty<string>();
        }

        int depth = 0;
        int close = -1;
        for (int i = open; i < typeName.Length; i++)
        {
            if (typeName[i] == '[')
            {
                depth++;
            }
            else if (typeName[i] == ']')
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    break;
                }
            }
        }

        if (close < 0)
        {
            return Array.Empty<string>();
        }

        string inner = typeName.Substring(open + 1, close - open - 1);

        List<string> raw = new List<string>();
        depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                raw.Add(inner.Substring(start, i - start));
                start = i + 1;
            }
        }

        raw.Add(inner.Substring(start));

        return raw
            .Select(a => a.Trim())
            .Select(a => a.StartsWith("[", StringComparison.Ordinal) && a.EndsWith("]", StringComparison.Ordinal) ? a.Substring(1, a.Length - 2) : a)
            .Select(TopLevelTypeName)
            .ToList();
    }

    private static string MapSimple(string typeName) => typeName switch
    {
        // SDK contract types.
        "Microsoft.Xrm.Sdk.Entity" => "Entity",
        "Microsoft.Xrm.Sdk.EntityReference" => "EntityReference",
        "Microsoft.Xrm.Sdk.EntityCollection" => "EntityCollection",
        "Microsoft.Xrm.Sdk.OptionSetValue" => "OptionSetValue",
        "Microsoft.Xrm.Sdk.Money" => "Money",
        "Microsoft.Xrm.Sdk.Relationship" => "Relationship",
        "Microsoft.Xrm.Sdk.EntityReferenceCollection" => "EntityReferenceCollection",
        "Microsoft.Xrm.Sdk.Query.ColumnSet" => "ColumnSet",
        "Microsoft.Xrm.Sdk.Query.QueryBase" => "QueryBase",

        // Primitives / BCL scalars.
        "System.Guid" => "Guid",
        "System.Boolean" => "bool",
        "System.Int32" => "int",
        "System.Int64" => "long",
        "System.Double" => "double",
        "System.Decimal" => "decimal",
        "System.String" => "string",
        "System.DateTime" => "DateTime",
        "System.Byte" => "byte",
        "System.Object" => "object",

        _ => "object",
    };
}
