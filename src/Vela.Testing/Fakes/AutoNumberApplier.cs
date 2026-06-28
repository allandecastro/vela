using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// Holds the auto-number rules declared on <see cref="InMemoryOrganizationService"/> and populates the
    /// configured attribute on Create when it is absent or empty, mirroring the platform's auto-number column.
    /// Each rule keeps its own 1-based counter.
    /// </summary>
    internal sealed class AutoNumberApplier
    {
        private readonly List<Rule> _rules = new List<Rule>();

        public void Add(string entityLogicalName, string attribute, Func<int, object> generator)
        {
            _rules.Add(new Rule
            {
                Entity = entityLogicalName,
                Attribute = attribute,
                Generator = generator ?? throw new ArgumentNullException(nameof(generator))
            });
        }

        public void Apply(Entity entity)
        {
            foreach (Rule rule in _rules)
            {
                if (!string.Equals(rule.Entity, entity.LogicalName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                object? current = EntityValues.GetValue(entity, rule.Attribute);
                bool isEmpty = current == null || (current is string text && text.Length == 0);
                if (isEmpty)
                {
                    entity[rule.Attribute] = rule.Generator(++rule.Counter);
                }
            }
        }

        private sealed class Rule
        {
            public string Entity { get; set; } = string.Empty;

            public string Attribute { get; set; } = string.Empty;

            public Func<int, object> Generator { get; set; } = _ => string.Empty;

            public int Counter { get; set; }
        }
    }
}
