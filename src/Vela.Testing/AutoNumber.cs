using System;
using System.Globalization;

namespace Vela.Testing
{
    /// <summary>
    /// Helpers for building auto-number value generators used with
    /// <see cref="InMemoryOrganizationService.WithAutoNumber(string, string, Func{int, object})"/>.
    /// </summary>
    public static class AutoNumber
    {
        /// <summary>
        /// Builds a sequential generator: <c>{prefix}{paddedNumber}{suffix}</c>. For example
        /// <c>Sequential("ACC-", 5)</c> yields <c>ACC-00001</c>, <c>ACC-00002</c>, …
        /// </summary>
        /// <param name="prefix">Text placed before the number.</param>
        /// <param name="length">Zero-padded width of the number (0 = no padding).</param>
        /// <param name="suffix">Text placed after the number.</param>
        public static Func<int, object> Sequential(string prefix = "", int length = 0, string suffix = "")
        {
            return number =>
            {
                string body = length > 0
                    ? number.ToString(new string('0', length), CultureInfo.InvariantCulture)
                    : number.ToString(CultureInfo.InvariantCulture);
                return prefix + body + suffix;
            };
        }
    }
}
