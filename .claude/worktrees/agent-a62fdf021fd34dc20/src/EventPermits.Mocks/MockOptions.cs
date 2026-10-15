namespace EventPermits.Mocks;

/// <summary>The "Mock" configuration section. Env vars use "__", e.g. Mock__AdminKey.</summary>
public sealed class MockOptions
{
    public string Service { get; set; } = "";
    public string Mode { get; set; } = Modes.Normal;
    public int LatencyMs { get; set; }
    public string? AdminKey { get; set; }
    public string? DataDirectory { get; set; }
    public string? SecretsDirectory { get; set; }

    public RegistryOptions Registry { get; set; } = new();
    public CalendarOptions Calendar { get; set; } = new();
    public NotifyOptions Notify { get; set; } = new();
    public PartnerFeedOptions PartnerFeed { get; set; } = new();

    public string ResolveDataDirectory() =>
        string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "data", "reference")
            : DataDirectory;
}

public sealed class RegistryOptions
{
    public string? ApiKey { get; set; }
}

public sealed class CalendarOptions
{
    public string ClientId { get; set; } = "event-permits-app";
    public string? ClientSecret { get; set; }
}

public sealed class NotifyOptions
{
    public string? ApiKey { get; set; }
    public string? WebhookUrl { get; set; }
    public string? WebhookSecret { get; set; }
}

public sealed class PartnerFeedOptions
{
    public string? ServiceUrl { get; set; }
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; }
    public string? Bucket { get; set; }
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
    public bool ScheduleEnabled { get; set; }
    public int NightlyHourUtc { get; set; } = 2;
}
