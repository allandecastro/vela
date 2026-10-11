using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Xrm.Sdk;

namespace Vela.Context
{
    /// <summary>
    /// Diagnostics surface of <see cref="PluginContextExtensions"/>: method tracing, execution
    /// snapshots, and the value/entity formatting helpers they rely on. Split into its own partial
    /// file to keep the parameter/image accessors (the other partial) focused and small.
    /// </summary>
    public static partial class PluginContextExtensions
    {
        /// <summary>
        /// Traces the current method name with optional details.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="details">Optional details.</param>
        /// <param name="methodName">Caller method name.</param>
        public static void TraceMethod(this ILocalPluginContext context, string? details = null, [CallerMemberName] string methodName = "")
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            string nl = Environment.NewLine;
            context.Trace(
                "[{0:o}] method trace{1}" +
                "  method      : {2}{1}" +
                "  message     : {3}{1}" +
                "  stage/mode  : {4}/{5}{1}" +
                "  depth       : {6}{1}" +
                "  correlation : {7}{1}" +
                "  details     : {8}",
                DateTime.UtcNow,
                nl,
                methodName,
                context.MessageName,
                context.Stage,
                context.Mode,
                context.Depth,
                context.CorrelationId,
                details ?? string.Empty);
        }

        /// <summary>
        /// Traces an execution snapshot (message, stage, ids, then the input/output parameters, images and target) to
        /// the plug-in trace log only: it never reaches <see cref="ILocalPluginContext.Logger"/> or Application Insights.
        /// </summary>
        /// <param name="context">Plugin context.</param>
        /// <param name="phase">Logical phase name.</param>
        /// <param name="detail">
        /// <see cref="TraceSnapshotDetail.Full"/> (the default for an explicit call) includes attribute and parameter
        /// values; <see cref="TraceSnapshotDetail.Metadata"/> lists only names, ids and counts, so no record data
        /// (personal data, secrets) is written; <see cref="TraceSnapshotDetail.None"/> writes nothing.
        /// </param>
        public static void TraceExecutionSnapshot(this ILocalPluginContext context, string phase, TraceSnapshotDetail detail = TraceSnapshotDetail.Full)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (detail == TraceSnapshotDetail.None)
            {
                return;
            }

            string safePhase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase;
            StringBuilder trace = new StringBuilder();
            string nl = Environment.NewLine;
            trace.AppendFormat("[{0:o}] ExecutionSnapshot{1}", DateTime.UtcNow, nl);
            trace.AppendFormat("  Phase: {0}{1}", safePhase, nl);
            trace.AppendFormat("  MessageName: {0}{1}", context.MessageName, nl);
            trace.AppendFormat("  Stage: {0}{1}", context.Stage, nl);
            trace.AppendFormat("  Mode: {0}{1}", context.Mode, nl);
            trace.AppendFormat("  Depth: {0}{1}", context.Depth, nl);
            trace.AppendFormat("  CorrelationId: {0}{1}", context.CorrelationId, nl);
            trace.AppendFormat("  PrimaryEntityName: {0}{1}", context.PrimaryEntityName, nl);
            trace.AppendFormat("  PrimaryEntityId: {0}{1}", context.PrimaryEntityId, nl);
            trace.AppendFormat("  UserId: {0}{1}", context.UserId, nl);
            trace.AppendFormat("  InitiatingUserId: {0}{1}", context.InitiatingUserId, nl);
            trace.AppendFormat("  IsApplicationUser: {0}{1}", context.IsApplicationUser, nl);
            trace.AppendFormat("  IsPortalsClientCall: {0}{1}", context.IsPortalsClientCall, nl);
            trace.AppendFormat("  PortalsContactId: {0}{1}", context.PortalsContactId?.ToString() ?? "<none>", nl);
            trace.AppendFormat("  InitiatingUserAgent: {0}{1}", string.IsNullOrWhiteSpace(context.InitiatingUserAgent) ? "<none>" : context.InitiatingUserAgent, nl);
            trace.AppendFormat("  EnvironmentId: {0}{1}", context.EnvironmentId ?? "<none>", nl);
            trace.AppendFormat("  TenantId: {0}{1}", context.TenantId?.ToString() ?? "<none>", nl);
            trace.AppendFormat("  IsBulkOperation: {0}{1}", context.IsBulkOperation, nl);
            if (context.IsBulkOperation)
            {
                trace.AppendFormat("  BulkTargetsCount: {0}{1}", context.BulkTargets?.Entities.Count ?? 0, nl);
                trace.AppendFormat("  BulkPreEntityImagesCount: {0}{1}", context.BulkPreEntityImagesCollection.Count, nl);
                trace.AppendFormat("  BulkPostEntityImagesCount: {0}{1}", context.BulkPostEntityImagesCollection.Count, nl);
            }
            // Fail closed: only exactly Full traces values; any other value (including an out-of-range cast) is metadata.
            if (detail != TraceSnapshotDetail.Full)
            {
                trace.AppendFormat("  InputParameters: {0}{1}", DescribeParameters(context.PluginExecutionContext.InputParameters), nl);
                trace.AppendFormat("  OutputParameters: {0}{1}", DescribeParameters(context.PluginExecutionContext.OutputParameters), nl);
                trace.AppendFormat("  PreEntityImages: {0}{1}", DescribeImages(context.PluginExecutionContext.PreEntityImages), nl);
                trace.AppendFormat("  PostEntityImages: {0}{1}", DescribeImages(context.PluginExecutionContext.PostEntityImages), nl);
                trace.AppendFormat("  Target: {0}", TryGetTarget(context, out Entity? metadataTarget) && metadataTarget != null ? DescribeEntity(metadataTarget) : "<none>");
            }
            else
            {
                trace.AppendLine("  InputParameters:");
                trace.AppendLine(FormatParameterCollection(context.PluginExecutionContext.InputParameters, 2));
                trace.AppendLine("  OutputParameters:");
                trace.AppendLine(FormatParameterCollection(context.PluginExecutionContext.OutputParameters, 2));
                trace.AppendLine("  PreEntityImages:");
                trace.AppendLine(FormatImageCollection(context.PluginExecutionContext.PreEntityImages, 2));
                trace.AppendLine("  PostEntityImages:");
                trace.AppendLine(FormatImageCollection(context.PluginExecutionContext.PostEntityImages, 2));
                trace.AppendLine("  Target:");
                trace.Append(FormatTarget(context, 2));
            }

            // Straight to the plug-in trace log: Trace() would also fan the snapshot out to ILogger / Application
            // Insights, copying record data into telemetry.
            if (context.TracingService != null)
            {
                context.TracingService.Trace("{0}", trace.ToString());
            }
            else
            {
                context.Trace(trace.ToString());
            }
        }

        // Metadata-only descriptions: names, types, ids and counts, never values.
        private static string DescribeParameters(ParameterCollection parameters) =>
            parameters == null || parameters.Count == 0
                ? "<none>"
                : string.Join("; ", parameters.Keys.Cast<string>().OrderBy(x => x, StringComparer.Ordinal).Select(key => $"{key}={DescribeValue(parameters[key])}"));

        private static string DescribeImages(EntityImageCollection images) =>
            images == null || images.Count == 0
                ? "<none>"
                : string.Join("; ", images.Keys.Cast<string>().OrderBy(x => x, StringComparer.Ordinal).Select(alias => $"{alias}={DescribeEntity(images[alias])}"));

        private static string DescribeEntity(Entity entity) =>
            entity == null
                ? "<null>"
                : $"{entity.LogicalName}:{entity.Id} [{string.Join(", ", entity.Attributes.Keys.OrderBy(x => x, StringComparer.Ordinal))}]";

        private static string DescribeValue(object? value) => value switch
        {
            null => "<null>",
            Entity entity => DescribeEntity(entity),
            EntityReference reference => $"{reference.LogicalName}:{reference.Id}",
            EntityCollection collection => $"EntityCollection(Count={collection.Entities.Count}, {collection.EntityName})",
            Guid guid => guid.ToString(),
            _ => $"({value.GetType().Name})",
        };

        private static string FormatTarget(ILocalPluginContext context, int indentLevel)
        {
            if (!TryGetTarget(context, out Entity? target) || target == null)
            {
                return $"{Indent(indentLevel)}<none>";
            }

            return FormatEntity(target, indentLevel);
        }

        private static string FormatParameterCollection(ParameterCollection parameters, int indentLevel)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return $"{Indent(indentLevel)}<none>";
            }

            StringBuilder output = new StringBuilder();
            foreach (string key in parameters.Keys.Cast<string>().OrderBy(x => x, StringComparer.Ordinal))
            {
                output.AppendFormat("{0}{1}:{2}", Indent(indentLevel), key, Environment.NewLine);
                output.AppendLine(FormatValue(parameters[key], indentLevel + 1));
            }

            return TrimTrailingNewLine(output.ToString());
        }

        private static string FormatImageCollection(EntityImageCollection images, int indentLevel)
        {
            if (images == null || images.Count == 0)
            {
                return $"{Indent(indentLevel)}<none>";
            }

            StringBuilder output = new StringBuilder();
            foreach (string alias in images.Keys.Cast<string>().OrderBy(x => x, StringComparer.Ordinal))
            {
                output.AppendFormat("{0}{1}:{2}", Indent(indentLevel), alias, Environment.NewLine);
                output.AppendLine(FormatEntity(images[alias], indentLevel + 1));
            }

            return TrimTrailingNewLine(output.ToString());
        }

        private static string FormatEntity(Entity entity, int indentLevel)
        {
            StringBuilder output = new StringBuilder();
            output.AppendFormat("{0}LogicalName: {1}{2}", Indent(indentLevel), entity.LogicalName, Environment.NewLine);
            output.AppendFormat("{0}Id: {1}{2}", Indent(indentLevel), entity.Id, Environment.NewLine);

            if (entity.Attributes.Count == 0)
            {
                output.AppendFormat("{0}Attributes: <none>", Indent(indentLevel));
                return output.ToString();
            }

            output.AppendFormat("{0}Attributes:{1}", Indent(indentLevel), Environment.NewLine);
            foreach (string key in entity.Attributes.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                object? value = entity.Attributes[key];
                if (TryFormatInlineValue(value, out string inline))
                {
                    output.AppendFormat("{0}{1}: {2}{3}", Indent(indentLevel + 1), key, inline, Environment.NewLine);
                    continue;
                }

                output.AppendFormat("{0}{1}:{2}", Indent(indentLevel + 1), key, Environment.NewLine);
                output.AppendLine(FormatValue(value, indentLevel + 2));
            }

            return TrimTrailingNewLine(output.ToString());
        }

        private static string FormatValue(object? value, int indentLevel)
        {
            if (TryFormatInlineValue(value, out string inline))
            {
                return $"{Indent(indentLevel)}{inline}";
            }

            if (value is Entity entity)
            {
                return FormatEntity(entity, indentLevel);
            }

            if (value is EntityCollection collection)
            {
                if (collection.Entities.Count == 0)
                {
                    return $"{Indent(indentLevel)}EntityCollection(Count=0)";
                }

                StringBuilder output = new StringBuilder();
                output.AppendFormat("{0}EntityCollection(Count={1}):{2}", Indent(indentLevel), collection.Entities.Count, Environment.NewLine);
                for (int i = 0; i < collection.Entities.Count; i++)
                {
                    output.AppendFormat("{0}[{1}]:{2}", Indent(indentLevel + 1), i, Environment.NewLine);
                    output.AppendLine(FormatEntity(collection.Entities[i], indentLevel + 2));
                }

                return TrimTrailingNewLine(output.ToString());
            }

            if (value is ParameterCollection parameters)
            {
                return FormatParameterCollection(parameters, indentLevel);
            }

            return $"{Indent(indentLevel)}{value}";
        }

        private static bool TryFormatInlineValue(object? value, out string inline)
        {
            switch (value)
            {
                case null:
                    inline = "<null>";
                    return true;
                case string text:
                    inline = $"\"{text}\"";
                    return true;
                case DateTime dateTime:
                    inline = dateTime.ToString("o");
                    return true;
                case bool flag:
                    inline = flag ? "true" : "false";
                    return true;
                case OptionSetValue optionSetValue:
                    inline = $"{optionSetValue.Value} (OptionSetValue)";
                    return true;
                case Money money:
                    inline = $"{money.Value} (Money)";
                    return true;
                case EntityReference reference:
                    inline = $"{reference.LogicalName}:{reference.Id} Name=\"{reference.Name ?? string.Empty}\"";
                    return true;
                case AliasedValue aliased:
                    inline = $"AliasedValue[{aliased.AttributeLogicalName}]={FormatAliasedInline(aliased.Value)}";
                    return true;
                case Guid guid:
                    inline = guid.ToString();
                    return true;
                case int or long or short or decimal or double or float:
                    inline = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    return true;
                case Entity:
                case EntityCollection:
                case ParameterCollection:
                    inline = string.Empty;
                    return false;
                default:
                    inline = value.ToString() ?? string.Empty;
                    return true;
            }
        }

        private static string FormatAliasedInline(object? value)
        {
            if (TryFormatInlineValue(value, out string inline))
            {
                return inline;
            }

            return value?.ToString() ?? "<null>";
        }

        private static string Indent(int level)
        {
            return new string(' ', level * 2);
        }

        private static string TrimTrailingNewLine(string value)
        {
            return value.TrimEnd('\r', '\n');
        }
    }
}
