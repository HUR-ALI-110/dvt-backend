using System.Collections.Specialized;
using System.Data;
using Microsoft.Data.SqlClient;

namespace ICTA_DVT.Services;

/// <summary>
/// Drop-in replacement for System.Web.Security.SqlRoleProvider (not available on
/// .NET 10). Reads roles from the aspnet role tables via the standard
/// aspnet_UsersInRoles_GetRolesForUser stored procedure.
/// </summary>
public sealed class SqlRoleProvider
{
    private readonly IConfiguration _config;
    private string _connStr = "";
    private string _applicationName = "";

    public SqlRoleProvider(IConfiguration config) => _config = config;

    public void Initialize(string name, NameValueCollection config)
    {
        var csName = config["connectionStringName"] ?? "AppServices";
        _connStr = _config.GetConnectionString(csName)
                   ?? _config.GetConnectionString("icta_ideas_app")
                   ?? throw new InvalidOperationException($"Connection string '{csName}' is not configured.");
        _applicationName = config["applicationName"] ?? "";
    }

    public string[] GetRolesForUser(string user)
    {
        if (string.IsNullOrEmpty(user)) return Array.Empty<string>();

        using var conn = new SqlConnection(_connStr);
        conn.Open();
        using var cmd = new SqlCommand("dbo.aspnet_UsersInRoles_GetRolesForUser", conn)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 30
        };
        cmd.Parameters.Add("@ApplicationName", SqlDbType.NVarChar, 256).Value = _applicationName;
        cmd.Parameters.Add("@UserName", SqlDbType.NVarChar, 256).Value = user;

        var roles = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0)) roles.Add(reader.GetString(0));
        return roles.ToArray();
    }
}
