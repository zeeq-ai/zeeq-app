using System.Security.Cryptography;
using System.Text;

namespace Zeeq.Core.Common;

/// <summary>
/// Local extensions for strings to make it easier to work with common string operations.
/// </summary>
public static class StringExtensions
{
    extension(String str)
    {
        /// <summary>
        /// Returns a given string as a lowercase hex encoded string.
        /// </summary>
        /// <returns>
        /// Hex encoded string representation of the input string; empty if the
        /// string is null or empty.
        /// </returns>
        public string AsHexString()
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }

            return Convert.ToHexStringLower(Encoding.UTF8.GetBytes(str));
        }

        /// <summary>
        /// Returns a given string as a lowercase hex encoded SHA256 hash.
        /// </summary>
        /// <returns>
        /// Hex encoded SHA256 hash of the input string; empty if the string is null or empty.
        /// </returns>
        public string AsHashedHexString()
        {
            if (string.IsNullOrEmpty(str))
            {
                return string.Empty;
            }

            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(str));

            return Convert.ToHexStringLower(hashBytes);
        }
    }
}
