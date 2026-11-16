using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;

namespace Vela.Testing
{
    /// <summary>
    /// Declared one-to-many (1:N) relationships and their <see cref="CascadeConfiguration"/>, so the store can
    /// apply relationship cascade behaviour (Delete/Assign/Share/… ) driven by the relationship's "character".
    /// N:N relationships live in <see cref="RelationshipRegistry"/>; this holds only 1:N.
    /// </summary>
    internal sealed class OneToManyRegistry
    {
        private readonly List<OneToMany> _relationships = new List<OneToMany>();

        /// <summary>
        /// Registers a relationship. A table can have only one parental relationship (Dataverse rejects a second one
        /// with 0x80047007, verified live), except over the same lookup column — a polymorphic lookup such as a
        /// customer lookup to account and contact is one parental relationship per target table.
        /// </summary>
        public void Add(OneToMany relationship)
        {
            // A schema name given by the test is the relationship's identity: no other relationship may carry it. A
            // synthesized name ({referenced}_{referencing}) may repeat, as before, for two lookups between one pair.
            if (_relationships.Any(r => (relationship.HasSchemaName || r.HasSchemaName)
                    && string.Equals(r.SchemaName, relationship.SchemaName, StringComparison.OrdinalIgnoreCase)))
            {
                throw InMemoryOrganizationService.DuplicateSchemaName(relationship.SchemaName);
            }

            if (relationship.IsParental)
            {
                OneToMany? existing = ParentRelationshipsOf(relationship.ReferencingEntity).FirstOrDefault(r =>
                    r.IsParental && !string.Equals(r.ReferencingAttribute, relationship.ReferencingAttribute, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    throw new InvalidOperationException(
                        $"Entity: {relationship.ReferencingEntity} is parented to Entity: {existing.ReferencedEntity} (through " +
                        $"'{existing.ReferencingAttribute}'). Cannot create another parental relation with Entity: " +
                        $"{relationship.ReferencedEntity} (through '{relationship.ReferencingAttribute}'). Dataverse allows one " +
                        "parental relationship per table (0x80047007); declare this one as referential, or with no Assign/" +
                        "Reparent/Share/Unshare cascade and a Delete behaviour other than Cascade.");
                }
            }

            _relationships.Add(relationship);
        }

        /// <summary>The relationships in which <paramref name="referencedEntity"/> is the parent (referenced) table.</summary>
        public IEnumerable<OneToMany> ChildRelationshipsOf(string referencedEntity) =>
            _relationships.Where(r => string.Equals(r.ReferencedEntity, referencedEntity, StringComparison.OrdinalIgnoreCase));

        /// <summary>The relationships in which <paramref name="referencingEntity"/> is the child (referencing) table.</summary>
        public IEnumerable<OneToMany> ParentRelationshipsOf(string referencingEntity) =>
            _relationships.Where(r => string.Equals(r.ReferencingEntity, referencingEntity, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Resolves a declared 1:N relationship by its schema name (used by Associate/Disassociate over 1:N): a declared
        /// schema name first, then a synthesized one.
        /// </summary>
        public bool TryResolveBySchema(string schemaName, out OneToMany relationship)
        {
            relationship = _relationships
                .Where(r => string.Equals(r.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.HasSchemaName ? 0 : 1)
                .FirstOrDefault()!;
            return relationship != null;
        }

        /// <summary>A 1:N relationship: a lookup (<see cref="ReferencingAttribute"/>) on the child pointing at the parent.</summary>
        internal sealed class OneToMany
        {
            public OneToMany(string schemaName, string referencedEntity, string referencingEntity, string referencingAttribute, CascadeConfiguration cascade, bool hasSchemaName = false)
            {
                SchemaName = schemaName;
                HasSchemaName = hasSchemaName;
                ReferencedEntity = referencedEntity;
                ReferencingEntity = referencingEntity;
                ReferencingAttribute = referencingAttribute;
                Cascade = cascade ?? new CascadeConfiguration();
            }

            public string SchemaName { get; }

            /// <summary>Whether <see cref="SchemaName"/> is the real schema name the test declared, not a synthesized one.</summary>
            public bool HasSchemaName { get; }

            /// <summary>The parent table (the "one" side).</summary>
            public string ReferencedEntity { get; }

            /// <summary>The child table (the "many" side) that carries the lookup.</summary>
            public string ReferencingEntity { get; }

            /// <summary>The lookup column on the child that points at the parent.</summary>
            public string ReferencingAttribute { get; }

            public CascadeConfiguration Cascade { get; }

            /// <summary>
            /// Whether the relationship is parental (Learn): any of Assign/Reparent/Share/Unshare cascades (All, Active
            /// or User-owned), or Delete is Cascade.
            /// </summary>
            public bool IsParental =>
                Cascades(Cascade.Assign) || Cascades(Cascade.Reparent) || Cascades(Cascade.Share) || Cascades(Cascade.Unshare) ||
                Cascade.Delete == CascadeType.Cascade;

            private static bool Cascades(CascadeType? type) =>
                type == CascadeType.Cascade || type == CascadeType.Active || type == CascadeType.UserOwned;
        }
    }
}
