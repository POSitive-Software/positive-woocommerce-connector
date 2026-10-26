using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PA.Connectors.WooCommerce;

/// <summary>
/// Woo webhook authentication: <c>X-WC-Webhook-Signature</c> =
/// base64(HMAC-SHA256(raw body, webhook secret)) — confirmed on the dev store
/// (S0bc). Verify the RAW bytes before parsing anything; compare in constant time.
/// </summary>
public static class WooWebhookSignature
{
    public const string HeaderName = "X-WC-Webhook-Signature";

    private static readonly Regex PingBody = new(@"^webhook_id=\d+$", RegexOptions.CultureInvariant);

    /// <summary>base64(HMAC-SHA256(body, secret)).</summary>
    public static string Compute(ReadOnlySpan<byte> rawBody, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody));
    }

    /// <summary>
    /// True only when <paramref name="header"/> is a well-formed base64 HMAC of
    /// exactly these bytes under <paramref name="secret"/>. A missing or blank
    /// header, a missing secret, or malformed base64 is false — never an exception.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> rawBody, string? secret, string? header)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(header))
            return false;

        Span<byte> presented = stackalloc byte[64];
        if (!Convert.TryFromBase64String(header.Trim(), presented, out var written) || written != HMACSHA256.HashSizeInBytes)
            return false;

        Span<byte> expected = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody, expected);
        return CryptographicOperations.FixedTimeEquals(expected, presented[..written]);
    }

    /// <summary>
    /// Woo's creation ping: a form post (<c>application/x-www-form-urlencoded</c>)
    /// whose body is just <c>webhook_id=N</c>, with no signature and no X-WC-*
    /// headers. The receiver answers it 200 and does nothing else.
    /// </summary>
    public static bool IsPing(string? contentType, ReadOnlySpan<byte> rawBody)
    {
        if (contentType is null
            || !contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || rawBody.Length is 0 or > 64)
            return false;
        return PingBody.IsMatch(Encoding.ASCII.GetString(rawBody).Trim());
    }
}
