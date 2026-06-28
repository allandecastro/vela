using System;
using System.Collections.Generic;

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

            _relationships[schemaName] = new ManyToMany(intersectEntity, firstEntity, firstAttribute, secondEntity, secondAttribute);
        }

        /// <summary>Returns the declared relationship or throws with guidance to declare it via <c>WithManyToMany</c>.</summary>
        public ManyToMany Resolve(string schemaName)
        {
            if (_relationships.TryGetValue(schemaName, out ManyToMany relationship))
            {
                return relationship;
            }

            throw new InvalidOperationException(
                $"Many-to-many relationship '{schemaName}' is not declared. Declare it on the in-memory service with " +
                $"WithManyToMany(\"{schemaName}\", intersectEntity, firstEntity, firstAttribute, secondEntity, secondAttribute) " +
                $"or register an EntityMetadata carrying the relationship via WithEntityMetadata(...).");
        }

        /// <summary>Looks up a declared relationship without throwing.</summary>
        public bool TryResolve(string schemaName, out ManyToMany relationship) =>
            _relationships.TryGetValue(schemaName, out relationship);

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
            /// relationships map by logical name; a self-referential relationship maps positionally
            /// (caller → first, related → second).
            /// </summary>
            public (string callerAttribute, string relatedAttribute) AttributesFor(string callerEntity)
            {
                bool selfReferential = string.Equals(FirstEntity, SecondEntity, StringComparison.OrdinalIgnoreCase);
                if (!selfReferential && string.Equals(callerEntity, SecondEntity, StringComparison.OrdinalIgnoreCase))
                {
                    return (SecondAttribute, FirstAttribute);
                }

                return (FirstAttribute, SecondAttribute);
            }
        }
    }
}
