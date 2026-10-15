namespace EventPermits.Domain;

/// <summary>Fixed validation limits. Configurable thresholds live in <see cref="RuleKeys"/> (RuleSetting rows).</summary>
public static class Limits
{
    public const long MaxAttachmentBytes = 5L * 1024 * 1024; // A-14: 5 MiB
    public const int MaxFileNameLength = 120;
    public const int ApplicantNameMax = 120;
    public const int TitleMin = 5;
    public const int TitleMax = 120;
    public const int DescriptionMax = 2000;
    public const int ReasonMin = 10;
    public const int ReasonMax = 1000;
    public const int OverrideReasonMin = 20;
    public const int CommentMax = 2000;
    public const int PageSize = 20;
    public const string ApplicationPrefix = "APP";
    public const string PermitPrefix = "PER";
    public const string IncidentPrefix = "INC";
    public const int MaxSequence = 99_999;
}

/// <summary>Keys of business rules editable on page 18 (A-08..A-16 defaults).</summary>
public static class RuleKeys
{
    public const string MinDurationMinutes = "Event.MinDurationMinutes";
    public const string MaxDurationMinutes = "Event.MaxDurationMinutes";
    public const string DefaultSetupBufferMinutes = "Venue.DefaultSetupBufferMinutes";
    public const string DefaultTeardownBufferMinutes = "Venue.DefaultTeardownBufferMinutes";
    public const string CheckInOpensMinutesBefore = "CheckIn.OpensMinutesBefore";
    public const string TokenGraceHoursAfterEnd = "CheckIn.TokenGraceHoursAfterEnd";
    public const string InfoRequestDefaultBusinessDays = "InfoRequest.DefaultBusinessDays";
    public const string DownloadLinkMinutes = "Files.DownloadLinkMinutes";
    public const string MaxUploadMegabytes = "Files.MaxUploadMegabytes";
    public const string RegistryOverrideUnknown = "Registry.AllowOverrideUnknown";
    public const string RegistryOverrideFlagged = "Registry.AllowOverrideFlagged";
    public const string WeatherCacheMinutes = "Weather.CacheMinutes";
    public const string WebhookToleranceMinutes = "Webhook.ToleranceMinutes";
    public const string NotificationMaxAttempts = "Notification.MaxAttempts";
    public const string PartnerImportHourUtc = "PartnerFeed.NightlyHourUtc";
    public const string CalendarSyncMinutes = "Calendar.SyncIntervalMinutes";

    public static readonly IReadOnlyList<(string Key, string Value, string Description, int Min, int Max)> Defaults =
    [
        (MinDurationMinutes, "30", "Shortest allowed event duration in minutes (brief §4)", 15, 120),
        (MaxDurationMinutes, "300", "Longest allowed event duration in minutes (brief §4)", 60, 600),
        (DefaultSetupBufferMinutes, "30", "Setup buffer for new venues, minutes (A-08)", 0, 240),
        (DefaultTeardownBufferMinutes, "30", "Teardown buffer for new venues, minutes (A-08)", 0, 240),
        (CheckInOpensMinutesBefore, "60", "Check-in window opens this many minutes before the start (A-09)", 0, 240),
        (TokenGraceHoursAfterEnd, "2", "Permit token stays valid this many hours after the scheduled end (A-12)", 0, 24),
        (InfoRequestDefaultBusinessDays, "5", "Default response due date for information requests, business days (A-13)", 1, 30),
        (DownloadLinkMinutes, "5", "Lifetime of signed download links in minutes (A-16)", 1, 60),
        (MaxUploadMegabytes, "5", "Largest accepted upload in MiB (FR-15)", 1, 5),
        (RegistryOverrideUnknown, "1", "1 = reviewers may override an Unknown registry result with a reason (A-10)", 0, 1),
        (RegistryOverrideFlagged, "0", "1 = reviewers may override a Flagged registry result with a reason (A-10)", 0, 1),
        (WeatherCacheMinutes, "15", "How long a forecast counts as fresh (brief §7.1)", 1, 120),
        (WebhookToleranceMinutes, "5", "Allowed clock difference for delivery webhooks (brief §7.1)", 1, 15),
        (NotificationMaxAttempts, "5", "Send attempts before a notification is marked Failed", 1, 10),
        (PartnerImportHourUtc, "2", "UTC hour of the nightly partner feed import", 0, 23),
        (CalendarSyncMinutes, "10", "Minutes between scheduled regional calendar syncs", 1, 1440),
    ];
}

/// <summary>APP-YYYY-NNNNN and PER-YYYY-NNNNN formatting (A-07).</summary>
public static class References
{
    public static string Format(string prefix, int year, int sequence) => $"{prefix}-{year:D4}-{sequence:D5}";
}
