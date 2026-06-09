using System.Text.Json.Serialization;

namespace ICTA_DVT.Models;

// ── Auth ─────────────────────────────────────────────────────────────────────

public record LoginRequest(
    string Username,
    string Password,
    [property: JsonPropertyName("rememberMe")]
    bool RememberMe = false);

public record AuthUser(string Username, string Role);

public record LoginResponse(
    string Token,
    AuthUser User,
    [property: JsonPropertyName("passwordChangeRequired")]
    bool PasswordChangeRequired = false);

public record MeResponse(AuthUser User);