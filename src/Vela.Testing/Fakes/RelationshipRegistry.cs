using System;
using System.Collections.Generic;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing
{
    /// <summary>
    /// Holds the many-to-many relationships declared on <see cref="InMemoryOrganizationService"/>. The in-memory
    /// store has no metadata, so a test declares a relationship's intersect entity and its two lookup columns
    /// once; <c>Associate</c>/<c>Disassociate</c> then create/delete the intersect rows that back the N:N link.
    /// </summary>
    internal sealed class RelationshipRegistry
    {
        private readonly Dictionary<string, ManyToMany> _relationships =
            new Dictionary<string, ManyToMany>(StringComparer.OrdinalIgnoreCase);

        public void Add(string schemaName, string intersectEntity, string firstEntity, string firstAttribute, string secondEntity, string secondAttribute)
        {
            if (string.IsNullOrWhiteSpace(schemaName))
            {
                throw new ArgumentException("Relationship schema name is required.", nameof(schemaName));
            }

            if (string.IsNullOrWhiteSpace(intersectEntity))
            {
                throw new ArgumentException("Intersect entity is required.", nameof(intersectEntity));
            }

            if (string.IsNullOrWhiteSpace(firstEntity))
            {
                throw new ArgumentException("First entity is required.", nameof(firstEntity));
            }

            if (string.IsNullOrWhiteSpace(firstAttribute))
            {
                throw new ArgumentException("First intersect attribute is required.", nameof(firstAttribute));
            }

            if (string.IsNullOrWhiteSpace(secondEntity))
            {
                throw new ArgumentException("Second entity is required.", nameof(secondEntity));
            }

            if (string.IsNullOrWhiteSpace(secondAttribute))
            {
                throw new ArgumentException("Second intersect attribute is required.", nameof(secondAttribute));
            }

            _relationships[schemaName] = new ManyToMany(intersectEntity, firstEntity, firstAttribute, secondEntity, secondAttribute);
        }

        /// <summary>Looks up a declared relationship without throwing.</summary>
        public bool TryResolve(string schemaName, out ManyToMany relationship) =>
            _relationships.TryGetValue(schemaName, out relationship);

        /// <summary>Every declared N:N relationship (used to clean up dangling intersect rows on delete).</summary>
        public IEnumerable<ManyToMany> All => _relationships.Values;

        /// <summary>
        /// A declared N:N relationship: the intersect entity and the two lookup columns, each tied to a table.
        /// </summary>
        internal readonly struct ManyToMany
        {
            public ManyToMany(string intersectEntity, string firstEntity, string firstAttribute, string secondEntity, string secondAttribute)
            {
                IntersectEntity = intersectEntity;
                FirstEntity = firstEntity;
                FirstAttribute = firstAttribute;
                SecondEntity = secondEntity;
                SecondAttribute = secondAttribute;
            }

            public string IntersectEntity { get; }

            public string FirstEntity { get; }

            public string FirstAttribute { get; }

            public string SecondEntity { get; }

            public string SecondAttribute { get; }

            /// <summary>
            /// Returns the intersect attribute for the calling side and for the related side. Non-self-referential
            /// relationships map by logical name; a self-referential relationship maps by the role of the calling
            /// record: Referencing (or no role) puts it in the first column, Referenced in the second (verified live).
            /// </summary>
            public (string callerAttribute, string relatedAttribute) AttributesFor(string callerEntity, EntityRole? role = null)
            {
                bool selfReferential = string.Equals(FirstEntity, SecondEntity, StringComparison.OrdinalIgnoreCase);
                bool callerIsSecond = selfReferential
                    ? role == EntityRole.Referenced
                    : string.Equals(callerEntity, SecondEntity, StringComparison.OrdinalIgnoreCase);
                return callerIsSecond ? (SecondAttribute, FirstAttribute) : (FirstAttribute, SecondAttribute);
            }
        }
    }
}
