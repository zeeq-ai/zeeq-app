using System.Security.Cryptography;
using System.Text;

namespace Zeeq.Core.Documents;

/// <summary>Computes the canonical content fingerprint used for document concurrency checks.</summary>
public static class DocumentContentHash
{
    /// <summary>Returns the lowercase SHA-256 hash of UTF-8 content.</summary>
    public static string Compute(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
