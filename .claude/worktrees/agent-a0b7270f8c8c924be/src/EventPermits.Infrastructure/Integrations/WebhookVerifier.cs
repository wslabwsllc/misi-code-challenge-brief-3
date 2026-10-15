using System.Security.Cryptography;
using System.Text;
using EventPermits.Application.Services;

namespace EventPermits.Infrastructure.Integrations;

/// <summary>HMAC-SHA256 over "timestamp.rawBody" with the gateway secret (A-20); constant-time comparison.</summary>
public sealed class WebhookVerifier(IntegrationOptions options) : IWebhookVerifier
{
    public bool Verify(string timestamp, string rawBody, string signature)
    {
        if (string.IsNullOrEmpty(options.Notify.WebhookSecret)) return false;
        var expected = Encoding.ASCII.GetBytes(NotificationService.Signature(options.Notify.WebhookSecret, timestamp, rawBody));
        var actual = Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
