using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Zeeq.Core.Identity;

/// <summary>
/// Helpers for RFC 7636 PKCE verifier/challenge values.
/// </summary>
public static class Pkce
{
    /// <summary>
    /// Creates a high-entropy PKCE code verifier for an external provider login.
    /// </summary>
    public static string CreateCodeVerifier() =>
        Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Computes the RFC 7636 S256 code challenge for a verifier.
    /// </summary>
    public static string CreateS256Challenge(string verifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
