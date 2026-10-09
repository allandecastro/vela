using System;
using System.Linq;
using System.Text.Json;

namespace Vela.Cli;

/// <summary>
/// Pure helpers for reading values out of <see cref="JsonElement"/> registration definitions.
/// Collection readers normalize ordering so that diffs are order-insensitive.
/// </summary>
internal static class JsonReader
{
    public static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    public static string? TryGetNestedString(JsonElement element, string objectProperty, string nestedProperty)
    {
        if (!element.TryGetProperty(objectProperty, out JsonElement nested) || nested.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(nested, nestedProperty);
    }

    public static bool TryGetBool(JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return defaultValue;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    public static int TryGetInt(JsonElement element, string propertyName, int defaultValue = 0)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return defaultValue;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out int value))
        {
            return value;
        }

        return defaultValue;
    }

    public static string[] TryGetStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }

    public static int TryGetArrayLength(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return property.GetArrayLength();
    }

    // Image ordering is owned by DefinitionNormalizer.BuildStepShape (the shared shape builder), so this
    // just reads the raw alias/attribute pairs in document order.
    public static StepImage[] TryGetStepImages(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement images) || images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return images.EnumerateArray()
            .Select(image => new StepImage(
                TryGetString(image, "alias") ?? string.Empty,
                TryGetStringArray(image, "attributes"),
                TryGetString(image, "messagePropertyName") ?? string.Empty))
            .ToArray();
    }

    public static object[] TryGetCustomApiInputShapes(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(value =>
            {
                string name = TryGetString(value, "name") ?? string.Empty;
                bool isOptional = TryGetBool(value, "isOptional");
                string? parameterTypeName = TryGetString(value, "parameterTypeName") ?? TryGetString(value, "typeName");
                bool isCollection = TryGetBool(value, "isCollection");
                string? logicalEntityName = TryGetString(value, "logicalEntityName");
                // The local/desired side carries no uniqueName (it is not in the package metadata); its expected
                // value is the bare parameter name. The live side supplies the actual Dataverse uniquename, so a
                // drifted (dotted) live value diffs against the bare expected value and triggers reconciliation.
                string uniqueName = TryGetString(value, "uniqueName") ?? name;
                string description = CustomApiShape.LabelOrDefault(TryGetString(value, "description"), name);
                (parameterTypeName, isCollection) = CanonicalizeCustomApiType(parameterTypeName, isCollection, logicalEntityName);
                return (name, isOptional, parameterTypeName, isCollection, logicalEntityName, uniqueName, description);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .OrderBy(x => x.name, StringComparer.Ordinal)
            .Select(x => CustomApiShape.InputParameter(x.name, x.isOptional, x.parameterTypeName, x.isCollection, x.logicalEntityName, x.uniqueName, x.description))
            .ToArray();
    }

    public static object[] TryGetCustomApiOutputShapes(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(value =>
            {
                string name = TryGetString(value, "name") ?? string.Empty;
                string? parameterTypeName = TryGetString(value, "parameterTypeName") ?? TryGetString(value, "typeName");
                bool isCollection = TryGetBool(value, "isCollection");
                string? logicalEntityName = TryGetString(value, "logicalEntityName");
                string uniqueName = TryGetString(value, "uniqueName") ?? name;
                string description = CustomApiShape.LabelOrDefault(TryGetString(value, "description"), name);
                (parameterTypeName, isCollection) = CanonicalizeCustomApiType(parameterTypeName, isCollection, logicalEntityName);
                return (name, parameterTypeName, isCollection, logicalEntityName, uniqueName, description);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .OrderBy(x => x.name, StringComparer.Ordinal)
            .Select(x => CustomApiShape.OutputParameter(x.name, x.parameterTypeName, x.isCollection, x.logicalEntityName, x.uniqueName, x.description))
            .ToArray();
    }

    // A4: canonicalize a Custom API parameter type name to the same code->name form the live read produces (e.g.
    // System.Int64 / long -> System.Int32, System.Single -> System.Double, and the collection flag normalized), so an
    // equivalent local declaration does not diff as a perpetual "changed" against the live state. Round-trips through
    // the same resolvers apply uses, so it is idempotent on an already-canonical (live) shape.
    private static (string typeName, bool isCollection) CanonicalizeCustomApiType(string? parameterTypeName, bool isCollection, string? logicalEntityName)
    {
        int typeCode = DataverseRegistration.ResolveCustomApiTypeCode(parameterTypeName, isCollection);
        return DataverseRegistration.ResolveCustomApiTypeName(typeCode, logicalEntityName);
    }

    public static string[] SplitCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }
}
