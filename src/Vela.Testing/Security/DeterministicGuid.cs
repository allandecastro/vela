using System;
using System.Security.Cryptography;
using System.Text;

namespace Vela.Testing.Security
{
    /// <summary>
    /// Produces stable, reproducible GUIDs from their inputs — used for roles declared without an explicit id and for
    /// the security intersect rows (systemuserroles/teamroles/teammembership), so seeded data is deterministic.
    /// </summary>
    internal static class DeterministicGuid
    {
        public static Guid From(string first, string second) => Hash($"{first}|{second}");

        public static Guid From(string table, Guid first, Guid second) => Hash($"{table}|{first:D}|{second:D}");

        private static Guid Hash(string value)
        {
            using (MD5 md5 = MD5.Create())
            {
                // MD5 yields 16 bytes — exactly one GUID. Not for security, only for a stable identity.
                return new Guid(md5.ComputeHash(Encoding.UTF8.GetBytes(value)));
            }
        }
    }
}
