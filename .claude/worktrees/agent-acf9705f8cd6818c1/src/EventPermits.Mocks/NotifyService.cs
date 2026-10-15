using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EventPermits.Mocks;

/// <summary>Fictional notification gateway: accepts messages, then reports delivery through signed webhooks.</summary>
public sealed class NotifyService : IMockService
{
    public const string FailDelivery = "fail_delivery";
    public const string DuplicateCallbacks = "duplicate_callbacks";
    public const string OutOfOrder = "out_of_order";
    public const string BadSignature = "bad_signature";
    public const string StaleTimestamp = "stale_timestamp";

    public string Name => "notify";

    public IReadOnlyList<string> Modes { get; } =
        [.. EventPermits.Mocks.Modes.Common, FailDelivery, DuplicateCallbacks, OutOfOrder, BadSignature, StaleTimestamp];

    public void ConfigureServices(IServiceCollection services, MockOptions options)
    {
        services.AddHttpClient(NotifyDispatcher.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<NotifyStore>();
        services.AddSingleton<IResettable>(sp => sp.GetRequiredService<NotifyStore>());
        services.AddSingleton<NotifyDispatcher>();
    }

    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/messages", Send)
            .AsServiceEndpoint()
            .WithName("SendMessage")
            .WithSummary("Queue an Email or Sms message. Requires X-API-Key and Idempotency-Key. Delivery is reported by webhook.");
        app.MapGet("/messages/{messageId}", Get)
            .AsServiceEndpoint()
            .WithName("GetMessage")
            .WithSummary("Message status, history and webhook attempts. Requires X-API-Key.");

        app.MapAdminGroup().ExcludeFromDescription()
            .MapPost("/notify/redeliver/{messageId}", Redeliver);
    }

    private static IResult Send(SendRequest? body, HttpRequest request, ScenarioState scenario, MockOptions options, NotifyStore store, NotifyDispatcher dispatcher)
    {
        if (!Secrets.Matches(request.Headers["X-API-Key"], options.Notify.ApiKey)) return InvalidApiKey();

        var idempotencyKey = request.Headers["Idempotency-Key"].ToString().Trim();
        if (idempotencyKey.Length is 0 or > 200) return Results.BadRequest(new { error = "missing_idempotency_key" });

        if (body is null || string.IsNullOrWhiteSpace(body.To) || body.Channel is not ("Email" or "Sms"))
        {
            return Results.BadRequest(new { error = "invalid_request", detail = "to is required and channel must be Email or Sms." });
        }

        var (message, created) = store.GetOrCreate(idempotencyKey, body, Correlation.Read(request), scenario.Mode);
        if (created) dispatcher.Start(message);
        return Results.Json(new { messageId = message.Id, status = message.Status }, statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult Get(string messageId, HttpRequest request, MockOptions options, NotifyStore store)
    {
        if (!Secrets.Matches(request.Headers["X-API-Key"], options.Notify.ApiKey)) return InvalidApiKey();
        return store.TryGet(messageId, out var message)
            ? Results.Ok(message.View())
            : Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> Redeliver(string messageId, NotifyStore store, NotifyDispatcher dispatcher, CancellationToken cancellationToken)
    {
        if (!store.TryGet(messageId, out var message)) return Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
        var last = message.LastCallback;
        if (last is null) return Results.Conflict(new { error = "no_callback_yet" });
        var responseCode = await dispatcher.DeliverAsync(message, last, signingMode: EventPermits.Mocks.Modes.Normal, cancellationToken);
        return Results.Ok(new { messageId, last.EventId, last.Status, responseCode });
    }

    private static IResult InvalidApiKey() =>
        Results.Json(new { error = "invalid_api_key" }, statusCode: StatusCodes.Status401Unauthorized);
}

public sealed record SendRequest(string? To, string? Channel, string? Subject, string? Body);

/// <summary>A webhook event: the exact body is kept so redelivery and duplicates are byte-identical.</summary>
public sealed record CallbackEvent(string EventId, string Status, string Body);

public sealed record HistoryEntry(string Status, DateTime At);

public sealed record CallbackAttempt(string EventId, string Status, string Url, DateTime AttemptedAt, int? ResponseCode, string? Error);

public sealed class NotifyMessage(string id, SendRequest request, string? correlationId, string mode)
{
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _history = [new("Queued", Clock.UtcNow())];
    private readonly List<CallbackAttempt> _callbacks = [];

    public string Id { get; } = id;
    public string? CorrelationId { get; } = correlationId;

    /// <summary>The scenario mode captured when the message was accepted; it shapes the callbacks.</summary>
    public string Mode { get; } = mode;

    public string Status
    {
        get { lock (_gate) return _history[^1].Status; }
    }

    public CallbackEvent? LastCallback { get; private set; }

    public void SetStatus(string status)
    {
        lock (_gate) _history.Add(new HistoryEntry(status, Clock.UtcNow()));
    }

    public void RecordAttempt(CallbackEvent callback, CallbackAttempt attempt)
    {
        lock (_gate)
        {
            LastCallback = callback;
            _callbacks.Add(attempt);
        }
    }

    public object View()
    {
        lock (_gate)
        {
            return new
            {
                messageId = Id,
                to = request.To,
                channel = request.Channel,
                subject = request.Subject,
                status = _history[^1].Status,
                history = _history.ToList(),
                callbacks = _callbacks.Select(c => new { c.EventId, c.Status, c.Url, c.AttemptedAt, c.ResponseCode, c.Error }).ToList(),
            };
        }
    }
}

public sealed class NotifyStore : IResettable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NotifyMessage> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NotifyMessage> _byIdempotencyKey = new(StringComparer.Ordinal);
    private CancellationTokenSource _deliveries = new();

    /// <summary>Cancelled by reset so pending callbacks for cleared messages are not sent.</summary>
    public CancellationToken DeliveryToken
    {
        get { lock (_gate) return _deliveries.Token; }
    }

    public (NotifyMessage Message, bool Created) GetOrCreate(string idempotencyKey, SendRequest request, string? correlationId, string mode)
    {
        lock (_gate)
        {
            if (_byIdempotencyKey.TryGetValue(idempotencyKey, out var existing)) return (existing, false);
            var message = new NotifyMessage("MSG-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), request, correlationId, mode);
            _messages[message.Id] = message;
            _byIdempotencyKey[idempotencyKey] = message;
            return (message, true);
        }
    }

    public bool TryGet(string id, out NotifyMessage message)
    {
        lock (_gate) return _messages.TryGetValue(id, out message!);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _deliveries.Cancel();
            _deliveries.Dispose();
            _deliveries = new CancellationTokenSource();
            _messages.Clear();
            _byIdempotencyKey.Clear();
        }
    }
}

/// <summary>Runs the Queued → Sent → callback lifecycle in the background and signs webhook requests.</summary>
public sealed class NotifyDispatcher(IHttpClientFactory httpClientFactory, MockOptions options, NotifyStore store, ILogger<NotifyDispatcher> logger)
{
    public const string HttpClientName = "notify-webhook";
    private static readonly TimeSpan StepDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Start(NotifyMessage message)
    {
        var token = store.DeliveryToken;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(message, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Reset while the message was in flight.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delivery lifecycle failed for {MessageId}", message.Id);
            }
        }, CancellationToken.None);
    }

    private async Task RunAsync(NotifyMessage message, CancellationToken cancellationToken)
    {
        await Task.Delay(StepDelay, cancellationToken);
        message.SetStatus("Sent");
        await Task.Delay(StepDelay, cancellationToken);

        var now = Clock.UtcNow();
        var outcome = message.Mode == NotifyService.FailDelivery ? "Failed" : "Delivered";
        var first = Event(message, outcome, now);
        message.SetStatus(outcome);
        await DeliverAsync(message, first, message.Mode, cancellationToken);

        switch (message.Mode)
        {
            case NotifyService.DuplicateCallbacks:
                await DeliverAsync(message, first, message.Mode, cancellationToken);
                break;
            case NotifyService.OutOfOrder:
                // A second, contradictory event that claims to have happened a minute before the first.
                await DeliverAsync(message, Event(message, "Failed", now.AddMinutes(-1)), message.Mode, cancellationToken);
                break;
        }
    }

    private static CallbackEvent Event(NotifyMessage message, string status, DateTime occurredAt)
    {
        var eventId = "EVT-" + Guid.NewGuid().ToString("D");
        var body = JsonSerializer.Serialize(new { eventId, messageId = message.Id, status, occurredAtUtc = Clock.Iso(occurredAt) }, JsonOptions);
        return new CallbackEvent(eventId, status, body);
    }

    /// <summary>POSTs the callback, retrying on 5xx or transport failure (after 2 s, then 4 s). Returns the last response code.</summary>
    public async Task<int?> DeliverAsync(NotifyMessage message, CallbackEvent callback, string signingMode, CancellationToken cancellationToken)
    {
        var url = options.Notify.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            message.RecordAttempt(callback, new CallbackAttempt(callback.EventId, callback.Status, "", Clock.UtcNow(), null, "webhook_url_not_configured"));
            logger.LogWarning("No webhook URL configured; callback {EventId} for {MessageId} not sent", callback.EventId, message.Id);
            return null;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        int? code = null;
        for (var attempt = 0; ; attempt++)
        {
            string? error = null;
            code = null;
            try
            {
                using var request = BuildRequest(url, message, callback, signingMode);
                using var response = await client.SendAsync(request, cancellationToken);
                code = (int)response.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                error = ex.GetType().Name;
            }

            message.RecordAttempt(callback, new CallbackAttempt(callback.EventId, callback.Status, url, Clock.UtcNow(), code, error));
            logger.LogInformation("Callback {EventId} {Status} for {MessageId} attempt {Attempt} answered {ResponseCode} correlation {CorrelationId}",
                callback.EventId, callback.Status, message.Id, attempt + 1, code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? error, message.CorrelationId ?? "");

            var retryable = code is null or >= 500;
            if (!retryable || attempt >= RetryDelays.Length) return code;
            await Task.Delay(RetryDelays[attempt], cancellationToken);
        }
    }

    private HttpRequestMessage BuildRequest(string url, NotifyMessage message, CallbackEvent callback, string signingMode)
    {
        var timestamp = DateTimeOffset.UtcNow;
        if (signingMode == NotifyService.StaleTimestamp) timestamp = timestamp.AddMinutes(-10);
        var unix = timestamp.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var secret = options.Notify.WebhookSecret ?? "";
        if (signingMode == NotifyService.BadSignature) secret += "-not-the-shared-secret";

        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(unix + "." + callback.Body)));
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(callback.Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Timestamp", unix);
        request.Headers.Add("X-Signature", signature);
        if (message.CorrelationId is not null) request.Headers.Add(Correlation.Header, message.CorrelationId);
        return request;
    }
}
