using System;
using System.Collections.Generic;

namespace Vela.StepBuilder;

/// <summary>
/// Helpers for working with step definition collections.
/// </summary>
public static class StepDefinitionCollectionExtensions
{
    /// <summary>
    /// Adds multiple step definitions to a collection.
    /// </summary>
    /// <param name="steps">The destination collection.</param>
    /// <param name="definitions">The step definitions to add.</param>
    public static void AddRange(this ICollection<StepDefinition> steps, IEnumerable<StepDefinition> definitions)
    {
        steps = steps ?? throw new ArgumentNullException(nameof(steps));
        definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

        foreach (StepDefinition definition in definitions)
        {
            steps.Add(definition);
        }
    }
}
