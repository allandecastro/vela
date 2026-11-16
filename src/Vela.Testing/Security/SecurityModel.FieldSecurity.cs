using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    // Field-level security: secured columns and the read/update/create field permissions of a user and its teams.
    internal sealed partial class SecurityModel
    {
        /// <summary>Whether a column is field-secured (hidden/blocked unless a field permission grants it).</summary>
        public bool IsSecuredColumn(string entity, string column) =>
            _securedColumns.Count > 0 && _securedColumns.Contains((entity.ToLowerInvariant(), column.ToLowerInvariant()));

        /// <summary>Whether the user may read a field-secured column (directly or via a team).</summary>
        public bool CanReadColumn(Guid userId, string entity, string column) => FieldAllowed(_fieldRead, userId, entity, column);

        /// <summary>Whether the user may change a field-secured column on an existing record (directly or via a team).</summary>
        public bool CanUpdateColumn(Guid userId, string entity, string column) => FieldAllowed(_fieldUpdate, userId, entity, column);

        /// <summary>Whether the user may set a field-secured column when creating a record (directly or via a team).</summary>
        public bool CanCreateColumn(Guid userId, string entity, string column) => FieldAllowed(_fieldCreate, userId, entity, column);

        private bool FieldAllowed(HashSet<(Guid Principal, string Entity, string Column)> permissions, Guid userId, string entity, string column)
        {
            string e = entity.ToLowerInvariant();
            string c = column.ToLowerInvariant();
            if (permissions.Contains((userId, e, c)))
            {
                return true;
            }

            if (_memberships.TryGetValue(userId, out HashSet<Guid>? teams))
            {
                foreach (Guid teamId in teams)
                {
                    if (permissions.Contains((teamId, e, c)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
