using System;
using System.Collections.Generic;
using Vela.Constants;
using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// Helper methods for input/output parameters and images. Diagnostics/tracing helpers live in the
    /// <c>PluginContextExtensions.Tracing.cs</c> partial.
    /// </summary>
    public static partial class PluginContextExtensions
    {
        /// <summary>
        /// Tries to get target entity.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="target">Target entity when found.</param>
        /// <returns>True when target exists.</returns>
        public static bool TryGetTarget(this ILocalPluginContext context, out Entity? target)
        {
            target = null;
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.PluginExecutionContext.InputParameters.Contains("Target"))
            {
                return false;
            }

            target = context.PluginExecutionContext.InputParameters["Target"] as Entity;
            return target != null;
        }

        /// <summary>
        /// Gets target entity reference for delete/associate patterns.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <returns>Target entity reference.</returns>
        public static EntityReference GetTargetRef(this ILocalPluginContext context)
        {
            EntityReference? reference = context.GetInputParameter<EntityReference>("Target");
            return reference ?? throw new InvalidPluginExecutionException("Input parameter 'Target' is not an EntityReference.");
        }

        /// <summary>
        /// Gets typed target entity.
        /// </summary>
        /// <typeparam name="T">Entity type.</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <returns>Typed entity.</returns>
        public static T GetTarget<T>(this ILocalPluginContext context) where T : Entity
        {
            if (!TryGetTarget(context, out Entity? target) || target == null)
            {
                throw new InvalidPluginExecutionException("Input parameter 'Target' is not an Entity.");
            }

            return target.ToEntity<T>();
        }

        /// <summary>
        /// Tries to get the target collection for bulk messages (CreateMultiple, UpdateMultiple).
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="targets">Targets collection when found.</param>
        /// <returns>True when targets exist.</returns>
        public static bool TryGetTargets(this ILocalPluginContext context, out EntityCollection? targets)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            targets = context.BulkTargets;
            if (targets != null)
            {
                return true;
            }

            if (!context.PluginExecutionContext.InputParameters.Contains("Targets"))
            {
                return false;
            }

            targets = context.PluginExecutionContext.InputParameters["Targets"] as EntityCollection;
            return targets != null;
        }

        /// <summary>
        /// Gets the target collection for bulk messages (CreateMultiple, UpdateMultiple).
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <returns>Targets collection.</returns>
        public static EntityCollection GetTargets(this ILocalPluginContext context)
        {
            if (!context.TryGetTargets(out EntityCollection? targets) || targets == null)
            {
                throw new InvalidPluginExecutionException("Input parameter 'Targets' is not an EntityCollection.");
            }

            return targets;
        }

        /// <summary>
        /// Gets typed input parameter.
        /// </summary>
        /// <typeparam name="T">Parameter type.</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <param name="name">Parameter name.</param>
        /// <returns>Parameter value when present, otherwise default.</returns>
        public static T? GetInputParameter<T>(this ILocalPluginContext context, string name)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return GetParameter<T>(context.PluginExecutionContext.InputParameters, name);
        }

        /// <summary>
        /// Gets a service from the execution resolver when available.
        /// </summary>
        /// <typeparam name="T">Service type.</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <returns>Service instance, or null when not registered.</returns>
        public static T? GetService<T>(this ILocalPluginContext context) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context is not IServiceResolverContext resolverContext)
            {
                return null;
            }

            return resolverContext.Resolver.GetService(typeof(T)) as T;
        }

        /// <summary>
        /// Gets a required service from the execution resolver.
        /// </summary>
        /// <typeparam name="T">Service type.</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <returns>Registered service instance.</returns>
        public static T GetRequiredService<T>(this ILocalPluginContext context) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context is not IServiceResolverContext resolverContext)
            {
                throw new InvalidPluginExecutionException(
                    $"Context type '{context.GetType().FullName}' does not expose the execution service resolver.");
            }

            return resolverContext.Resolver.GetRequiredService<T>();
        }

        /// <summary>
        /// Gets typed output parameter.
        /// </summary>
        /// <typeparam name="T">Parameter type.</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <param name="name">Parameter name.</param>
        /// <returns>Parameter value when present, otherwise default.</returns>
        public static T? GetOutputParameter<T>(this ILocalPluginContext context, string name)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return GetParameter<T>(context.PluginExecutionContext.OutputParameters, name);
        }

        // Shared lookup for input/output parameters: returns the typed value when present and of the
        // requested type, otherwise default(T).
        private static T? GetParameter<T>(ParameterCollection parameters, string name)
        {
            if (!parameters.Contains(name))
            {
                return default(T);
            }

            return parameters[name] is T typed ? typed : default(T);
        }

        /// <summary>
        /// Sets an output parameter value.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="name">Parameter name.</param>
        /// <param name="value">Parameter value.</param>
        public static void SetOutputParameter(this ILocalPluginContext context, string name, object? value)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.PluginExecutionContext.OutputParameters[name] = value;
        }

        /// <summary>
        /// Gets pre-image by alias.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Entity image or null.</returns>
        public static Entity? GetPreImage(this ILocalPluginContext context, string alias = PluginImageAliases.PreImage)
        {
            return GetEntityImage(context.PluginExecutionContext.PreEntityImages, alias);
        }

        /// <summary>
        /// Gets post-image by alias.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Entity image or null.</returns>
        public static Entity? GetPostImage(this ILocalPluginContext context, string alias = PluginImageAliases.PostImage)
        {
            return GetEntityImage(context.PluginExecutionContext.PostEntityImages, alias);
        }

        /// <summary>
        /// Gets pre-image by alias for a specific target index in bulk messages.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="targetIndex">Target index.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Entity image or null.</returns>
        public static Entity? GetPreImage(this ILocalPluginContext context, int targetIndex, string alias = PluginImageAliases.PreImage)
        {
            EntityImageCollection? imageCollection = GetBulkImageCollection(context, targetIndex, context.BulkPreEntityImagesCollection);
            return imageCollection == null ? null : GetEntityImage(imageCollection, alias);
        }

        /// <summary>
        /// Gets post-image by alias for a specific target index in bulk messages.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="targetIndex">Target index.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Entity image or null.</returns>
        public static Entity? GetPostImage(this ILocalPluginContext context, int targetIndex, string alias = PluginImageAliases.PostImage)
        {
            EntityImageCollection? imageCollection = GetBulkImageCollection(context, targetIndex, context.BulkPostEntityImagesCollection);
            return imageCollection == null ? null : GetEntityImage(imageCollection, alias);
        }

        /// <summary>
        /// Gets typed pre-image.
        /// </summary>
        /// <typeparam name="T">Entity type.</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Typed image or null.</returns>
        public static T? GetPreImage<T>(this ILocalPluginContext context, string alias = PluginImageAliases.PreImage) where T : Entity
        {
            Entity? image = context.GetPreImage(alias);
            return image == null ? null : image.ToEntity<T>();
        }

        /// <summary>
        /// Gets typed post-image.
        /// </summary>
        /// <typeparam name="T">Entity type.</typeparam>
        /// <param name="context">Plugin context.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Typed image or null.</returns>
        public static T? GetPostImage<T>(this ILocalPluginContext context, string alias = PluginImageAliases.PostImage) where T : Entity
        {
            Entity? image = context.GetPostImage(alias);
            return image == null ? null : image.ToEntity<T>();
        }

        /// <summary>
        /// Gets pre-image merged with target attributes.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Merged entity.</returns>
        public static Entity GetPreImageMerged(this ILocalPluginContext context, string alias = PluginImageAliases.PreImage)
        {
            Entity? preImage = context.GetPreImage(alias);
            return MergeWithTarget(context, preImage);
        }

        /// <summary>
        /// Gets post-image merged with target attributes.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Merged entity.</returns>
        public static Entity GetPostImageMerged(this ILocalPluginContext context, string alias = PluginImageAliases.PostImage)
        {
            Entity? postImage = context.GetPostImage(alias);
            return MergeWithTarget(context, postImage);
        }

        /// <summary>
        /// Gets pre-image merged with target attributes for a specific target index in bulk messages.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="targetIndex">Target index.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Merged entity.</returns>
        public static Entity GetPreImageMerged(this ILocalPluginContext context, int targetIndex, string alias = PluginImageAliases.PreImage)
        {
            Entity? preImage = context.GetPreImage(targetIndex, alias);
            return MergeWithBulkTarget(context, targetIndex, preImage);
        }

        /// <summary>
        /// Gets post-image merged with target attributes for a specific target index in bulk messages.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="targetIndex">Target index.</param>
        /// <param name="alias">Image alias.</param>
        /// <returns>Merged entity.</returns>
        public static Entity GetPostImageMerged(this ILocalPluginContext context, int targetIndex, string alias = PluginImageAliases.PostImage)
        {
            Entity? postImage = context.GetPostImage(targetIndex, alias);
            return MergeWithBulkTarget(context, targetIndex, postImage);
        }

        private static Entity? GetEntityImage(EntityImageCollection images, string alias)
        {
            if (images == null || !images.Contains(alias))
            {
                return null;
            }

            return images[alias];
        }

        private static EntityImageCollection? GetBulkImageCollection(
            ILocalPluginContext context,
            int targetIndex,
            IReadOnlyList<EntityImageCollection>? imageCollections)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (targetIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetIndex), "Target index cannot be negative.");
            }

            if (imageCollections == null || targetIndex >= imageCollections.Count)
            {
                return null;
            }

            return imageCollections[targetIndex];
        }

        private static Entity MergeWithTarget(ILocalPluginContext context, Entity? image)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.TryGetTarget(out Entity? target) || target == null)
            {
                if (image == null)
                {
                    throw new InvalidPluginExecutionException("Neither image nor target is available for merge.");
                }

                // Clone, like the with-target branch: the merged result must be an independent copy so a caller
                // mutating it cannot corrupt the shared PreEntityImages/PostEntityImages entry.
                return CloneEntity(image);
            }

            Entity merged = image == null ? new Entity(target.LogicalName, target.Id) : CloneEntity(image);
            foreach (KeyValuePair<string, object> attribute in target.Attributes)
            {
                merged[attribute.Key] = attribute.Value;
            }

            return merged;
        }

        private static Entity MergeWithBulkTarget(ILocalPluginContext context, int targetIndex, Entity? image)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (targetIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetIndex), "Target index cannot be negative.");
            }

            if (!context.TryGetTargets(out EntityCollection? targets) || targets == null || targetIndex >= targets.Entities.Count)
            {
                if (image == null)
                {
                    throw new InvalidPluginExecutionException($"Neither image nor target is available for merge at index {targetIndex}.");
                }

                // Clone, like the with-target branch: the merged result must be independent of the shared image.
                return CloneEntity(image);
            }

            Entity target = targets.Entities[targetIndex];
            Entity merged = image == null ? new Entity(target.LogicalName, target.Id) : CloneEntity(image);
            foreach (KeyValuePair<string, object> attribute in target.Attributes)
            {
                merged[attribute.Key] = attribute.Value;
            }

            return merged;
        }

        private static Entity CloneEntity(Entity source)
        {
            Entity clone = new Entity(source.LogicalName, source.Id)
            {
                EntityState = source.EntityState,
                RowVersion = source.RowVersion
            };

            foreach (KeyValuePair<string, object> item in source.Attributes)
            {
                clone[item.Key] = item.Value;
            }

            return clone;
        }
    }
}
