using Microsoft.Data.SqlClient;

namespace ICTA_DVT.Services;

/// <summary>Row of the app_config table (parm_name / parm_value).</summary>
public sealed class Config
{
    public string parm_name { get; set; } = "";
    public string parm_value { get; set; } = "";
}

/// <summary>
/// Reads app_config, mirroring SAS.Repository.ConfigRepository. Returns an empty
/// Config when the key is missing (matching the legacy swallow behaviour).
/// </summary>
public sealed class ConfigRepository
{
    private readonly string _connStr;

    public ConfigRepository(IConfiguration config)
    {
        _connStr = config.GetConnectionString("icta_ideas_app")
                   ?? throw new InvalidOperationException("Connection string 'icta_ideas_app' is not configured.");
    }

    public Config GetByCode(string code)
    {
        try
        {
            using var conn = new SqlConnection(_connStr);
            conn.Open();
            using var cmd = new SqlCommand(
                "SELECT TOP 1 parm_value FROM app_config WHERE parm_name = @code", conn);
            cmd.Parameters.AddWithValue("@code", code);
            var value = cmd.ExecuteScalar() as string;
            return new Config { parm_name = code, parm_value = value ?? "" };
        }
        catch
        {
            return new Config { parm_name = code, parm_value = "" };
        }
    }
}
