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
                TryGetStringArray(image, "attributes")))
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
                return new { name, isOptional, parameterTypeName, isCollection, logicalEntityName };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .OrderBy(x => x.name, StringComparer.Ordinal)
            .ToArray<object>();
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
                return new { name, parameterTypeName, isCollection, logicalEntityName };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.name))
            .OrderBy(x => x.name, StringComparer.Ordinal)
            .ToArray<object>();
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
