using System;
using Microsoft.Xrm.Sdk;

namespace Vela.Testing.Security
{
    /// <summary>
    /// The two principal tables a record can be owned by or shared with, and the case-insensitive tests on a
    /// reference's logical name the security code repeats.
    /// </summary>
    internal static class PrincipalKind
    {
        public const string SystemUser = "systemuser";

        public const string Team = "team";

        public static bool IsUser(EntityReference? principal) =>
            principal != null && string.Equals(principal.LogicalName, SystemUser, StringComparison.OrdinalIgnoreCase);

        public static bool IsTeam(EntityReference? principal) =>
            principal != null && string.Equals(principal.LogicalName, Team, StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="principal"/> is the user <paramref name="userId"/>.</summary>
        public static bool IsUser(EntityReference? principal, Guid userId) => IsUser(principal) && principal!.Id == userId;
    }
}
