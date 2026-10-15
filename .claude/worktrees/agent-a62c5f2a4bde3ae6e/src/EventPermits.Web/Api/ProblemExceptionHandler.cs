using EventPermits.Application;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace EventPermits.Web.Api;

/// <summary>Maps application exceptions to consistent ProblemDetails with a code and correlation ID (plan §3.5).</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var problem = ToProblem(exception, context.TraceIdentifier);
        if (problem is null)
        {
            logger.LogError(exception, "Unhandled error for {Path}", context.Request.Path);
            return false;
        }
        if (problem.Status >= 500) logger.LogWarning("{Code} for {Path}: {Title}", problem.Extensions["code"], context.Request.Path, problem.Title);
        context.Response.StatusCode = problem.Status!.Value;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }

    public static ProblemDetails? ToProblem(Exception exception, string correlationId)
    {
        ProblemDetails? problem = exception switch
        {
            ValidationException v => new HttpValidationProblemDetails(v.Errors.ToDictionary(e => e.Key, e => e.Value)) { Title = v.Message },
            AppException a => new ProblemDetails { Title = a.Message },
            AntiforgeryValidationException => new ProblemDetails { Title = "The request is missing a valid anti-forgery token.", Status = 400 },
            BadHttpRequestException b => new ProblemDetails { Title = "The request is malformed.", Detail = SafeDetail(b), Status = b.StatusCode },
            _ => null,
        };
        if (problem is null) return null;

        switch (exception)
        {
            case AppException app:
                problem.Status = app.StatusCode;
                problem.Extensions["code"] = app.Code;
                break;
            case AntiforgeryValidationException:
                problem.Extensions["code"] = "ANTIFORGERY_FAILED";
                break;
            default:
                problem.Extensions["code"] = "MALFORMED_REQUEST";
                break;
        }
        if (exception is ConflictException conflict)
        {
            problem.Extensions["currentVersion"] = conflict.CurrentVersion;
            problem.Extensions["current"] = conflict.Current;
            problem.Extensions["reload"] = "Reload the record, review the current values and re-apply your change.";
        }
        if (exception is DependencyUnavailableException) problem.Extensions["retryable"] = true;
        problem.Extensions["correlationId"] = correlationId;
        return problem;
    }

    private static string? SafeDetail(BadHttpRequestException exception) =>
        exception.InnerException is System.Text.Json.JsonException json ? json.Message : null;
}

/// <summary>Cookie-authenticated state-changing API calls must carry the anti-forgery header (E1-01 AC2).</summary>
public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method))
            await antiforgery.ValidateRequestAsync(http);
        return await next(context);
    }
}
