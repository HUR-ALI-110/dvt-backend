using System.Collections.Specialized;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace ICTA_DVT.Services;

/// <summary>Minimal membership user (existence + email), like System.Web.Security.MembershipUser.</summary>
public sealed record MembershipUser(string UserName, string? Email);

/// <summary>
/// Drop-in replacement for System.Web.Security.SqlMembershipProvider (not
/// available on .NET 10). Same surface — Initialize + ValidateUser + GetUser —
/// backed by the aspnet_Membership / aspnet_Users / aspnet_Applications tables
/// on the configured connection. SAS uses passwordFormat="Clear", so ValidateUser
/// is a plaintext comparison.
/// </summary>
public sealed class SqlMembershipProvider
{
    private readonly IConfiguration _config;
    private string _connStr = "";
    private string _applicationName = "";

    public SqlMembershipProvider(IConfiguration config) => _config = config;

    public void Initialize(string name, NameValueCollection config)
    {
        var csName = config["connectionStringName"] ?? "AppServices";
        _connStr = _config.GetConnectionString(csName)
                   ?? _config.GetConnectionString("icta_ideas_app")
                   ?? throw new InvalidOperationException($"Connection string '{csName}' is not configured.");
        _applicationName = config["applicationName"] ?? "";
    }

    public bool ValidateUser(string user, string pass)
    {
        if (string.IsNullOrEmpty(user) || pass is null) return false;

        using var conn = Open();
        using var cmd = new SqlCommand(
            @"SELECT TOP 1 m.Password
              FROM dbo.aspnet_Membership m
              JOIN dbo.aspnet_Users u         ON u.UserId = m.UserId
              JOIN dbo.aspnet_Applications a  ON a.ApplicationId = u.ApplicationId
              WHERE u.LoweredUserName = LOWER(@u)
                AND a.LoweredApplicationName = LOWER(@app)
                AND m.IsApproved = 1 AND m.IsLockedOut = 0", conn);
        cmd.Parameters.Add("@u", SqlDbType.NVarChar, 256).Value = user;
        cmd.Parameters.Add("@app", SqlDbType.NVarChar, 256).Value = _applicationName;

        var stored = cmd.ExecuteScalar() as string;
        return stored is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(pass));
    }

    public MembershipUser? GetUser(string user, bool userIsOnline)
    {
        if (string.IsNullOrEmpty(user)) return null;

        using var conn = Open();
        using var cmd = new SqlCommand(
            @"SELECT TOP 1 u.UserName, m.Email
              FROM dbo.aspnet_Users u
              JOIN dbo.aspnet_Applications a   ON a.ApplicationId = u.ApplicationId
              LEFT JOIN dbo.aspnet_Membership m ON m.UserId = u.UserId
              WHERE u.LoweredUserName = LOWER(@u)
                AND a.LoweredApplicationName = LOWER(@app)", conn);
        cmd.Parameters.Add("@u", SqlDbType.NVarChar, 256).Value = user;
        cmd.Parameters.Add("@app", SqlDbType.NVarChar, 256).Value = _applicationName;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new MembershipUser(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private SqlConnection Open()
    {
        var conn = new SqlConnection(_connStr);
        conn.Open();
        return conn;
    }
}
