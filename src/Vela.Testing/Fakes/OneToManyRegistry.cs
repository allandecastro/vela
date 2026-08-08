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

        public void Add(OneToMany relationship) => _relationships.Add(relationship);

        /// <summary>The relationships in which <paramref name="referencedEntity"/> is the parent (referenced) table.</summary>
        public IEnumerable<OneToMany> ChildRelationshipsOf(string referencedEntity) =>
            _relationships.Where(r => string.Equals(r.ReferencedEntity, referencedEntity, StringComparison.OrdinalIgnoreCase));

        /// <summary>A 1:N relationship: a lookup (<see cref="ReferencingAttribute"/>) on the child pointing at the parent.</summary>
        internal sealed class OneToMany
        {
            public OneToMany(string schemaName, string referencedEntity, string referencingEntity, string referencingAttribute, CascadeConfiguration cascade)
            {
                SchemaName = schemaName;
                ReferencedEntity = referencedEntity;
                ReferencingEntity = referencingEntity;
                ReferencingAttribute = referencingAttribute;
                Cascade = cascade ?? new CascadeConfiguration();
            }

            public string SchemaName { get; }

            /// <summary>The parent table (the "one" side).</summary>
            public string ReferencedEntity { get; }

            /// <summary>The child table (the "many" side) that carries the lookup.</summary>
            public string ReferencingEntity { get; }

            /// <summary>The lookup column on the child that points at the parent.</summary>
            public string ReferencingAttribute { get; }

            public CascadeConfiguration Cascade { get; }
        }
    }
}
