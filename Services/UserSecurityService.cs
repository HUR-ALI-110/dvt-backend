using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICTA_DVT.Models;

namespace ICTA_DVT.Services;

/// <summary>
/// Port of SAS.Security.UserSecurity. Configures the membership/role providers
/// from app_config.client_initials and exposes the two flows the app needs:
///   • AuthenticateUser(user, pass)   — LoginSubmit  → _mem.ValidateUser
///   • AuthenticateUserToken(user, t) — SSO_Login    → MD5(token_prefix + user)
/// The signed bearer token is just the SPA session carrier.
/// </summary>
public sealed class UserSecurityService
{
    private readonly ConfigRepository _rep;
    private readonly SqlMembershipProvider _mem;
    private readonly SqlRoleProvider _role;
    private readonly byte[] _tokenKey;

    public UserSecurityService(IConfiguration config)
    {
        _rep = new ConfigRepository(config);
        Config c = _rep.GetByCode("client_initials");

        // Configure user membership provider.
        _mem = new SqlMembershipProvider(config);
        NameValueCollection cfg = new NameValueCollection();
        cfg.Add("connectionStringName", "AppServices");
        cfg.Add("applicationName", c.parm_value);
        cfg.Add("minRequiredNonalphanumericCharacters", "0");
        cfg.Add("passwordFormat", "Clear");
        cfg.Add("enablePasswordRetrieval", "true");
        cfg.Add("requiresQuestionAndAnswer", "false");
        _mem.Initialize("AspNetSqlMembershipProvider", cfg);

        // Configure user role provider.
        _role = new SqlRoleProvider(config);
        cfg = new NameValueCollection();
        cfg.Add("connectionStringName", "AppServices");
        cfg.Add("applicationName", c.parm_value);
        _role.Initialize("AspNetSqlRoleProvider", cfg);

        var secret = config["Auth:TokenSecret"];
        _tokenKey = Encoding.UTF8.GetBytes(
            string.IsNullOrWhiteSpace(secret) ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) : secret);
    }

    // ── Authentication ─────────────────────────────────────────────────────────

    public bool AuthenticateUser(string user, string pass)
    {
        return _mem.ValidateUser(user, pass);
    }

    public bool AuthenticateUserToken(string user, string token)
    {
        bool valid = (user.Length > 0 && token.Length > 0);
        if (!valid) return false;

        MembershipUser? u = _mem.GetUser(user, true);
        if (u == null) return false;

        Config c = _rep.GetByCode("token_prefix");
        string hash = GetMD5Hash(c.parm_value + user);
        return hash.ToLower() == token.ToLower();
    }

    public string[] GetRoles(string user) => _role.GetRolesForUser(user);

    public string GetRole(string user)
    {
        var roles = _role.GetRolesForUser(user);
        return roles.Length > 0 ? roles[0] : "user";
    }

    private static string GetMD5Hash(string input)
    {
        byte[] data = MD5.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(data.Length * 2);
        foreach (var b in data) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // ── SPA session token (signed bearer) ──────────────────────────────────────

    public string IssueToken(string username, string role, bool persistent)
    {
        var lifetime = persistent ? TimeSpan.FromDays(7) : TimeSpan.FromHours(8);
        var payload = new TokenPayload(username, role, DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds());
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{body}.{Base64Url(Sign(body))}";
    }

    public AuthUser? ValidateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 2 || !FixedTimeEquals(Base64Url(Sign(parts[0])), parts[1])) return null;

        try
        {
            var payload = JsonSerializer.Deserialize<TokenPayload>(Base64UrlDecode(parts[0]));
            if (payload is null || DateTimeOffset.FromUnixTimeSeconds(payload.Exp) < DateTimeOffset.UtcNow) return null;
            return new AuthUser(payload.U, payload.R);
        }
        catch
        {
            return null;
        }
    }

    public AuthUser? ResolveFromAuthorizationHeader(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader)) return null;
        const string prefix = "Bearer ";
        var token = authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader[prefix.Length..].Trim()
            : authorizationHeader.Trim();
        return ValidateToken(token);
    }

    private byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(_tokenKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private sealed record TokenPayload(string U, string R, long Exp);
}
