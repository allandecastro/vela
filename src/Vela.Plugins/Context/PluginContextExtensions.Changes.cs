using System;
using System.Linq;
using System.Linq.Expressions;
using Vela.Constants;
using Vela.Steps;
using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    public static partial class PluginContextExtensions
    {
        /// <summary>
        /// Returns true when the selected attribute is present in the <c>Target</c> and its value differs from the
        /// registered pre-image — the common "only act when this field actually changed" guard.
        /// </summary>
        /// <remarks>
        /// Requires an <see cref="Entity"/> Target (Create/Update) and a registered pre-image. The message must
        /// carry an entity Target — Delete/Assign/SetState carry an <see cref="EntityReference"/> and throw. An
        /// attribute absent from the Target counts as unchanged (it wasn't part of this write).
        /// </remarks>
        /// <param name="context">The plugin context.</param>
        /// <param name="attribute">Lambda selecting the attribute, e.g. <c>x =&gt; x.Name</c>.</param>
        /// <param name="preImageAlias">The pre-image alias (defaults to the standard pre-image alias).</param>
        public static bool HasChanged<TEntity>(this ILocalPluginContext context, Expression<Func<TEntity, object?>> attribute, string preImageAlias = PluginImageAliases.PreImage)
            where TEntity : Entity
        {
            return HasChangedCore(context, preImageAlias, ExpressionHelper.GetAttributeName(attribute));
        }

        /// <summary>Returns true when <em>any</em> of the selected attributes changed (see <see cref="HasChanged{TEntity}"/>).</summary>
        public static bool HasAnyChanged<TEntity>(this ILocalPluginContext context, params Expression<Func<TEntity, object?>>[] attributes)
            where TEntity : Entity
        {
            if (attributes == null || attributes.Length == 0)
            {
                throw new ArgumentException("At least one attribute expression is required.", nameof(attributes));
            }

            return ExpressionHelper.GetAttributeNames(attributes).Any(name => HasChangedCore(context, PluginImageAliases.PreImage, name));
        }

        private static bool HasChangedCore(ILocalPluginContext context, string preImageAlias, string attribute)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.TryGetTarget(out Entity? target) || target == null)
            {
                throw new InvalidPluginExecutionException(
                    "HasChanged requires an Entity Target (Create/Update). The current message has no Entity Target — " +
                    "Delete/Assign/SetState carry an EntityReference, so there are no changed attribute values to compare.");
            }

            Entity? preImage = context.GetPreImage(preImageAlias);
            if (preImage == null)
            {
                throw new InvalidPluginExecutionException(
                    $"HasChanged requires a registered pre-image (alias '{preImageAlias}'). Register a pre-image on the step " +
                    "(it must include the attributes you compare), or pass the correct alias.");
            }

            // Not part of this write → not a change we can attribute to this operation.
            if (!target.Contains(attribute))
            {
                return false;
            }

            object? newValue = target[attribute];
            object? oldValue = preImage.Contains(attribute) ? preImage[attribute] : null;
            return !AttributeValueComparer.AreEqual(newValue, oldValue);
        }
    }
}
