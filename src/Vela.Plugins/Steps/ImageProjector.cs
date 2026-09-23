using System;
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
        // Property -> attribute logical name, cached per projection type (plugins run hot; reflection once).
        private static readonly ConcurrentDictionary<Type, KeyValuePair<PropertyInfo, string>[]> Maps = new();

        /// <summary>
        /// Projects <paramref name="image"/> onto a new <typeparamref name="TImg"/>. Returns <c>null</c> when the
        /// image is absent (mirroring the nullable early-bound <c>ctx.PreImage</c>/<c>ctx.PostImage</c>). Only
        /// attributes present on both the image and the projection are copied; the value is copied as-is (the
        /// generated property type matches the early-bound column type), so no coercion is needed. An attribute whose
        /// runtime value type does not match the projection property (for example after a metadata change since the
        /// model was generated) is skipped and, when <paramref name="tracing"/> is supplied, traced so the drop is
        /// diagnosable rather than a silent <c>null</c>.
        /// </summary>
        public static TImg? Project<TImg>(Entity? image, ITracingService? tracing = null) where TImg : class, new()
        {
            if (image == null)
            {
                return null;
            }

            TImg target = new TImg();
            foreach (KeyValuePair<PropertyInfo, string> entry in GetMap(typeof(TImg)))
            {
                if (!image.Attributes.TryGetValue(entry.Value, out object? value) || value == null)
                {
                    continue;
                }

                if (!entry.Key.PropertyType.IsInstanceOfType(value))
                {
                    tracing?.Trace(
                        "ImageProjector: attribute '{0}' skipped - expected {1} but the image value was {2}.",
                        entry.Value,
                        entry.Key.PropertyType.FullName,
                        value.GetType().FullName);
                    continue;
                }

                entry.Key.SetValue(target, value);
            }

            return target;
        }

        private static KeyValuePair<PropertyInfo, string>[] GetMap(Type type) =>
            Maps.GetOrAdd(type, static t =>
            {
                List<KeyValuePair<PropertyInfo, string>> map = new List<KeyValuePair<PropertyInfo, string>>();
                foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!property.CanWrite)
                    {
                        continue;
                    }

                    AttributeLogicalNameAttribute? attribute = property.GetCustomAttribute<AttributeLogicalNameAttribute>();
                    if (attribute != null && !string.IsNullOrEmpty(attribute.LogicalName))
                    {
                        map.Add(new KeyValuePair<PropertyInfo, string>(property, attribute.LogicalName));
                    }
                }

                return map.ToArray();
            });
    }
}
