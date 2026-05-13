using System.Data;
using System.Security;
using System.Text;
using Dapper;
using ICTA_DVT.Models;
using Microsoft.Data.SqlClient;
using Serilog;

namespace ICTA_DVT.Services;

public sealed class DvtDbService
{
    private readonly string _connStr;
    private readonly IConfiguration _config;

    public DvtDbService(IConfiguration config)
    {
        _config = config;
        _connStr = config.GetConnectionString("icta_ideas_app")
            ?? throw new InvalidOperationException("Connection string 'icta_ideas_app' is not configured.");
    }

    // ── Connection factory ───────────────────────────────────────────────────

    private SqlConnection OpenConnection()
    {
        var conn = new SqlConnection(_connStr);
        conn.Open();
        return conn;
    }

    // ── XML parms builder ────────────────────────────────────────────────────

    public static string BuildXmlParms(Dictionary<string, string?> parms)
    {
        var sb = new StringBuilder("<parms>");
        foreach (var (key, value) in parms)
        {
            if (value is not null)
                sb.Append($"<{key}>{SecurityElement.Escape(value)}</{key}>");
        }
        sb.Append("</parms>");
        return sb.ToString();
    }

    // ── Generic SP executor (XML @parms) ────────────────────────────────────

    private List<Dictionary<string, object?>> ExecuteXmlSp(string spName, string xmlParms, string debug = "n")
    {
        using var conn = OpenConnection();
        using var cmd = new SqlCommand(spName, conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 60 };
        cmd.Parameters.Add("@parms", SqlDbType.Xml).Value = xmlParms;
        cmd.Parameters.AddWithValue("@debug", debug);

        var results = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            results.Add(row);
        }
        return results;
    }

    private static T? Col<T>(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return default;
        if (v is T t) return t;
        try { return (T)Convert.ChangeType(v, typeof(T)); }
        catch { return default; }
    }

    private static string Str(Dictionary<string, object?> row, string key) => Col<string>(row, key) ?? string.Empty;
    private static double Dbl(Dictionary<string, object?> row, string key) => Col<double>(row, key);
    private static int Int(Dictionary<string, object?> row, string key) => Col<int>(row, key);
    private static DateTime? Date(Dictionary<string, object?> row, string key) => Col<DateTime?>(row, key);

    // ── Geo & date helpers ───────────────────────────────────────────────────

    public static GeoParams ResolveGeoParams(string? dealer, string? scope, string? district)
    {
        // geo_type_id: 1=dealer, 2=district, 4=national/all
        if (!string.IsNullOrEmpty(dealer) && dealer != "all-dealer" && int.TryParse(dealer, out _))
            return new GeoParams(1, dealer);

        if (!string.IsNullOrEmpty(district) && district != "all-district")
            return new GeoParams(2, district);

        return new GeoParams(4, "0");
    }

    // ── Filter options (shell) ───────────────────────────────────────────────

    public DashboardFilterOptions GetFilterOptions()
    {
        using var conn = OpenConnection();

        var dealers = conn.Query<LocationRow>(
            "SELECT location_id LocationId, location_code LocationCode, location_name LocationName, " +
            "district1 District1, district2 District2, '' Region " +
            "FROM dim_location WHERE location_id > 0 AND date_close IS NULL ORDER BY location_name").ToList();

        var dealerOptions = dealers
            .Select(d => new FilterOption(d.LocationId.ToString(), $"{d.LocationName} ({d.LocationCode})"))
            .ToList();

        var districtOptions = dealers
            .Select(d => d.District1)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct()
            .OrderBy(d => d)
            .Select(d => new FilterOption(d!, d!))
            .Prepend(new FilterOption("all-district", "All Districts"))
            .ToList();

        var currentYear = DateTime.Now.Year;
        var dateOptions = Enumerable.Range(0, 5)
            .Select(i => currentYear - i)
            .Select(y => new FilterOption($"{y}-01-01", $"{y} YTD"))
            .ToList();

        var scopeOptions = new List<FilterOption>
        {
            new("all", "All"),
            new("dealer", "Dealer"),
            new("district", "District")
        };

        return new DashboardFilterOptions(dealerOptions, scopeOptions, districtOptions, dateOptions);
    }

    // ── Shell dealer summary ─────────────────────────────────────────────────

    public DealerSummary GetDealerSummary(GeoParams geo, string? date, int reportId)
    {
        // Only meaningful at dealer level
        if (geo.GeoTypeId != 1)
        {
            return new DealerSummary(
                "DVT", "All Dealers", string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty,
                Array.Empty<Certification>());
        }

        var year = ExtractYear(date);
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = reportId.ToString(),
            ["geo_type_id"] = geo.GeoTypeId.ToString(),
            ["geo_value"] = geo.GeoValue,
            ["date_type_id"] = year.ToString(),
            ["user_id"] = "system"
        });

        try
        {
            var rows = ExecuteXmlSp("sp_rpt_dcn_management_overview", xmlParms);
            if (rows.Count == 0)
                return EmptyDealerSummary();

            var first = rows[0];
            var locationName = Str(first, "location_name");
            var dealerCode = Str(first, "DealerCode");
            var region = Str(first, "Region");
            var salesDist = Str(first, "Sales_District");
            var svcDist = Str(first, "Service_District");

            return new DealerSummary(
                Eyebrow: "Dealer Visit Report",
                Title: locationName,
                Subtitle: string.Empty,
                DealerIdLabel: dealerCode,
                Region: region,
                SalesDistrict: salesDist,
                ServicePartsDistrict: svcDist,
                Certifications: Array.Empty<Certification>());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch dealer summary for geo {GeoValue}", geo.GeoValue);
            return EmptyDealerSummary();
        }
    }

    private static DealerSummary EmptyDealerSummary() => new(
        "DVT", string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty,
        Array.Empty<Certification>());

    // ── Overview page data ───────────────────────────────────────────────────

    public OverviewDashboard GetOverviewData(GeoParams geo, string? date, int reportId)
    {
        var year = ExtractYear(date);
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = reportId.ToString(),
            ["geo_type_id"] = geo.GeoTypeId.ToString(),
            ["geo_value"] = geo.GeoValue,
            ["date_type_id"] = year.ToString(),
            ["user_id"] = "system"
        });

        List<Dictionary<string, object?>> rows;
        try { rows = ExecuteXmlSp("sp_rpt_dcn_management_overview", xmlParms); }
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_dcn_management_overview failed");
            rows = new List<Dictionary<string, object?>>();
        }

        // Group personnel rows by broad role category
        var personRows = rows.Select(r => new PersonRow(
            Role: Str(r, "Role"),
            Name: Str(r, "Name"),
            Secondary: Str(r, "DealerCode"))).ToList();

        var peopleSection = new OverviewPeopleSection("Dealer Personnel", personRows);

        return new OverviewDashboard(
            LeftColumnTitle: "Personnel",
            CenterColumnTitle: "Performance",
            RightColumnTitle: "Metrics",
            PeopleSections: new[] { peopleSection },
            PerformanceTables: Array.Empty<OverviewTableCard>(),
            MetricCards: Array.Empty<OverviewMetricCard>());
    }

    // ── Service-Parts page data ──────────────────────────────────────────────

    public ServicePartsDashboard GetServicePartsData(GeoParams geo, string? date, int reportId)
    {
        var year = ExtractYear(date);
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = reportId.ToString(),
            ["geo_type_id"] = geo.GeoTypeId.ToString(),
            ["geo_value"] = geo.GeoValue,
            ["date_type_id"] = year.ToString(),
            ["user_id"] = "system"
        });

        List<Dictionary<string, object?>> rows;
        try { rows = ExecuteXmlSp("sp_rpt_dcn_partsales_overview", xmlParms); }
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_dcn_partsales_overview failed");
            rows = new List<Dictionary<string, object?>>();
        }

        // TODO: map rows to chart series and stat tiles once SP output is confirmed
        var emptyChart = new MixedChartCard(
            "Parts Sales Overview",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<MixedChartSeries>(),
            Array.Empty<ChartLegendItem>());

        return new ServicePartsDashboard(emptyChart, Array.Empty<StatTile>(), Array.Empty<StatTile>(), Array.Empty<MessagePanel>());
    }

    // ── EV Readiness page data ───────────────────────────────────────────────

    public EvReadinessDashboard GetEvReadinessData(GeoParams geo, string? date, int reportId)
    {
        var year = ExtractYear(date);
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = reportId.ToString(),
            ["geo_type_id"] = geo.GeoTypeId.ToString(),
            ["geo_value"] = geo.GeoValue,
            ["date_type_id"] = year.ToString(),
            ["user_id"] = "system"
        });

        List<Dictionary<string, object?>> surveyRows;
        try { surveyRows = ExecuteXmlSp("sp_rpt_dcn_ev_mobile_survey", xmlParms); }
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_dcn_ev_mobile_survey failed");
            surveyRows = new List<Dictionary<string, object?>>();
        }

        // TODO: map surveyRows to sections once SP column names are confirmed
        return new EvReadinessDashboard(
            ProgressLabel: "EV Readiness",
            ProgressValue: "0%",
            ProgressHelp: string.Empty,
            Sections: Array.Empty<EvSection>(),
            FooterAlert: string.Empty);
    }

    // ── Sales page data ──────────────────────────────────────────────────────

    public SalesDashboard GetSalesData(GeoParams geo, string? date, int reportId)
    {
        var year = ExtractYear(date);
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = reportId.ToString(),
            ["geo_type_id"] = geo.GeoTypeId.ToString(),
            ["geo_value"] = geo.GeoValue,
            ["date_type_id"] = year.ToString(),
            ["user_id"] = "system"
        });

        List<Dictionary<string, object?>> rows;
        try { rows = ExecuteXmlSp("sp_rpt_dcn_sales_total", xmlParms); }
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_dcn_sales_total failed");
            rows = new List<Dictionary<string, object?>>();
        }

        // TODO: map rows to SalesRow[] once SP column structure is confirmed
        return new SalesDashboard(Array.Empty<SalesRow>(), Array.Empty<MessagePanel>());
    }

    // ── Meetings ─────────────────────────────────────────────────────────────

    public IReadOnlyList<MeetingRecord> GetMeetings(
        GeoParams geo, string? date1, string? date2, int? deptId, int reportId)
    {
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = reportId.ToString(),
            ["geo_type_id"] = geo.GeoTypeId.ToString(),
            ["geo_value"] = geo.GeoValue,
            ["department_id"] = deptId?.ToString(),
            ["date1"] = date1,
            ["date2"] = date2,
            ["user_id"] = "system"
        });

        var rows = ExecuteXmlSp("sp_rpt_dcn_package", xmlParms);
        return rows.Select(MapToMeetingRecord).ToList();
    }

    private static MeetingRecord MapToMeetingRecord(Dictionary<string, object?> r)
    {
        var pkgId = Int(r, "pkg_id");
        var insertUser = Str(r, "insert_user_id");
        var initials = BuildInitials(insertUser);
        var pkgDate = Date(r, "pkg_date");

        return new MeetingRecord(
            Id: pkgId.ToString(),
            DealerName: Str(r, "location_name"),
            SalesDistrict: Str(r, "regdst1"),
            PartsDistrict: Str(r, "regdst2"),
            Department: Str(r, "dept_name"),
            ContactDate: pkgDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            CreatedBy: new CreatedBy(initials, insertUser, PickTone(insertUser)),
            Download: new DownloadAsset("Download PDF", $"/reports/{Str(r, "contact_pkg")}"),
            MeetingComment: Str(r, "pkg_notes"));
    }

    // ── KPIs ─────────────────────────────────────────────────────────────────

    public IReadOnlyList<KpiRecord> GetKpis(
        GeoParams geo, string? date1, string? date2, int? deptId, int reportId)
    {
        // report_id determines use_kpi via rpt_report.parm2 — ensure the configured
        // PackageKpi report_id has parm2 set to 1 in the rpt_report table.
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = reportId.ToString(),
            ["geo_type_id"] = geo.GeoTypeId.ToString(),
            ["geo_value"] = geo.GeoValue,
            ["department_id"] = deptId?.ToString(),
            ["date1"] = date1,
            ["date2"] = date2,
            ["user_id"] = "system"
        });

        var rows = ExecuteXmlSp("sp_rpt_dcn_package", xmlParms);

        // Only rows that have a kpi_id represent KPI records
        return rows
            .Where(r => r.ContainsKey("kpi_id") && r["kpi_id"] is not null)
            .Select(MapToKpiRecord)
            .ToList();
    }

    private static KpiRecord MapToKpiRecord(Dictionary<string, object?> r)
    {
        var kpi1 = Dbl(r, "kpi1");
        var kpi2 = Dbl(r, "kpi2");
        var kpiUnit = Str(r, "kpi_unit");
        var statusName = Str(r, "status_name");
        var pkgDate = Date(r, "pkg_date");
        var tgtDate = Date(r, "tgt_date");

        return new KpiRecord(
            Id: Str(r, "kpi_id"),
            DealerName: Str(r, "location_name"),
            SalesDistrict: Str(r, "regdst1"),
            PartsDistrict: Str(r, "regdst2"),
            Department: Str(r, "dept_name"),
            ActionItemDate: pkgDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            TargetDate: tgtDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            TimePeriod: DetermineTimePeriod(statusName),
            CentralKpi: Str(r, "kpi_name"),
            YtdKpiAtMeeting: kpi1,
            CurrentYtdKpi: kpi2,
            Achieved: DetermineAchieved(kpi1, kpi2, kpiUnit));
    }

    // ── KPI Detail ───────────────────────────────────────────────────────────

    public KpiDetailRecord? GetKpiDetail(string kpiId, string pkgId)
    {
        using var conn = OpenConnection();

        // Get the package row for this kpi
        var xmlParms = BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = _config.GetValue<int>("ReportIds:PackageKpi").ToString(),
            ["geo_type_id"] = "4",
            ["geo_value"] = "0",
            ["user_id"] = "system"
        });

        var rows = ExecuteXmlSp("sp_rpt_dcn_package", xmlParms);
        var kpiRow = rows.FirstOrDefault(r =>
            Str(r, "kpi_id") == kpiId &&
            Int(r, "pkg_id").ToString() == pkgId);

        if (kpiRow is null) return null;

        // Load comments from dcn.kpi_msg
        var comments = conn.Query<KpiMsgRow>(
            "SELECT id Id, pkg_id PkgId, action_id ActionId, kpi_id KpiId, msg Msg, update_date UpdateDate " +
            "FROM dcn.kpi_msg WHERE kpi_id = @kpiId AND pkg_id = @pkgId ORDER BY update_date",
            new { kpiId = int.Parse(kpiId), pkgId = int.Parse(pkgId) })
            .SelectMany(ParseComment)
            .ToList();

        var base_ = MapToKpiRecord(kpiRow);
        return new KpiDetailRecord(
            base_.Id, base_.DealerName, base_.SalesDistrict, base_.PartsDistrict,
            base_.Department, base_.ActionItemDate, base_.TargetDate, base_.TimePeriod,
            base_.CentralKpi, base_.YtdKpiAtMeeting, base_.CurrentYtdKpi, base_.Achieved,
            comments);
    }

    // ── Save meeting note ────────────────────────────────────────────────────

    public void SaveMeetingNote(int pkgId, string note)
    {
        using var conn = OpenConnection();
        conn.Execute(
            "dcn.sp_save_pkg_msg",
            new { pkg_id = pkgId, action_id = 0, kpi_id = 0, msg = note },
            commandType: CommandType.StoredProcedure);
    }

    // ── Add KPI comment ──────────────────────────────────────────────────────

    public void AddKpiComment(int pkgId, int actionId, int kpiId, string body, string authorName, string authorRole)
    {
        // Encode author info in the message text since sp_save_pkg_msg stores free-form text
        var formatted = $"[{authorName} | {authorRole}] {body}";
        using var conn = OpenConnection();
        conn.Execute(
            "dcn.sp_save_pkg_msg",
            new { pkg_id = pkgId, action_id = actionId, kpi_id = kpiId, msg = formatted },
            commandType: CommandType.StoredProcedure);
    }

    // ── Update message panel ─────────────────────────────────────────────────

    public void UpdateMessage(int pkgId, string body)
    {
        using var conn = OpenConnection();
        conn.Execute(
            "dcn.sp_save_pkg_msg",
            new { pkg_id = pkgId, action_id = 0, kpi_id = 0, msg = body },
            commandType: CommandType.StoredProcedure);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int ExtractYear(string? date)
    {
        if (string.IsNullOrEmpty(date)) return DateTime.Now.Year;
        if (DateTime.TryParse(date, out var dt)) return dt.Year;
        return DateTime.Now.Year;
    }

    private static string BuildInitials(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return "?";
        var parts = userId.Split('@')[0].Split('.');
        return parts.Length >= 2
            ? $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}"
            : userId.Length > 0 ? $"{char.ToUpper(userId[0])}" : "?";
    }

    private static readonly string[] _tones = { "indigo", "teal", "emerald", "amber" };

    private static string PickTone(string userId) =>
        _tones[Math.Abs(userId.GetHashCode()) % _tones.Length];

    private static string DetermineTimePeriod(string statusName) =>
        statusName.Equals("active", StringComparison.OrdinalIgnoreCase) ? "Active" : "Closed";

    private static string DetermineAchieved(double kpi1, double kpi2, string kpiUnit)
    {
        // For percentage-type KPIs, higher is generally better
        // For flagged KPIs (kpi = -1), lower (closer to 0) is better
        if (kpi1 < 0) return kpi2 >= 0 ? "Yes" : "No";
        return kpi2 >= kpi1 ? "Yes" : "No";
    }

    private static IReadOnlyList<KpiComment> ParseComment(KpiMsgRow row)
    {
        // msg format: "[date] text<br>" or "[Author | Role] text<br>"
        var comments = new List<KpiComment>();
        var parts = row.Msg.Split("<br>", StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            string authorName = "ICTA User", authorRole = "District Manager", body = trimmed;
            string createdAt = row.UpdateDate.ToString("yyyy-MM-ddTHH:mm:ss");

            // Try to parse "[Author | Role] text" format
            if (trimmed.StartsWith('['))
            {
                var closeBracket = trimmed.IndexOf(']');
                if (closeBracket > 0)
                {
                    var meta = trimmed[1..closeBracket];
                    body = trimmed[(closeBracket + 1)..].Trim();

                    // Try "[date]" format
                    if (DateTime.TryParse(meta, out var parsedDate))
                        createdAt = parsedDate.ToString("yyyy-MM-ddTHH:mm:ss");
                    // Try "[Author | Role]" format
                    else if (meta.Contains('|'))
                    {
                        var metaParts = meta.Split('|', 2);
                        authorName = metaParts[0].Trim();
                        authorRole = metaParts[1].Trim();
                    }
                }
            }

            comments.Add(new KpiComment(
                Id: $"{row.Id}_{index++}",
                AuthorName: authorName,
                AuthorRole: authorRole,
                Body: body,
                CreatedAt: createdAt));
        }
        return comments;
    }
}
