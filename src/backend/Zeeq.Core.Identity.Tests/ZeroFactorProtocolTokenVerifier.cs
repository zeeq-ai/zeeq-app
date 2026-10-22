using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zeeq.Core.Identity.Tests;

internal static class ZeroFactorProtocolTokenVerifier
{
    internal static (string Subject, string Email) VerifyIdToken(
        string token,
        ZeroFactorRealmUser persona,
        string nonce,
        ZeroFactorProtocolMetadata metadata,
        string clientId
    )
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new ZeroFactorProtocolFailure("The ID token was not a signed compact JWT.");
        }

        using var header = ZeroFactorProtocolClient.ParseJson(
            ZeroFactorProtocolClient.DecodeBase64Url(parts[0])
        );
        if (ZeroFactorProtocolClient.ReadString(header.RootElement, "alg") != "RS256")
        {
            throw new ZeroFactorProtocolFailure("The ID token did not use RS256.");
        }

        var kid = ZeroFactorProtocolClient.ReadString(header.RootElement, "kid");
        var keys = metadata
            .Keys.RootElement.GetProperty("keys")
            .EnumerateArray()
            .Where(key => ZeroFactorProtocolClient.ReadString(key, "kid") == kid)
            .ToArray();
        if (keys.Length != 1 || ZeroFactorProtocolClient.ReadString(keys[0], "kty") != "RSA")
        {
            throw new ZeroFactorProtocolFailure("The ID token kid did not match one RSA JWKS key.");
        }

        using (var rsa = RSA.Create())
        {
            rsa.ImportParameters(
                new RSAParameters
                {
                    Modulus = ZeroFactorProtocolClient.DecodeBase64Url(
                        ZeroFactorProtocolClient.ReadString(keys[0], "n")
                    ),
                    Exponent = ZeroFactorProtocolClient.DecodeBase64Url(
                        ZeroFactorProtocolClient.ReadString(keys[0], "e")
                    ),
                }
            );
            if (
                !rsa.VerifyData(
                    Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
                    ZeroFactorProtocolClient.DecodeBase64Url(parts[2]),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1
                )
            )
            {
                throw new ZeroFactorProtocolFailure(
                    "The ID token signature did not verify against the JWKS key."
                );
            }
        }

        // Required wire claims are read explicitly so missing or incorrectly typed values fail.
        using var claims = ZeroFactorProtocolClient.ParseJson(
            ZeroFactorProtocolClient.DecodeBase64Url(parts[1])
        );
        var root = claims.RootElement;
        var issuedAt = root.GetProperty("iat").GetInt64();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var validAudience =
            root.GetProperty("aud").ValueKind == JsonValueKind.String
            && ZeroFactorProtocolClient.ReadString(root, "aud") == clientId;
        if (
            ZeroFactorProtocolClient.ReadString(root, "iss") != metadata.Issuer
            || !validAudience
            || ZeroFactorProtocolClient.ReadString(root, "sub") != persona.Id
            || ZeroFactorProtocolClient.ReadString(root, "nonce") != nonce
            || ZeroFactorProtocolClient.ReadString(root, "email") != persona.Email
            || root.GetProperty("email_verified").GetBoolean() != persona.EmailVerified
            || root.GetProperty("exp").GetInt64() <= now
            || issuedAt <= 0
            || issuedAt > now + 5
        )
        {
            throw new ZeroFactorProtocolFailure(
                "The signed ID token claims did not match identity or lifetime."
            );
        }

        return (
            ZeroFactorProtocolClient.ReadString(root, "sub"),
            ZeroFactorProtocolClient.ReadString(root, "email")
        );
    }
}
