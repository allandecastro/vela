using System;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Vela.Steps;

namespace Vela.Context
{
    /// <summary>
    /// Read/write helpers for alternate (business) keys. A generated key factory (for example
    /// <c>Account.ByAccountNumberKey("A-001")</c>) returns a key-based <see cref="EntityReference"/>; these read and
    /// upsert the record it identifies without ever knowing its GUID - the canonical integration pattern.
    /// </summary>
    public static class AlternateKeyExtensions
    {
        /// <summary>
        /// Retrieves the record identified by a key-based <see cref="EntityReference"/> (as produced by a generated
        /// <c>By...Key(...)</c> factory), early-bound as <typeparamref name="TEntity"/>. Pass the columns to return,
        /// or none for all columns. A relationship navigation property is not a column and is rejected here.
        /// </summary>
        public static TEntity RetrieveByKey<TEntity>(this IOrganizationService service, EntityReference key, params Expression<Func<TEntity, object?>>[] columns)
            where TEntity : Entity
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            ColumnSet columnSet = columns is { Length: > 0 }
                ? new ColumnSet(columns.Select(ExpressionHelper.GetAttributeLogicalName).ToArray())
                : new ColumnSet(true);

            RetrieveResponse response = (RetrieveResponse)service.Execute(new RetrieveRequest { Target = key, ColumnSet = columnSet });
            return response.Entity.ToEntity<TEntity>();
        }

        /// <summary>
        /// Creates or updates <paramref name="entity"/> matched by the business key <paramref name="key"/> (as
        /// produced by a generated <c>By...Key(...)</c> factory), in one call. The key attributes are copied onto the
        /// entity, so you set the values to write on <paramref name="entity"/> and identify the row with
        /// <paramref name="key"/>. Returns whether a new record was created and a reference to the affected row.
        /// </summary>
        public static (bool Created, EntityReference Target) UpsertByKey<TEntity>(this IOrganizationService service, TEntity entity, EntityReference key)
            where TEntity : Entity
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (entity == null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            foreach (System.Collections.Generic.KeyValuePair<string, object> pair in key.KeyAttributes)
            {
                entity.KeyAttributes[pair.Key] = pair.Value;
            }

            UpsertResponse response = (UpsertResponse)service.Execute(new UpsertRequest { Target = entity });
            return (response.RecordCreated, response.Target);
        }
    }
}
