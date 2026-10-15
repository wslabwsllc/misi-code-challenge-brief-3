using System.Security.Cryptography;
using System.Text;
using EventPermits.Application;
using Microsoft.Extensions.Configuration;

namespace EventPermits.Infrastructure;

/// <summary>
/// HMAC-SHA256 signatures for permit tokens (Security:PermitTokenKey, A-12) and download links
/// (Security:LinkSigningKey, A-16). Keys come from secrets; comparison is constant-time.
/// </summary>
public sealed class HmacSigner : ISigner
{
    public const string PermitPurpose = "permit-token";
    public const string LinkPurpose = "download-link";
    private readonly Dictionary<string, byte[]> _keys;

    public HmacSigner(IConfiguration config)
    {
        _keys = new()
        {
            [PermitPurpose] = Key(config, "Security:PermitTokenKey"),
            [LinkPurpose] = Key(config, "Security:LinkSigningKey"),
        };
    }

    public string Sign(string purpose, string payload) =>
        Base64Url(HMACSHA256.HashData(_keys[purpose], Encoding.UTF8.GetBytes(payload)));

    public bool Verify(string purpose, string payload, string signature)
    {
        var expected = Encoding.ASCII.GetBytes(Sign(purpose, payload));
        var actual = Encoding.ASCII.GetBytes(signature ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Key(IConfiguration config, string name)
    {
        var value = config[name];
        if (string.IsNullOrWhiteSpace(value) || value.Length < 32)
            throw new InvalidOperationException($"{name} must be configured with at least 32 characters (run init-local, or set the secret).");
        return Encoding.UTF8.GetBytes(value);
    }
}
