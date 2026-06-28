using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// Entry points for building a fake plugin execution context. Use <see cref="For{TEntity}"/> for
    /// entity plugins (typed Target/images, primary entity name derived from <c>[EntityLogicalName]</c>)
    /// and <see cref="ForMessage"/> for Custom APIs or message-only plugins.
    /// </summary>
    public static class PluginContext
    {
        /// <summary>Starts a strongly-typed builder for an entity plugin.</summary>
        public static PluginContextBuilder<TEntity> For<TEntity>()
            where TEntity : Entity
        {
            return new PluginContextBuilder<TEntity>();
        }

        /// <summary>Starts a builder for a given message (Custom API or message-only plugin).</summary>
        public static PluginContextBuilder ForMessage(string messageName)
        {
            PluginContextBuilder builder = new PluginContextBuilder();
            builder.OnMessage(messageName);
            return builder;
        }
    }
}
