using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Constants;
using Vela.Context;

namespace Vela.Steps;

/// <summary>
/// The identity of a plugin's steps: the step key each one is deployed under, and which one a pipeline execution
/// belongs to. Several steps may share a trigger (message + stage + mode + entity); they are told apart by their
/// filtering attributes, images, relationship scope or explicit step key. The execution order (rank) is not part of
/// the identity, so changing it updates the step in place.
/// </summary>
internal static class StepIdentity
{
    private const string AutoKeyPrefix = "dv-";

    // Step id -> step name, for an execution whose OwningExtension carries the id but not the name (an asynchronous
    // step: verified live, its OwningExtension.Name is null). Per process: a step id never changes name in a run.
    private static readonly ConcurrentDictionary<Guid, string> StepNamesById = new ConcurrentDictionary<Guid, string>();

    /// <summary>
    /// Gives every keyless definition its automatic step key and rejects two steps that resolve to the same key.
    /// The first keyless step of a trigger keeps the historical seed (plugin, message, stage, mode, entity and a
    /// fixed rank of 1), so a step deployed at the default rank keeps its key. Each further keyless step of the same
    /// trigger adds its filtering attributes, images and relationship scope to the seed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Two steps resolve to the same step key.</exception>
    public static void AssignStepKeys(IReadOnlyList<StepDefinition> definitions, string pluginTypeName)
    {
        HashSet<string> seededTriggers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> shapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StepDefinition definition in definitions)
        {
            if (!string.IsNullOrWhiteSpace(definition.StepKey))
            {
                continue;
            }

            string trigger = Trigger(definition);
            if (!shapes.Add(trigger + "|" + Discriminator(definition)))
            {
                throw new InvalidOperationException(
                    $"Plugin '{pluginTypeName}' registers two steps for the same trigger (message '{definition.Message}', stage {(int)definition.Stage}, mode {(int)definition.Mode}) that cannot be told apart: they have the same filtering attributes and images. Give one of them a .WithStepKey(\"...\"), or merge them into one step.");
            }

            string seed = string.Join("|", pluginTypeName, trigger, "1");
            if (!seededTriggers.Add(trigger))
            {
                seed += "|" + Discriminator(definition);
            }

            definition.StepKey = AutoKeyPrefix + Hash(seed);
        }

        Dictionary<string, StepDefinition> byKey = new Dictionary<string, StepDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (StepDefinition definition in definitions)
        {
            if (byKey.TryGetValue(definition.StepKey, out StepDefinition? other))
            {
                bool sameTrigger = string.Equals(Trigger(definition), Trigger(other), StringComparison.OrdinalIgnoreCase);
                throw new InvalidOperationException(sameTrigger
                    ? $"Plugin '{pluginTypeName}' registers two steps for the same trigger (message '{definition.Message}', stage {(int)definition.Stage}, mode {(int)definition.Mode}) that cannot be told apart: they have the same filtering attributes, images and step key. Give one of them a distinct .WithStepKey(\"...\"), or merge them into one step."
                    : $"Plugin '{pluginTypeName}' registers two steps with the step key '{definition.StepKey}'. Step keys must be unique within a plugin.");
            }

            byKey[definition.StepKey] = definition;
        }
    }

    /// <summary>
    /// The definitions that apply to this execution, from those matching its message, stage, mode and entity:
    /// relationship-scoped Associate/Disassociate steps whose relationship is not the request's drop out, and when
    /// several steps share the trigger, the executing step (<c>OwningExtension</c>) selects its own.
    /// </summary>
    /// <exception cref="InvalidPluginExecutionException">Several steps match and the executing step cannot be identified.</exception>
    public static List<StepDefinition> Select(List<StepDefinition> matches, ILocalPluginContext ctx, string pluginTypeName)
    {
        List<StepDefinition> inScope = matches.Where(definition => IsInRelationshipScope(definition, ctx)).ToList();
        if (inScope.Count <= 1)
        {
            return inScope;
        }

        string? stepName = ExecutingStepName(ctx);
        if (!string.IsNullOrEmpty(stepName))
        {
            List<StepDefinition> owned = inScope.Where(definition => NameCarriesKey(stepName!, definition.StepKey)).ToList();
            if (owned.Count == 1)
            {
                return owned;
            }
        }

        throw new InvalidPluginExecutionException(
            $"Plugin '{pluginTypeName}' has {inScope.Count} steps for message '{ctx.MessageName}' at stage {ctx.Stage}, mode {ctx.Mode}, " +
            $"and the executing step '{stepName}' does not name one of their step keys ({string.Join(", ", inScope.Select(d => d.StepKey))}). " +
            "Redeploy the plugin with 'vela apply' so each step carries its key in its name.");
    }

    /// <summary>
    /// Whether <paramref name="definition"/> applies to the request's relationship. Always true for a step with no
    /// relationship scope, or when the request carries no relationship.
    /// </summary>
    public static bool IsInRelationshipScope(StepDefinition definition, ILocalPluginContext ctx)
    {
        if (definition.Relationships == null || definition.Relationships.Length == 0)
        {
            return true;
        }

        ParameterCollection? inputs = ctx.PluginExecutionContext?.InputParameters;
        if (inputs == null || !inputs.Contains("Relationship") || inputs["Relationship"] is not Relationship relationship)
        {
            return true;
        }

        return definition.Relationships.Any(name => string.Equals(name, relationship.SchemaName, StringComparison.OrdinalIgnoreCase));
    }

    // "{plugin} - {lifecycle}: {stepKey}": the key follows the last ": ".
    private static bool NameCarriesKey(string stepName, string stepKey)
    {
        string name = stepName.Trim();
        return string.Equals(name, stepKey, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(": " + stepKey, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExecutingStepName(ILocalPluginContext ctx)
    {
        EntityReference? owning = ctx.PluginExecutionContext?.OwningExtension;
        if (owning == null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(owning.Name))
        {
            return owning.Name;
        }

        if (owning.Id == Guid.Empty)
        {
            return null;
        }

        return StepNamesById.GetOrAdd(owning.Id, id =>
            ctx.SystemService.Retrieve("sdkmessageprocessingstep", id, new ColumnSet("name"))
                .GetAttributeValue<string>("name") ?? string.Empty);
    }

    private static string Trigger(StepDefinition definition) => string.Join("|",
        definition.Message ?? string.Empty,
        (int)definition.Stage,
        (int)definition.Mode,
        definition.EntityLogicalName ?? string.Empty);

    private static string Discriminator(StepDefinition definition)
    {
        static string List(IEnumerable<string>? values) => string.Join(",",
            (values ?? Enumerable.Empty<string>()).Select(v => v.ToLowerInvariant()).OrderBy(v => v, StringComparer.Ordinal));

        static string Images(ImageDefinition[]? images) => string.Join(";",
            (images ?? Array.Empty<ImageDefinition>())
                .Select(image => image.Alias + ":" + List(image.Attributes))
                .OrderBy(v => v, StringComparer.Ordinal));

        return $"filter={List(definition.FilteringAttributes)}|pre={Images(definition.PreImages)}|post={Images(definition.PostImages)}|rel={List(definition.Relationships)}";
    }

    private static string Hash(string seed)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant().Substring(0, 12);
    }
}
