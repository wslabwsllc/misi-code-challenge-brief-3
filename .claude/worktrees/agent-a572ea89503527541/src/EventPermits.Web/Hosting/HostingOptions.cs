using System.Net;

namespace EventPermits.Web.Hosting;

/// <summary>
/// Transport behaviour behind a TLS-terminating load balancer (deployability contract). Off by default, so local
/// runs keep HTTP on loopback (A-25). Keys: Hosting:BehindTlsProxy, Hosting:TrustedProxyNetworks, Hosting:HstsMaxAgeDays.
/// </summary>
public sealed class HostingOptions
{
    public bool BehindTlsProxy { get; set; }
    /// <summary>CIDR ranges (for example the VPC CIDR) whose X-Forwarded-* headers are trusted. Empty = loopback only.</summary>
    public string[] TrustedProxyNetworks { get; set; } = [];
    public int HstsMaxAgeDays { get; set; } = 365;

    public IEnumerable<IPNetwork> ParseNetworks() => TrustedProxyNetworks
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Select(n => IPNetwork.TryParse(n.Trim(), out var net)
            ? net
            : throw new InvalidOperationException($"Hosting:TrustedProxyNetworks entry '{n}' is not a CIDR range such as 10.0.0.0/16."));
}

public enum HostRole
{
    Web,
    Worker,
    Admin,
}
