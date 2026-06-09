using ICTA_DVT.Models;
using ICTA_DVT.Services;
using Serilog;

namespace ICTA_DVT.Routes;

public static class AuthRoutes
{
    public static void MapAuthRoutes(this WebApplication app)
    {
        // POST /auth/login — validate credentials, return a bearer token.
        app.MapPost("/auth/login", (LoginRequest body, UserSecurityService sec) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
                return Results.BadRequest(new { message = "Username and password are required." });

            if (!sec.AuthenticateUser(body.Username, body.Password))
            {
                Log.Information("Auth.Login: invalid login attempt for user '{User}'.", body.Username);
                return Results.Json(new { message = "Invalid username/password - please try again." }, statusCode: 401);
            }

            var role  = sec.GetRole(body.Username);
            var token = sec.IssueToken(body.Username, role, body.RememberMe);
            var user  = new AuthUser(body.Username, role);

            Log.Information("Auth.Login: user '{User}' signed in.", body.Username);
            return Results.Ok(new LoginResponse(token, user));
        })
        .WithName("Login")
        .WithSummary("Authenticate a user and return a bearer token")
        .WithTags("Auth");

        // GET /auth/sso?user=...&token=... — single sign-on (SSO_Login).
        // Validates an MD5(token_prefix + user) token and returns a session token.
        app.MapGet("/auth/sso", (string? user, string? token, UserSecurityService sec) =>
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { message = "user and token are required." });

            if (!sec.AuthenticateUserToken(user, token))
            {
                Log.Information("Auth.SSO: invalid token for user '{User}'.", user);
                return Results.Json(new { message = "Invalid SSO token." }, statusCode: 401);
            }

            var role        = sec.GetRole(user);
            var sessionToken = sec.IssueToken(user, role, false);

            Log.Information("Auth.SSO: user '{User}' signed in via SSO.", user);
            return Results.Ok(new LoginResponse(sessionToken, new AuthUser(user, role)));
        })
        .WithName("SsoLogin")
        .WithSummary("Single sign-on: validate an SSO token and return a session token")
        .WithTags("Auth");

        // GET /auth/me — resolve the current user from the Authorization header.
        app.MapGet("/auth/me", (HttpRequest req, UserSecurityService sec) =>
        {
            var user = sec.ResolveFromAuthorizationHeader(req.Headers.Authorization);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(new MeResponse(user));
        })
        .WithName("Me")
        .WithSummary("Return the currently authenticated user")
        .WithTags("Auth");

        // POST /auth/logout — stateless tokens, so this is a no-op the client
        // can call for symmetry; the client simply discards its token.
        app.MapPost("/auth/logout", () => Results.Ok(new { ok = true }))
            .WithName("Logout")
            .WithSummary("Log the current user out (client discards token)")
            .WithTags("Auth");
    }
}
