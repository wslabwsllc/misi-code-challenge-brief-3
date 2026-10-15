namespace EventPermits.Application;

/// <summary>
/// Expected failures raised by application services. The web layer maps each one to ProblemDetails;
/// the Blazor UI shows it inline. Check order (plan §3.5): 401, 404, 403, 400, 409, 422, 503.
/// </summary>
public abstract class AppException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public abstract int StatusCode { get; }
}

/// <summary>Missing record, or a record outside the caller's scope (no existence leak, A-06).</summary>
public sealed class NotFoundException(string message = "The requested record was not found or you cannot see it.")
    : AppException("NOT_FOUND", message)
{
    public override int StatusCode => 404;
}

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.", string code = "FORBIDDEN")
    : AppException(code, message)
{
    public override int StatusCode => 403;
}

public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors, string message = "One or more fields are invalid.")
    : AppException("VALIDATION_FAILED", message)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
    public override int StatusCode => 400;

    public static ValidationException For(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>Stale row version (FR-18). Carries the latest values the caller may see and the current version.</summary>
public sealed class ConflictException(object? current, uint currentVersion, string? message = null)
    : AppException("STALE_VERSION", message ?? "Someone else changed this record after you opened it. Review the current values, then reload or re-apply your changes.")
{
    public object? Current { get; } = current;
    public uint CurrentVersion { get; } = currentVersion;
    public override int StatusCode => 409;
}

/// <summary>Invalid workflow transition or unmet business guard (422).</summary>
public sealed class BusinessRuleException(string code, string message) : AppException(code, message)
{
    public override int StatusCode => 422;
}

/// <summary>A dependency is unavailable; nothing was changed and the caller may retry (503).</summary>
public sealed class DependencyUnavailableException(string code, string message) : AppException(code, message)
{
    public override int StatusCode => 503;
}

public static class ErrorCodes
{
    public const string InvalidTransition = "INVALID_TRANSITION";
    public const string NotOwner = "NOT_OWNER";
    public const string SubmissionIncomplete = "SUBMISSION_INCOMPLETE";
    public const string RequiredDocumentMissing = "REQUIRED_DOCUMENT_MISSING";
    public const string EventNotInFuture = "EVENT_NOT_IN_FUTURE";
    public const string OpenRequestsUnanswered = "OPEN_REQUESTS_UNANSWERED";
    public const string ChecklistIncomplete = "CHECKLIST_INCOMPLETE";
    public const string RegistryEvidenceMissing = "REGISTRY_EVIDENCE_MISSING";
    public const string RegistryOverrideNotAllowed = "REGISTRY_OVERRIDE_NOT_ALLOWED";
    public const string ReservationConflict = "RESERVATION_CONFLICT";
    public const string ReservationIncomplete = "RESERVATION_INCOMPLETE";
    public const string SeparationOfDuties = "SEPARATION_OF_DUTIES";
    public const string CalendarNotReconciled = "CALENDAR_NOT_RECONCILED";
    public const string WeatherNotAcknowledged = "SEVERE_WEATHER_NOT_ACKNOWLEDGED";
    public const string NotAssignedAttendant = "NOT_ASSIGNED_ATTENDANT";
    public const string PermitInvalid = "PERMIT_TOKEN_INVALID";
    public const string CheckOutMissing = "CHECK_OUT_MISSING";
    public const string ReadinessIncomplete = "READINESS_INCOMPLETE";
    public const string BlockingIncidents = "UNRESOLVED_HIGH_INCIDENTS";
    public const string CaseClosed = "CASE_NOT_EDITABLE";
    public const string NumberRangeExhausted = "REFERENCE_RANGE_EXHAUSTED";
    public const string AttachmentRejected = "ATTACHMENT_REJECTED";
    public const string LinkExpired = "LINK_EXPIRED";
    public const string LinkInvalid = "LINK_INVALID";
    public const string DemoPasswordRefused = "DEMO_PASSWORD_REFUSED";
    public const string RegistryUnavailable = "REGISTRY_UNAVAILABLE";
    public const string CalendarUnavailable = "CALENDAR_UNAVAILABLE";
    public const string WeatherUnavailable = "WEATHER_UNAVAILABLE";
    public const string GeocoderUnavailable = "GEOCODER_UNAVAILABLE";
    public const string StorageUnavailable = "STORAGE_UNAVAILABLE";
    public const string NotRetryable = "NOT_RETRYABLE";
    public const string SelfLockout = "SELF_LOCKOUT";
}

/// <summary>Collects field errors and throws one <see cref="ValidationException"/>.</summary>
public sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public Validator Text(string field, string? value, int min, int max, string label, bool required = true)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            if (required) Add(field, $"{label} is required.");
            return this;
        }
        if (trimmed.Length < min || trimmed.Length > max)
            Add(field, min > 1 ? $"{label} must be {min} to {max} characters." : $"{label} must be at most {max} characters.");
        return this;
    }

    public Validator Require(bool condition, string field, string message)
    {
        if (!condition) Add(field, message);
        return this;
    }

    public Validator Email(string field, string? value, string label, bool required = true)
    {
        var v = value?.Trim() ?? "";
        if (v.Length == 0)
        {
            if (required) Add(field, $"{label} is required.");
            return this;
        }
        if (v.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(v, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            Add(field, $"{label} must be a valid email address.");
        return this;
    }

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list)) _errors[field] = list = [];
        list.Add(message);
    }

    public bool HasErrors => _errors.Count > 0;

    public IReadOnlyDictionary<string, string[]> Errors => _errors.ToDictionary(e => e.Key, e => e.Value.ToArray());

    public void ThrowIfInvalid()
    {
        if (HasErrors) throw new ValidationException(Errors);
    }
}
