using System.Security.Cryptography;
using System.Text;

namespace DevFoundry.Api;

/// <summary>
/// The security-sensitive part of webhook intake, kept free of HTTP types so it can be unit tested:
/// a bounded body read and an HMAC-SHA256 signature check.
/// </summary>
public static class WebhookVerifier
{
    /// <summary>True when the header looks like a 64-character hex SHA-256 digest.</summary>
    public static bool IsWellFormedSignature(string signature) => Patterns.Signature().IsMatch(signature);

    /// <summary>
    /// Reads the whole stream, or returns null as soon as it exceeds <paramref name="maxBytes"/>.
    /// Counts streamed bytes because Content-Length is absent for chunked requests.
    /// </summary>
    public static async Task<byte[]?> ReadBoundedAsync(
        Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Constant-time check of a hex HMAC-SHA256 signature; the header must be well formed.</summary>
    public static bool IsValidSignature(string secret, byte[] body, string signatureHex)
    {
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(signatureHex));
    }
}
