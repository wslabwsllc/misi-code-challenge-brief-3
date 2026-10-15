using EventPermits.Application;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace EventPermits.Web.Api;

public static class AccountEndpoints
{
    /// <summary>Sign-in happens on the static Login page; sign-out is a form POST protected by anti-forgery.</summary>
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/account/logout", async ([FromForm] string? returnUrl, SignInManager<AppUser> signIn, ILoggerFactory logs, HttpContext http) =>
        {
            await signIn.SignOutAsync();
            logs.CreateLogger("Account").LogInformation("Signed out {User}", http.User.Identity?.Name);
            return Results.LocalRedirect("/");
        }).RequireAuthorization();
    }

    /// <summary>Only local paths are accepted as return URLs (no open redirects).</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal) && !returnUrl.StartsWith("/\\", StringComparison.Ordinal)
            ? returnUrl
            : "/dashboard";

    public static IResult AntiforgeryToken(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        return Results.Ok(new { token = tokens.RequestToken, headerName = ApiEndpoints.AntiforgeryHeader });
    }
}
