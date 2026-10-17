using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Client;

namespace Vela.Steps
{
    /// <summary>
    /// Maps a Dataverse <see cref="Entity"/> image into a generated projection POCO, copying only the attributes
    /// whose logical names appear on the projection's <see cref="AttributeLogicalNameAttribute"/>-annotated
    /// properties. Backs the typed-image <c>Handle&lt;TImg&gt;(...)</c> terminals so a handler sees exactly the
    /// columns the step captured (reading anything else is a compile error on the generated type).
    /// </summary>
    public static class ImageProjector
    {
        // Mapped columns (property, logical name, choice coercion), cached per projection type (plugins run hot;
        // reflection once).
        private static readonly ConcurrentDictionary<Type, Column[]> Maps = new();

        /// <summary>
        /// Projects <paramref name="image"/> onto a new <typeparamref name="TImg"/>. Returns <c>null</c> when the
        /// image is absent (mirroring the nullable early-bound <c>ctx.PreImage</c>/<c>ctx.PostImage</c>). Only
        /// attributes present on both the image and the projection are copied. A value is copied as-is when it already
        /// fits the property; choice columns are converted, because the image holds the SDK types while the generated
        /// property has the early-bound type: an <see cref="OptionSetValue"/> becomes the property's enum (nullable or
        /// not) or <c>int</c>, and an <see cref="OptionSetValueCollection"/> becomes the property's enum collection
        /// (<c>IList&lt;TEnum&gt;</c>, <c>IEnumerable&lt;TEnum&gt;</c>, <c>IReadOnlyList&lt;TEnum&gt;</c>,
        /// <c>List&lt;TEnum&gt;</c>, <c>TEnum[]</c>...). Any other mismatch (for example after a metadata change since
        /// the model was generated) is skipped and, when <paramref name="tracing"/> is supplied, traced so the drop is
        /// diagnosable rather than a silent <c>null</c>.
        /// </summary>
        public static TImg? Project<TImg>(Entity? image, ITracingService? tracing = null) where TImg : class, new()
        {
            if (image == null)
            {
                return null;
            }

            TImg target = new TImg();
            foreach (Column column in GetMap(typeof(TImg)))
            {
                if (!image.Attributes.TryGetValue(column.LogicalName, out object? value) || value == null)
                {
                    continue;
                }

                if (!column.Property.PropertyType.IsInstanceOfType(value))
                {
                    object? converted = column.Coerce?.Invoke(value);
                    if (converted == null)
                    {
                        tracing?.Trace(
                            "ImageProjector: attribute '{0}' skipped - expected {1} but the image value was {2}.",
                            column.LogicalName,
                            column.Property.PropertyType.FullName,
                            value.GetType().FullName);
                        continue;
                    }

                    value = converted;
                }

                column.Property.SetValue(target, value);
            }

            // Retain the source image so the generated explicit (TEntity)projection cast can return the whole
            // early-bound image (Id + logical name + every attribute), not a reconstruction limited to the mapped
            // columns. Only generated projections implement the marker; a hand-rolled projection simply has no Source.
            if (target is Vela.Context.IImageProjection projection)
            {
                projection.Source = image;
            }

            return target;
        }

        private static Column[] GetMap(Type type) =>
            Maps.GetOrAdd(type, static t =>
            {
                List<Column> map = new List<Column>();
                foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!property.CanWrite)
                    {
                        continue;
                    }

                    AttributeLogicalNameAttribute? attribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
                    if (attribute != null && !string.IsNullOrEmpty(attribute.LogicalName))
                    {
                        map.Add(new Column(property, attribute.LogicalName, ChoiceCoercion(property.PropertyType)));
                    }
                }

                return map.ToArray();
            });

        // The conversion from the SDK choice type the image holds to the early-bound property type, or null when the
        // property is not a choice shape. Each returns null for a value it cannot convert (the caller skips and traces).
        private static Func<object, object?>? ChoiceCoercion(Type propertyType)
        {
            Type valueType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
            if (valueType.IsEnum)
            {
                return value => value is OptionSetValue option ? Enum.ToObject(valueType, option.Value) : null;
            }

            if (valueType == typeof(int))
            {
                return static value => value is OptionSetValue option ? option.Value : null;
            }

            Type? element = EnumElementType(propertyType);
            if (element == null)
            {
                return null;
            }

            if (propertyType.IsArray)
            {
                return value => ToEnumValues(value, element) is List<object> values ? ToArray(values, element) : null;
            }

            Type listType = typeof(List<>).MakeGenericType(element);
            if (!propertyType.IsAssignableFrom(listType))
            {
                return null;
            }

            return value =>
            {
                if (ToEnumValues(value, element) is not List<object> values)
                {
                    return null;
                }

                IList list = (IList)Activator.CreateInstance(listType)!;
                foreach (object item in values)
                {
                    list.Add(item);
                }

                return list;
            };
        }

        // The enum element of an array or IEnumerable<TEnum>-shaped property type, or null.
        private static Type? EnumElementType(Type propertyType)
        {
            if (propertyType.IsArray)
            {
                Type arrayElement = propertyType.GetElementType()!;
                return propertyType.GetArrayRank() == 1 && arrayElement.IsEnum ? arrayElement : null;
            }

            List<Type> candidates = new List<Type>(propertyType.GetInterfaces());
            candidates.Add(propertyType);
            foreach (Type candidate in candidates)
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    Type element = candidate.GetGenericArguments()[0];
                    if (element.IsEnum)
                    {
                        return element;
                    }
                }
            }

            return null;
        }

        // The boxed enum values of a multi-select image value, or null when it is not an OptionSetValueCollection.
        // Built with public, non-generic APIs only (no private reflection), so it also runs in the plugin sandbox.
        private static List<object>? ToEnumValues(object value, Type element)
        {
            if (value is not OptionSetValueCollection options)
            {
                return null;
            }

            List<object> values = new List<object>(options.Count);
            foreach (OptionSetValue option in options)
            {
                if (option != null)
                {
                    values.Add(Enum.ToObject(element, option.Value));
                }
            }

            return values;
        }

        private static Array ToArray(List<object> values, Type element)
        {
            Array array = Array.CreateInstance(element, values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                array.SetValue(values[i], i);
            }

            return array;
        }

        private sealed class Column
        {
            public Column(PropertyInfo property, string logicalName, Func<object, object?>? coerce)
            {
                Property = property;
                LogicalName = logicalName;
                Coerce = coerce;
            }

            public PropertyInfo Property { get; }

            public string LogicalName { get; }

            public Func<object, object?>? Coerce { get; }
        }
    }
}
