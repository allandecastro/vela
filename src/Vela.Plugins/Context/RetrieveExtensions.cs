using System;
using System.Linq.Expressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Vela.Querying;

namespace Vela.Context
{
    /// <summary>
    /// Typed read of one record by its id. Together with <see cref="AlternateKeyExtensions.RetrieveByKey{TEntity}"/>
    /// (by alternate key) and <see cref="Query.For{TEntity}"/> (by criteria), it is the typed way to read records:
    /// the table comes from the early-bound type and the columns from typed selectors.
    /// </summary>
    public static class RetrieveExtensions
    {
        /// <summary>
        /// Retrieves the <typeparamref name="TEntity"/> record with the id <paramref name="id"/>, early-bound, with
        /// the columns the selectors name (<c>service.Retrieve&lt;Account&gt;(id, a =&gt; a.Name)</c>). It sends a
        /// real Retrieve, so the steps registered on Retrieve run and a missing record faults as it does in
        /// Dataverse. At least one column is required: there is no all-columns form.
        /// </summary>
        /// <typeparam name="TEntity">The early-bound table type; its logical name is the table read.</typeparam>
        /// <param name="service">The organization service.</param>
        /// <param name="id">The id of the record.</param>
        /// <param name="column">The first column to return.</param>
        /// <param name="columns">Further columns to return.</param>
        /// <returns>The record, as <typeparamref name="TEntity"/>.</returns>
        public static TEntity Retrieve<TEntity>(
            this IOrganizationService service,
            Guid id,
            Expression<Func<TEntity, object?>> column,
            params Expression<Func<TEntity, object?>>[] columns)
            where TEntity : Entity
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            if (id == Guid.Empty)
            {
                throw new ArgumentException("The id must not be empty.", nameof(id));
            }

            if (column == null)
            {
                throw new ArgumentNullException(nameof(column));
            }

            columns ??= [];
            Expression<Func<TEntity, object?>>[] selected = new Expression<Func<TEntity, object?>>[columns.Length + 1];
            selected[0] = column;
            Array.Copy(columns, 0, selected, 1, columns.Length);

            ColumnSet columnSet = TypedQueryNames.BuildColumnSet(selected);
            return service.Retrieve(TypedQueryNames.GetEntityLogicalName<TEntity>(), id, columnSet).ToEntity<TEntity>();
        }
    }
}
