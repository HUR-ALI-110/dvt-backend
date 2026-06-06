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

    // ── XML parms builders ───────────────────────────────────────────────────

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

    private static string BuildOverviewXmlParms(DashboardFilters filters)
    {
        return BuildXmlParms(new Dictionary<string, string?>
        {
            ["report_id"] = filters.ReportId,
            ["geo_type_id"] = filters.GeoTypeId,
            ["geo_value"] = filters.GeoValue,
            ["geo_title"] = string.IsNullOrEmpty(filters.GeoTitle) ? null : $"Dealer = {filters.GeoTitle}",
            ["date_type_id"] = filters.DateTypeId,
            ["time_title"] = string.IsNullOrEmpty(filters.TimeTitle) ? null : $"Time = {filters.TimeTitle}",
            ["request_id"] = DateTime.Now.ToString("yyyyMMddHHmmss"),
            ["user_id"] = "system"
        });
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

    private List<Dictionary<string, object?>> TryExecute(string spName, string xmlParms)
    {
        try
        {
            return ExecuteXmlSp(spName, xmlParms);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{SpName} failed", spName);
            return new List<Dictionary<string, object?>>();
        }
    }

    // sp_rpt_csi_summary requires @request_id=198 as a separate SQL parameter (not in XML parms)
    private List<Dictionary<string, object?>> ExecuteCsiScoresSp(string xmlParms)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = new SqlCommand("sp_rpt_csi_summary", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 60
            };
            cmd.Parameters.Add("@parms", SqlDbType.Xml).Value = xmlParms;
            cmd.Parameters.AddWithValue("@debug", "n");
            cmd.Parameters.AddWithValue("@request_id", 198);

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
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_csi_summary failed");
            return new List<Dictionary<string, object?>>();
        }
    }

    private static T? Col<T>(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return default;
        if (v is T t) return t;
        try
        {
            return (T)Convert.ChangeType(v, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    private static string Str(Dictionary<string, object?> row, string key) => Col<string>(row, key) ?? string.Empty;
    private static double Dbl(Dictionary<string, object?> row, string key) => Col<double>(row, key);
    private static int Int(Dictionary<string, object?> row, string key) => Col<int>(row, key);
    private static DateTime? Date(Dictionary<string, object?> row, string key) => Col<DateTime?>(row, key);

    // Numeric: whole numbers as int, decimals rounded to 2 dp (no trailing zeros)
    private static string FmtNum(Dictionary<string, object?> row, string key)
    {
        var raw = Str(row, key);
        if (!double.TryParse(raw, out var d)) return raw;
        return Math.Round(d, 2).ToString("0.##");
    }

    // Percentage: SP returns 0–1 fraction → multiply by 100, round to 2 dp, append "%"
    private static string FmtPct(Dictionary<string, object?> row, string key)
    {
        var raw = Str(row, key);
        if (!double.TryParse(raw, out var d)) return raw;
        return Math.Round(d * 100, 2).ToString("0.##") + "%";
    }

    // Currency: rounds to whole dollars, format $#,##0
    private static string DollarFmt(Dictionary<string, object?> row, string key)
    {
        var raw = Str(row, key);
        if (!double.TryParse(raw, out var d)) return raw;
        return ((long)Math.Round(d)).ToString("$#,##0");
    }

    // Appends " POINTS" to a string field value
    private static string WithPoints(Dictionary<string, object?> row, string key)
        => Str(row, key) + " POINTS";

    // Date formatted as M/d/yyyy. Handles both DateTime columns and string columns
    // (many user-defined SP columns return dates as already-formatted strings).
    private static string DateFmt(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var v) || v is null) return "";
        if (v is DateTime dt) return dt.ToString("M/d/yyyy");
        var s = v.ToString() ?? "";
        if (DateTime.TryParse(s, out var parsed)) return parsed.ToString("M/d/yyyy");
        return s;
    }

    // ── Geo helper (kept for tracking routes) ───────────────────────────────

    public static GeoParams ResolveGeoParams(string? dealer, string? scope, string? district)
    {
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

        var scopeOptions = new List<FilterOption> { new("1", "Dealer") };

        var dateOptions = new List<FilterOption>
        {
            new("13", "2024YTD"),
            new("12", "2025YTD"),
            new("11", "2026YTD"),
        };

        return new DashboardFilterOptions(dealerOptions, scopeOptions, Array.Empty<FilterOption>(), dateOptions);
    }

    // ── Shell dealer summary ─────────────────────────────────────────────────

    public DealerSummary GetDealerSummary(DashboardFilters filters)
    {
        if (filters.GeoTypeId != "1" || string.IsNullOrEmpty(filters.GeoValue))
        {
            return new DealerSummary(
                "DVT", "All Dealers", string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty,
                Array.Empty<Certification>());
        }

        var xmlParms = BuildOverviewXmlParms(filters);
        var mgmtRows = TryExecute("sp_rpt_dcn_management_overview", xmlParms);
        var certRows = TryExecute("sp_rpt_dcn_ci_overview", xmlParms);

        if (mgmtRows.Count == 0) return EmptyDealerSummary();

        var first = mgmtRows[0];
        return new DealerSummary(
            Eyebrow: "Dealer Visit Report",
            Title: Str(first, "location_name"),
            Subtitle: string.Empty,
            DealerIdLabel: Str(first, "DealerCode"),
            Region: Str(first, "Region"),
            SalesDistrict: Str(first, "Sales_District"),
            ServicePartsDistrict: Str(first, "Service_District"),
            Certifications: BuildCertifications(certRows));
    }

    private static DealerSummary EmptyDealerSummary() => new(
        "DVT", string.Empty, string.Empty, string.Empty,
        string.Empty, string.Empty, string.Empty,
        Array.Empty<Certification>());

    private static IReadOnlyList<Certification> BuildCertifications(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return Array.Empty<Certification>();
        var r = rows[0];
        return new[]
        {
            new Certification("COE Certified", IsActive(Str(r, "coe_cert")) ? "complete" : "pending"),
            new Certification("Isuzu Connect", IsActive(Str(r, "isuzu_connect")) ? "complete" : "pending"),
            new Certification("Cummins Certified", IsActive(Str(r, "cummins_cert")) ? "complete" : "pending"),
            new Certification("Allison Certified", IsActive(Str(r, "allison_cert")) ? "complete" : "pending"),
        };
    }

    private static bool IsActive(string value) =>
        value is "1" or "yes" or "true" or "Yes" or "True" ||
        (double.TryParse(value, out var d) && d > 0);

    // ── Overview page data ───────────────────────────────────────────────────

    public OverviewDashboard GetOverviewData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new OverviewDashboard("Personnel", "Performance", "Metrics",
                Array.Empty<OverviewPeopleSection>(),
                Array.Empty<OverviewTableCard>(),
                Array.Empty<OverviewMetricCard>(),
                Array.Empty<SalesConsultantRow>(),
                Array.Empty<VerticalDetailRow>(),
                Array.Empty<VerticalDetailRow>(),
                Array.Empty<VerticalDetailRow>(),
                Array.Empty<VerticalDetailRow>(),
                Array.Empty<VerticalDetailRow>());

        var xmlParms = BuildOverviewXmlParms(filters);

        // sp_rpt_dcn_personel drives both people sections (dealer personnel + winner's circle)
        var personnelRows = TryExecute("sp_rpt_dcn_personel", xmlParms);
        // @rpt_lvl=3 returns the Sales Consultant detail table (matches dcn_dashboard_sales_cnslt_overview.rdl)
        var salesConsultantRows = TryExecuteWithRptLvl("sp_rpt_dcn_personel", xmlParms, 3);
        // Manager detail rows: rpt_lvl=2 Sales Manager, 4 Service Manager, 5 Parts Manager
        var salesMgrDetailRows    = TryExecuteWithRptLvl("sp_rpt_dcn_personel", xmlParms, 2);
        var svcMgrDetailRows      = TryExecuteWithRptLvl("sp_rpt_dcn_personel", xmlParms, 4);
        var partsMgrDetailRows    = TryExecuteWithRptLvl("sp_rpt_dcn_personel", xmlParms, 5);
        // ichibanRows (no rpt_lvl) is used for footer links (qualified status)
        var ichibanRows = TryExecute("sp_rpt_dcn_ichiban_overview", xmlParms);
        // ichibanDetailRows with rpt_lvl=2 returns all detail columns (matches dcn_dashboard_ichiban_detail.rdl)
        var ichibanDetailRows = TryExecuteWithRptLvl("sp_rpt_dcn_Ichiban_Overview", xmlParms, 2);
        // COE detail rows from sp_rpt_dcn_COE_Overview with rpt_lvl=2
        var coeDetailRows = TryExecuteWithRptLvl("sp_rpt_dcn_COE_Overview", xmlParms, 2);
        var vehicleRows = TryExecute("sp_rpt_dcn_vehicleinfo_overview", xmlParms);
        var coopRows = TryExecute("sp_rpt_dcn_coop_trucks", xmlParms);
        var objRows = TryExecute("sp_rpt_dcn_truck_sales_objectives", xmlParms);
        var demosRows = TryExecute("sp_rpt_dcn_Demos_overview", xmlParms);
        var partSalesRows = TryExecute("sp_rpt_dcn_partsales_overview", xmlParms);
        var csiRows = TryExecute("sp_rpt_dcn_csi_overview", xmlParms);
        var csiScoreRows = ExecuteCsiScoresSp(xmlParms);
        var partCoopRows = TryExecute("sp_rpt_dcn_parts_coop_overview", xmlParms);
        var irisRows = TryExecute("sp_rpt_dcn_iris_overview", xmlParms);

        return new OverviewDashboard(
            LeftColumnTitle: "Personnel",
            CenterColumnTitle: "Performance",
            RightColumnTitle: "Metrics",
            PeopleSections: new[]
            {
                BuildDealerPersonnelSection(personnelRows),
                BuildWinnersCircleSection(personnelRows, ichibanRows, coeDetailRows),
            },
            PerformanceTables: new[]
            {
                BuildVehicleInfoTable(vehicleRows),
                BuildTruckSalesObjectivesTable(objRows),
                BuildDemosTable(demosRows),
                BuildCoopTrucksTable(coopRows),
            },
            MetricCards: new[]
            {
                BuildPartSalesMetricCard(partSalesRows),
                BuildCsiMetricCard(csiRows, csiScoreRows),
                BuildPartsCoopMetricCard(partCoopRows),
                BuildIrisMetricCard(irisRows),
            },
            SalesConsultants:     BuildSalesConsultantsTable(salesConsultantRows),
            IchibanDetail:        BuildIchibanDetail(ichibanDetailRows),
            CoeDetail:            BuildCoeDetail(coeDetailRows),
            SalesManagerDetail:   BuildSalesManagerDetail(salesMgrDetailRows),
            ServiceManagerDetail: BuildServiceManagerDetail(svcMgrDetailRows),
            PartsManagerDetail:   BuildPartsManagerDetail(partsMgrDetailRows));
    }

    // ── Overview section builders ────────────────────────────────────────────

    private static OverviewPeopleSection BuildDealerPersonnelSection(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewPeopleSection("Dealer Personnel", Array.Empty<PersonRow>());

        // Fixed columns are identical across all rows in sp_rpt_dcn_personel
        var first = rows[0];
        var warrantyAdmin = Str(first, "Warranty Administrator");
        if (string.IsNullOrEmpty(warrantyAdmin)) warrantyAdmin = Str(first, "warranty_administrator");

        var personRows = new[]
            {
                new PersonRow(Role: "Dealer Principal", Name: Str(first, "dealer_principal")),
                new PersonRow(Role: "Executive Manager", Name: Str(first, "executive_manager")),
                new PersonRow(Role: "ICS Admin", Name: Str(first, "ics_admin")),
                new PersonRow(Role: "Warranty Administrator", Name: warrantyAdmin),
            }
            .Where(pr => !string.IsNullOrEmpty(pr.Name))
            .ToList();

        return new OverviewPeopleSection("Dealer Personnel", personRows);
    }

    private static readonly HashSet<string> _winnerCategories =
        new(StringComparer.OrdinalIgnoreCase) { "parts_managers", "svc_managers", "sales_managers" };

    private static string FindPersonnelCategory(Dictionary<string, object?> row)
    {
        foreach (var v in row.Values)
        {
            var s = v?.ToString() ?? string.Empty;
            if (_winnerCategories.Contains(s)) return s;
        }

        return string.Empty;
    }

    private static OverviewPeopleSection BuildWinnersCircleSection(
        List<Dictionary<string, object?>> personnelRows,
        List<Dictionary<string, object?>> ichibanRows,
        List<Dictionary<string, object?>> coeRows)
    {
        var personRows = personnelRows
            .Select(r => (Row: r, Category: FindPersonnelCategory(r)))
            .Where(x => !string.IsNullOrEmpty(x.Category))
            .Select(x =>
            {
                var r = x.Row;
                var category = x.Category;
                var role = category switch
                {
                    "parts_managers" => "Parts Manager",
                    "svc_managers" => "Service Manager",
                    "sales_managers" => "Sales Manager",
                    var other => other
                };

                var drillType = category switch
                {
                    "sales_managers" => "overview/sales-manager",
                    "svc_managers"   => "overview/service-manager",
                    "parts_managers" => "overview/parts-manager",
                    _ => (string?)null
                };

                var level = Str(r, "crnt_level");
                var rank = Str(r, "rank");
                var natRank = Str(r, "national_rank");
                var partsGroup = Str(r, "parts_group");

                var (al, av, el, ev) = category switch
                {
                    "sales_managers" => (
                        "Current Lvl", string.IsNullOrEmpty(level) ? "No Data" : level,
                        "Rank", string.IsNullOrEmpty(rank) ? "N/A" : rank),
                    "svc_managers" => (
                        "Group", string.IsNullOrEmpty(partsGroup) ? "N/A" : partsGroup,
                        "Rank", string.IsNullOrEmpty(rank) ? "N/A" : rank),
                    "parts_managers" => (
                        "National Rank", string.IsNullOrEmpty(natRank) ? "N/A" : natRank,
                        (string?)null, (string?)null),
                    _ => ((string?)null, (string?)null, (string?)null, (string?)null)
                };

                return new PersonRow(
                    Role: role,
                    Name: Str(r, "Person"),
                    AccentLabel: al,
                    AccentValue: av,
                    ExtraAccentLabel: el,
                    ExtraAccentValue: ev,
                    DrillType: drillType);
            })
            .Where(pr => !string.IsNullOrEmpty(pr.Name))
            .ToList();

        // Sales Consultants link row — always append at the end of the winner rows
        var allPersonRows = personRows
            .Append(new PersonRow(
                Role: "Sales Consultants",
                Name: "Click to see list",
                DrillType: "overview/sales-consultants"))
            .ToList();

        var ichibanLinks = ichibanRows
            .Select(r => new FooterLink(Str(r, "location_name"), Str(r, "qualified"), "ichiban"))
            .Where(fl => !string.IsNullOrEmpty(fl.Label))
            .ToList();

        // COE footer link only appears when COE data exists
        var coeLinks = coeRows.Count > 0
            ? new[] { new FooterLink("Circle of Excellence", Str(coeRows[0], "coe_flag"), "coe") }
            : Array.Empty<FooterLink>();

        var allFooterLinks = ichibanLinks.Concat(coeLinks).ToList();

        return new OverviewPeopleSection(
            "Winner's Circle",
            allPersonRows,
            allFooterLinks.Count > 0 ? allFooterLinks : null);
    }

    private static IReadOnlyList<SalesConsultantRow> BuildSalesConsultantsTable(
        List<Dictionary<string, object?>> rows)
    {
        return rows.Select(r => new SalesConsultantRow(
            Person:           Str(r, "Person"),
            CurrentLevel:     Str(r, "Current Level"),
            Pke:              Str(r, "PKE"),
            TrainingWorkshop: Str(r, "Training Workshop"),
            YtdSales:         Str(r, "YTD Sales"),
            YtdRetailSales:   Str(r, "YTD Retail Sales"),
            YtdFleetSales:    Str(r, "YTD Fleet Sales"),
            YtdMegaSales:     Str(r, "YTD Mega Sales"),
            Rank:             Str(r, "Rank"),
            WaVideo:          Str(r, "WA Video")
        )).ToList();
    }

    // ── Detail vertical-table builders ──────────────────────────────────────

    private static IReadOnlyList<VerticalDetailRow> BuildIchibanDetail(
        List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return Array.Empty<VerticalDetailRow>();
        var r = rows[0];
        return new[]
        {
            new VerticalDetailRow("Dealer Code",        Str(r, "location_code")),
            new VerticalDetailRow("Dealer Name",        Str(r, "location_name")),
            new VerticalDetailRow("Dealer Region",      Str(r, "region")),
            new VerticalDetailRow("Dealer District",    Str(r, "district")),
            new VerticalDetailRow("Qualified",          Str(r, "qualified")),
            new VerticalDetailRow("Vehicle Points",     Str(r, "vehicle_points")),
            new VerticalDetailRow("Part Sales",         DollarFmt(r, "part_sales")),
            new VerticalDetailRow("CSI Customer trt.",  Str(r, "csi_cust_trt")),
            new VerticalDetailRow("CSI Customer exp.",  Str(r, "csi_cust_exp")),
            new VerticalDetailRow("CSI sch tim",        Str(r, "csi_sch_tim")),
            new VerticalDetailRow("CSI Doc Chg",        Str(r, "csi_doc_chg")),
        };
    }

    private static IReadOnlyList<VerticalDetailRow> BuildCoeDetail(
        List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return Array.Empty<VerticalDetailRow>();
        var r = rows[0];
        return new[]
        {
            new VerticalDetailRow("Dealer Code",          Str(r, "location_code")),
            new VerticalDetailRow("Dealer Name",          Str(r, "location_name")),
            new VerticalDetailRow("Retail Sales",         Str(r, "retail_sales")),
            new VerticalDetailRow("Fleet Sales",          Str(r, "fleet_sales")),
            new VerticalDetailRow("Net Retail Sales",     Str(r, "non_retail_sales")),
            new VerticalDetailRow("Salesperson Training", Str(r, "salesperson_training_1")),
            new VerticalDetailRow("Salesperson PKE",      Str(r, "salesperson_training_2")),
            new VerticalDetailRow("Service Training",     DollarFmt(r, "service_cert")),
            new VerticalDetailRow("Parts Training",       Str(r, "parts_cert")),
            new VerticalDetailRow("IRIS Percent",         Str(r, "iris_perc")),
            new VerticalDetailRow("CSI cust trt",         Str(r, "csi_cust_trt")),
            new VerticalDetailRow("CSI cust exp",         Str(r, "csi_cust_exp")),
            new VerticalDetailRow("CSI sch tim",          Str(r, "csi_sch_tim")),
            new VerticalDetailRow("CSI doc chg",          Str(r, "csi_doc_chg")),
            new VerticalDetailRow("COE Flag",             Str(r, "coe_flag")),
            new VerticalDetailRow("COE Potential",        Str(r, "coe_potential")),
        };
    }

    private static IReadOnlyList<VerticalDetailRow> BuildSalesManagerDetail(
        List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return Array.Empty<VerticalDetailRow>();
        var r = rows[0];
        return new[]
        {
            new VerticalDetailRow("Person",                     Str(r, "Person")),
            new VerticalDetailRow("Current Level",              Str(r, "Current_Level")),
            new VerticalDetailRow("Rank",                       Str(r, "Rank")),
            new VerticalDetailRow("Dealership Retail Sales",    Str(r, "Dealership_Retail_Sales")),
            new VerticalDetailRow("Qualifies for SM Challenge", Str(r, "Qualifies_for_SM_Challenge")),
            new VerticalDetailRow("SM Start Date",              DateFmt(r, "SM_Start_Date")),
            new VerticalDetailRow("SM Primary Dealership",      Str(r, "SM_Primary_Dealership")),
            new VerticalDetailRow("SM PKEs",                    Str(r, "SM_PKEs")),
            new VerticalDetailRow("SM In Person Class",         Str(r, "SM_In-Person_Class")),
            new VerticalDetailRow("Videos Uploaded",            Str(r, "Videos_Uploaded")),
            new VerticalDetailRow("Trained SC",                 Str(r, "Trained_SC")),
            new VerticalDetailRow("SC with In Person Training", Str(r, "SC_w/In-Person_Training")),
            new VerticalDetailRow("SC with PKEs",               Str(r, "SC_w/PKEs")),
            new VerticalDetailRow("Everconnects First Half",    Str(r, "Everconnects_First_Half")),
            new VerticalDetailRow("Everconnects Second Half",   Str(r, "Everconnects_Second_Half")),
        };
    }

    private static IReadOnlyList<VerticalDetailRow> BuildServiceManagerDetail(
        List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return Array.Empty<VerticalDetailRow>();
        var r = rows[0];
        var yearTag = Int(r, "year_tag");
        var projLabel = yearTag == 1
            ? "Proj YE Parts Purchases > $175,000"
            : "Proj YE Parts Purchases > $150,000";
        return new[]
        {
            new VerticalDetailRow("Person",                              Str(r, "Person")),
            new VerticalDetailRow("Parts Group",                         Str(r, "Parts_Group")),
            new VerticalDetailRow("Objective Yearly",                    DollarFmt(r, "Objective")),
            new VerticalDetailRow("Purchases YTD",                       DollarFmt(r, "Purchases_YTD")),
            new VerticalDetailRow("Purchases Projected",                 DollarFmt(r, "Purchases_Projected")),
            new VerticalDetailRow("Program Eligibility",                 Str(r, "Qualifies")),
            new VerticalDetailRow("YTD Parts Objective Met",             Str(r, "YTD_Parts_Objective_Met")),
            new VerticalDetailRow(projLabel,                             Str(r, "Proj_YE_Parts_Purchases_>_150,000")),
            new VerticalDetailRow("Videos Uploaded",                     Str(r, "WA_Video")),
            new VerticalDetailRow("Parts Objective",                     WithPoints(r, "Parts_Objective")),
            new VerticalDetailRow("Service Training",                    WithPoints(r, "Service_Training")),
            new VerticalDetailRow("Service/Parts EVERConnects 1st Half", WithPoints(r, "Service_Parts_EVERConnects_1st_Half")),
            new VerticalDetailRow("Service/Parts EVERConnects 2nd Half", WithPoints(r, "Service_Parts_EVERConnects_2nd_Half")),
            new VerticalDetailRow("CSI",                                 WithPoints(r, "CSI")),
            new VerticalDetailRow("ATD Points",                          WithPoints(r, "ATD_Bonus")),
            new VerticalDetailRow("Total",                               WithPoints(r, "Total")),
            new VerticalDetailRow("Rank",                                Str(r, "Rank")),
        };
    }

    private static IReadOnlyList<VerticalDetailRow> BuildPartsManagerDetail(
        List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return Array.Empty<VerticalDetailRow>();
        var r = rows[0];
        var yearTag = Int(r, "year_tag");
        var projLabel = yearTag == 1
            ? "Proj YE Parts Purchases > $175,000"
            : "Proj YE Parts Purchases > $150,000";
        return new[]
        {
            new VerticalDetailRow("Person",                              Str(r, "person")),
            new VerticalDetailRow("Parts Group",                         Str(r, "Parts_Group")),
            new VerticalDetailRow("Objective Yearly",                    DollarFmt(r, "Objective")),
            new VerticalDetailRow("Purchases YTD",                       DollarFmt(r, "Purchases_YTD")),
            new VerticalDetailRow("Purchases Projected",                 DollarFmt(r, "Purchases_Projected")),
            new VerticalDetailRow("Program Eligibility",                 Str(r, "Qualifies")),
            new VerticalDetailRow("YTD Parts Objective Met",             Str(r, "YTD_Parts_Objective_Met")),
            new VerticalDetailRow(projLabel,                             Str(r, "Proj_YE_Parts_Purchases_>_150,000")),
            new VerticalDetailRow("Video Uploaded",                      Str(r, "WA_Video")),
            new VerticalDetailRow("Parts Objective",                     WithPoints(r, "Parts_Objective")),
            new VerticalDetailRow("Parts Training",                      WithPoints(r, "Parts_Training")),
            new VerticalDetailRow("Service/Parts EVERConnects 1st Half", WithPoints(r, "Service_Parts_EVERConnects_1st_Half")),
            new VerticalDetailRow("Service/Parts EVERConnects 2nd Half", WithPoints(r, "Service_Parts_EVERConnects_2nd_Half")),
            new VerticalDetailRow("CSI",                                 WithPoints(r, "CSI")),
            new VerticalDetailRow("ATD Bonus",                           WithPoints(r, "ATD_Bonus")),
            new VerticalDetailRow("Total Points",                        WithPoints(r, "Total")),
            new VerticalDetailRow("Group Rank",                          Str(r, "Rank")),
            new VerticalDetailRow("National Rank",                       Str(r, "National_Rank")),
        };
    }

    private static OverviewTableCard BuildVehicleInfoTable(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewTableCard("Truck Sales", new[] { "Series" }, Array.Empty<OverviewTableRow>());

        // SP may return "type sale" (with space) or "type_sale" (underscore)
        var typeSaleKey = rows[0].ContainsKey("type_sale") ? "type_sale" : "type sale";

        var typeSales = rows
            .Select(r => Str(r, typeSaleKey))
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        var seriesList = rows
            .Select(r => Str(r, "series"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        var columns = new[] { "Series" }.Concat(typeSales).ToList();

        var tableRows = seriesList.Select(series =>
        {
            var values = typeSales.Select(type =>
            {
                var match = rows.FirstOrDefault(r => Str(r, "series") == series && Str(r, typeSaleKey) == type);
                return match != null ? Str(match, "units") : "0";
            }).ToList();
            return new OverviewTableRow(series, values);
        }).ToList();

        // Total row — sum each type column across all series
        var totals = typeSales.Select(type =>
            rows.Where(r => Str(r, typeSaleKey) == type)
                .Sum(r =>
                {
                    double.TryParse(Str(r, "units"), out var v);
                    return v;
                })
                .ToString("0")
        ).ToList();
        tableRows.Add(new OverviewTableRow("Total", totals));

        return new OverviewTableCard("Truck Sales", columns, tableRows);
    }

    private static OverviewTableCard BuildCoopTrucksTable(List<Dictionary<string, object?>> rows)
    {
        var columns = new[] { "Period", "Earned", "Utilized", "Remaining", "Ordered" };
        var tableRows = rows.Select(r =>
        {
            var period = Str(r, "Period");
            if (string.IsNullOrEmpty(period)) period = Str(r, "year");
            return new OverviewTableRow(period, new[]
            {
                Str(r, "total_reward"), Str(r, "reward_used"),
                Str(r, "reward_remaining"), Str(r, "ordered")
            });
        }).ToList();
        return new OverviewTableCard("Sales Co-Op", columns, tableRows);
    }

    private static OverviewTableCard BuildTruckSalesObjectivesTable(List<Dictionary<string, object?>> rows)
    {
        var columns = new[] { "Series", "Sales", "Objective", "% Achieved" };
        var tableRows = rows.Select(r => new OverviewTableRow(
            Str(r, "series"),
            new[] { FmtNum(r, "sales"), FmtNum(r, "objective"), FmtPct(r, "pct_achieved") }
        )).ToList();
        return new OverviewTableCard("Truck Sales Objectives", columns, tableRows);
    }

    private static OverviewTableCard BuildDemosTable(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewTableCard("Truck Demos", new[] { "Truck Series", "Earned", "Paid", "Remaining" },
                Array.Empty<OverviewTableRow>());

        var r = rows[0];
        var tableRows = new[]
        {
            new OverviewTableRow("N-Series", new[]
            {
                Str(r, "n_demo_earned"), Str(r, "n_demo_paid"), Str(r, "n_demo_rem")
            }),
            new OverviewTableRow("F-Series", new[]
            {
                Str(r, "ftr_demo_earned"), Str(r, "ftr_demo_paid"), Str(r, "ftr_demo_rem")
            }),
            new OverviewTableRow("Total", new[]
            {
                Str(r, "total_earned"), Str(r, "total_paid"), Str(r, "total_remaining")
            }),
        };

        return new OverviewTableCard("Truck Demos", new[] { "Truck Series", "Earned", "Paid", "Remaining" }, tableRows);
    }

    private static OverviewMetricCard BuildPartSalesMetricCard(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewMetricCard("Dealer Parts Purchasing", Array.Empty<OverviewMetricRow>());

        var r = rows[0];

        var captiveW    = Dbl(r, "Captive");
        var competitiveW = Dbl(r, "Competitive");
        var fvW         = Dbl(r, "FV");
        var totalW      = Dbl(r, "Total");
        var objectiveW  = Dbl(r, "Objective");
        var captiveR    = Dbl(r, "Captive retail");
        var competitiveR = Dbl(r, "Competitive retail");
        var fvR         = Dbl(r, "fv retail");
        var totalR      = Dbl(r, "retail_total");

        static string Dollar(double d) => ((long)Math.Round(d)).ToString("$#,##0");

        static string CalcPct(double num, double den) =>
            den == 0 ? string.Empty : Math.Round(num / den * 100, 1).ToString("0.#") + "%";

        OverviewMetricRow CategoryRow(string label, double wholesale, double retail) =>
            new(label, new[]
            {
                new OverviewMetricValue("Wholesale $",    Dollar(wholesale)),
                new OverviewMetricValue("% of Wholesale", CalcPct(wholesale, totalW)),
                new OverviewMetricValue("Retail / MSRP",  Dollar(retail)),
                new OverviewMetricValue("% of Retail",    CalcPct(retail, totalR)),
            });

        return new OverviewMetricCard("Dealer Parts Purchasing", new[]
        {
            CategoryRow("Captive",     captiveW,     captiveR),
            CategoryRow("Competitive", competitiveW, competitiveR),
            CategoryRow("Fleet Value", fvW,          fvR),
            CategoryRow("Total",       totalW,       totalR),
            new OverviewMetricRow("Objective", new[]
            {
                new OverviewMetricValue("Wholesale $", Dollar(objectiveW)),
                new OverviewMetricValue("% Achieved",  CalcPct(totalW, objectiveW)),
            }),
        });
    }

    private static OverviewMetricCard BuildCsiMetricCard(
        List<Dictionary<string, object?>> csiRows,
        List<Dictionary<string, object?>> csiScoreRows)
    {
        static string P(string v) => string.IsNullOrEmpty(v) ? v : v + "%";

        // Use sp_rpt_csi_summary data when available — matches client RDL exactly.
        // Dealer = g_avg, National = n_avg, looked up by question_text.
        if (csiScoreRows.Count > 0)
        {
            var totalSurveys = Str(csiScoreRows[0], "tot_num");

            const string ct  = "Customer Treatment";
            const string cre = "Customer Repair Expectations";
            const string st  = "Schedule & Timing";
            const string doc = "Documentation on Repairs & Charges";

            string G(string q) => CsiLookup(csiScoreRows, q, "g_avg");
            string N(string q) => CsiLookup(csiScoreRows, q, "n_avg");

            return new OverviewMetricCard("CSI", new[]
            {
                SingleMetricRow("Total Surveys", totalSurveys),
                new OverviewMetricRow("Dealer", new[]
                {
                    new OverviewMetricValue("Customer Treatment", P(G(ct))),
                    new OverviewMetricValue("Repair Expectation", P(G(cre))),
                    new OverviewMetricValue("Schedule & Timing",  P(G(st))),
                    new OverviewMetricValue("Documentation",      P(G(doc))),
                    new OverviewMetricValue("Total",              P(CsiAvg(G(ct), G(cre), G(st), G(doc)))),
                }),
                new OverviewMetricRow("National Average", new[]
                {
                    new OverviewMetricValue("Customer Treatment", P(N(ct))),
                    new OverviewMetricValue("Repair Expectation", P(N(cre))),
                    new OverviewMetricValue("Schedule & Timing",  P(N(st))),
                    new OverviewMetricValue("Documentation",      P(N(doc))),
                    new OverviewMetricValue("Total",              P(CsiAvg(N(ct), N(cre), N(st), N(doc)))),
                }),
            });
        }

        // Fallback: sp_rpt_dcn_csi_overview
        if (csiRows.Count == 0)
            return new OverviewMetricCard("CSI", Array.Empty<OverviewMetricRow>());
        var r = csiRows[0];
        return new OverviewMetricCard("CSI", new[]
        {
            SingleMetricRow("Total Surveys", Str(r, "total_survey")),
            new OverviewMetricRow("Dealer", new[]
            {
                new OverviewMetricValue("Customer Treatment", P(Str(r, "csi_cst_trt"))),
                new OverviewMetricValue("Repair Expectation", P(Str(r, "csi_rpr_exp"))),
                new OverviewMetricValue("Schedule & Timing",  P(Str(r, "csi_sch_tim"))),
                new OverviewMetricValue("Documentation",      P(Str(r, "csi_doc_chg"))),
                new OverviewMetricValue("Total",              P(Str(r, "csi_overall"))),
            }),
            new OverviewMetricRow("National Average", new[]
            {
                new OverviewMetricValue("Customer Treatment", P(Str(r, "csi_cst_trt_nat"))),
                new OverviewMetricValue("Repair Expectation", P(Str(r, "csi_rpr_exp_nat"))),
                new OverviewMetricValue("Schedule & Timing",  P(Str(r, "csi_sch_tim_nat"))),
                new OverviewMetricValue("Documentation",      P(Str(r, "csi_doc_chg_nat"))),
                new OverviewMetricValue("Total",              P(Str(r, "csi_tot_nat"))),
            }),
        });
    }

    private static string CsiLookup(List<Dictionary<string, object?>> rows, string questionText, string field)
    {
        var row = rows.FirstOrDefault(r =>
            string.Equals(Str(r, "question_text"), questionText, StringComparison.OrdinalIgnoreCase));
        if (row == null || !row.ContainsKey(field)) return string.Empty;
        var val = Col<double>(row, field);
        return val == 0 ? string.Empty : Math.Round(val, 1).ToString();
    }

    private static string CsiAvg(params string[] vals)
    {
        var doubles = vals
            .Select(v =>
            {
                double.TryParse(v, out var d);
                return (ok: d > 0, d);
            })
            .Where(x => x.ok)
            .Select(x => x.d)
            .ToArray();
        return doubles.Length == 0 ? string.Empty : Math.Round(doubles.Average(), 1).ToString();
    }

    private static OverviewMetricCard BuildPartsCoopMetricCard(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewMetricCard("Service & Parts Co-Op", Array.Empty<OverviewMetricRow>());

        static string Dollar(double d) => ((long)Math.Round(d)).ToString("$#,##0");

        var metricRows = rows.Select(r =>
        {
            var period = Str(r, "Period");
            if (string.IsNullOrEmpty(period)) period = Str(r, "year");
            if (string.IsNullOrEmpty(period)) period = "—";

            return new OverviewMetricRow(period, new[]
            {
                new OverviewMetricValue("Earned",    Dollar(Dbl(r, "total_reward"))),
                new OverviewMetricValue("Utilized",  Dollar(Dbl(r, "reward_used"))),
                new OverviewMetricValue("Remaining", Dollar(Dbl(r, "reward_remaining"))),
            });
        }).ToArray();

        return new OverviewMetricCard("Service & Parts Co-Op", metricRows);
    }

    private static OverviewMetricCard BuildIrisMetricCard(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewMetricCard("IRIS Performance", Array.Empty<OverviewMetricRow>());
        var r = rows[0];

        static string P(string v)
        {
            if (!double.TryParse(v, out var d)) return v;
            return Math.Round(d, 1).ToString("0.#") + "%";
        }

        return new OverviewMetricCard("IRIS Performance", new[]
        {
            new OverviewMetricRow("", new[]
            {
                new OverviewMetricValue("Dealer YTD Utilization",   P(Str(r, "ytd_net_utilization"))),
                new OverviewMetricValue("1st Half Net Utilization", P(Str(r, "1st_half_net_utilization"))),
                new OverviewMetricValue("2nd Half Net Utilization", P(Str(r, "2nd_half_net_utilization"))),
                new OverviewMetricValue("COM Min.",                 P(Str(r, "National_Avg"))),
            }),
        });
    }

    private static OverviewMetricRow SingleMetricRow(string label, string value) =>
        new(label, new[] { new OverviewMetricValue("Value", value) });

    private static OverviewMetricRow DualMetricRow(string label, string dealerVal, string natVal) =>
        new(label, new[]
        {
            new OverviewMetricValue("Dealer", dealerVal),
            new OverviewMetricValue("National Average", natVal),
        });

    // ── Service-Parts page data ──────────────────────────────────────────────

    public ServicePartsDashboard GetServicePartsData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new ServicePartsDashboard(
                new MixedChartCard("Parts Sales Overview", Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<MixedChartSeries>(), Array.Empty<ChartLegendItem>()),
                Array.Empty<StatTile>(), Array.Empty<StatTile>(), Array.Empty<MessagePanel>(),
                EmptyServicePartsDrillThrough());

        var xmlParms      = BuildOverviewXmlParms(filters);
        var pvoRows       = TryExecute("sp_rpt_dcn_parts_pvo",          xmlParms);
        var fvTotalRows   = TryExecute("sp_rpt_dcn_fv_vs_total_parts",  xmlParms);
        var irisRows      = TryExecute("sp_rpt_dcn_iris_util_parts",    xmlParms);
        var trainingRows  = TryExecute("sp_rpt_dcn_service_training",   xmlParms);
        var coopRows      = TryExecute("sp_rpt_dcn_service_co_op",      xmlParms);
        var csiRows       = TryExecute("sp_rpt_dcn_csi_overview",       xmlParms);
        var csiScoreRows  = ExecuteCsiScoresSp(xmlParms);
        var incentiveRows = TryExecuteWithRptLvl("sp_rpt_dcn_parts_incentives", xmlParms, 1);
        var dpppRows      = TryExecuteWithRptLvl("sp_rpt_dcn_parts_dppp",       xmlParms, 1);
        var backorderRows = TryExecute("sp_rpt_dcn_backorder_service",  xmlParms);
        var execMsgRows   = TryExecute("sp_rpt_dcn_exec_message",       xmlParms);
        var locMsgRows    = TryExecute("sp_rpt_dcn_loc_message",        xmlParms);
        var regnMsgRows   = TryExecute("sp_rpt_dcn_regn_message",       xmlParms);

        // Drill-through detail rows (deeper report levels)
        var dpppDetailRows      = TryExecuteWithRptLvl("sp_rpt_dcn_parts_dppp",            xmlParms, 2);
        var incentiveDetailRows = TryExecuteWithRptLvl("sp_rpt_dcn_parts_incentives",     xmlParms, 2);
        var backorderDetailRows = TryExecuteWithRptLvl("sp_rpt_dcn_backorder_service",    xmlParms, 2);
        var trainingSummaryRows = TryExecuteWithRptLvl("sp_rpt_dcn_service_training",     xmlParms, 2);
        var trainingListRows    = TryExecuteWithRptLvl("sp_rpt_dcn_service_training_ind", xmlParms, 2);

        // Individual service training detail per person (sp_rpt_dcn_service_training_ind needs @userid).
        var trainingIndRows = new List<Dictionary<string, object?>>();
        var seenTrainees = new HashSet<string>();
        foreach (var row in trainingListRows)
        {
            var uid = Str(row, "user_id");
            var gid = Str(row, "personnel_group_id");
            if (string.IsNullOrEmpty(uid) || !seenTrainees.Add(uid)) continue;
            foreach (var ind in TryExecuteServiceTrainingIndForUser(xmlParms, uid, gid))
            {
                ind["user_id"] = uid;
                trainingIndRows.Add(ind);
            }
        }

        var drillThrough = new ServicePartsDrillThrough(
            PartsPurchasing:     BuildPartsPurchasingDrillTables(pvoRows),
            CsiOverall:          BuildCsiDrillTable(csiRows, csiScoreRows),
            IrisNetUtilization:  BuildIrisDrillTable(irisRows),
            Training:            BuildServiceTrainingDrill(trainingSummaryRows, trainingListRows, trainingIndRows),
            CoOpUtilization:     BuildServiceCoopDrillTable(coopRows),
            Dppp:                BuildDpppDrillTable(dpppDetailRows),
            FvPartsSales:        BuildFvPartsSalesDrillTable(pvoRows),
            NationalSubmissions: BuildNationalSubmissionsDrillTable(incentiveDetailRows),
            BackOrderTotal:      BuildBackOrderDrillTable(backorderDetailRows.Count > 0 ? backorderDetailRows : backorderRows));

        return new ServicePartsDashboard(
            Chart:          BuildPartsChart(pvoRows),
            PrimaryStats:   BuildServicePrimaryStats(csiRows, irisRows, trainingRows, coopRows),
            SecondaryStats: BuildServiceSecondaryStats(dpppRows, fvTotalRows, incentiveRows, backorderRows),
            Messages:       BuildServiceMessages(execMsgRows, locMsgRows, regnMsgRows),
            DrillThrough:   drillThrough);
    }

    // sp_rpt_dcn_parts_incentives and sp_rpt_dcn_parts_dppp require @rpt_lvl extra parameter
    private List<Dictionary<string, object?>> TryExecuteWithRptLvl(string spName, string xmlParms, int rptLvl)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = new SqlCommand(spName, conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 60
            };
            cmd.Parameters.Add("@parms", SqlDbType.Xml).Value = xmlParms;
            cmd.Parameters.AddWithValue("@debug", "n");
            cmd.Parameters.AddWithValue("@rpt_lvl", rptLvl);

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
        catch (Exception ex)
        {
            Log.Warning(ex, "{SpName} failed", spName);
            return new List<Dictionary<string, object?>>();
        }
    }

    // sp_rpt_dcn_service_training_ind individual detail for one person (@userid + @personnel_group_id).
    private List<Dictionary<string, object?>> TryExecuteServiceTrainingIndForUser(string xmlParms, string userId, string groupId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = new SqlCommand("sp_rpt_dcn_service_training_ind", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 60
            };
            cmd.Parameters.Add("@parms", SqlDbType.Xml).Value = xmlParms;
            cmd.Parameters.AddWithValue("@debug", "n");
            cmd.Parameters.Add("@userid", SqlDbType.VarChar, 50).Value = string.IsNullOrEmpty(userId) ? DBNull.Value : userId;
            cmd.Parameters.Add("@personnel_group_id", SqlDbType.Int).Value =
                int.TryParse(groupId, out var g) ? g : (object)DBNull.Value;

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
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_dcn_service_training_ind (user) failed");
            return new List<Dictionary<string, object?>>();
        }
    }

    private static MixedChartCard BuildPartsChart(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new MixedChartCard("Parts Sales Overview", Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<MixedChartSeries>(), Array.Empty<ChartLegendItem>());

        var ordered = rows.OrderBy(r => Int(r, "date_id")).ToList();
        var labels  = ordered.Select(r => Str(r, "year_month")).ToArray();

        // SP column "total parts" has a space — dictionary is OrdinalIgnoreCase so both "total parts" and "total_parts" are tried
        IReadOnlyList<double> Vals(string key) => ordered.Select(r => Dbl(r, key)).ToArray();

        return new MixedChartCard(
            Title:       "Parts Sales Overview",
            Labels:      labels,
            YAxisLabels: Array.Empty<string>(),
            Series: new[]
            {
                new MixedChartSeries("Wholesale", "#4b83ff", "bar",  Vals("total parts")),
                new MixedChartSeries("Objective", "#ef2c30", "line", Vals("Objective")),
                new MixedChartSeries("Retail",    "#f8a108", "line", Vals("total_retail")),
            },
            Legend: new[]
            {
                new ChartLegendItem("Wholesale", "#4b83ff", "bar"),
                new ChartLegendItem("Objective", "#ef2c30", "line"),
                new ChartLegendItem("Retail",    "#f8a108", "line"),
            });
    }

    private static IReadOnlyList<StatTile> BuildServicePrimaryStats(
        List<Dictionary<string, object?>> csiRows,
        List<Dictionary<string, object?>> irisRows,
        List<Dictionary<string, object?>> trainingRows,
        List<Dictionary<string, object?>> coopRows)
    {
        // CSI Overall: round(csi_overall, 0)%
        var csiVal = csiRows.Count > 0
            ? ((int)Math.Round(Dbl(csiRows[0], "csi_overall"))) + "%"
            : "N/A";

        // IRIS Utilization: round(ytd_net_utilization, 0)%
        var irisVal = irisRows.Count > 0
            ? ((int)Math.Round(Dbl(irisRows[0], "ytd_net_utilization"))) + "%"
            : "N/A";

        // Training (% Certified): round(pcn_dlr_cert, 0)%
        var trainingVal = trainingRows.Count > 0
            ? ((int)Math.Round(Dbl(trainingRows[0], "pcn_dlr_cert"))) + "%"
            : "N/A";

        // Service & Parts Co-Op: IIf(current="NO", 0, Int(reward_utilized)*100)%
        var coopVal = "N/A";
        if (coopRows.Count > 0)
        {
            var r       = coopRows[0];
            var current = Str(r, "current").Trim();
            coopVal = string.Equals(current, "NO", StringComparison.OrdinalIgnoreCase)
                ? "0%"
                : ((int)Dbl(r, "reward_utilized") * 100) + "%";
        }

        return new[]
        {
            new StatTile("CSI Overall",                       csiVal,      "green"),
            new StatTile("IRIS Utilization",                  irisVal,     ""),
            new StatTile("Training (% Certified)",            trainingVal, ""),
            new StatTile("Service & Parts Co-Op Utilization", coopVal,     ""),
        };
    }

    private static IReadOnlyList<StatTile> BuildServiceSecondaryStats(
        List<Dictionary<string, object?>> dpppRows,
        List<Dictionary<string, object?>> fvTotalRows,
        List<Dictionary<string, object?>> incentiveRows,
        List<Dictionary<string, object?>> backorderRows)
    {
        static string Dollar(double d) => ((long)Math.Round(d)).ToString("$#,##0");

        // DPPP Accrual: currency no cents (NULL → "$0")
        var dpppVal = "$0";
        if (dpppRows.Count > 0)
        {
            var raw = Str(dpppRows[0], "dppp_accrual");
            if (double.TryParse(raw, out var d)) dpppVal = Dollar(d);
        }

        // FV Parts vs Total: round(sum(fleetvalue)/sum(total_parts)*100, 1)%
        var fvVal = "N/A";
        if (fvTotalRows.Count > 0)
        {
            var sumFv    = fvTotalRows.Sum(r => Dbl(r, "fleetvalue"));
            var sumParts = fvTotalRows.Sum(r => Dbl(r, "total_parts"));
            fvVal = sumParts > 0
                ? Math.Round(sumFv / sumParts * 100, 1).ToString("0.#") + "%"
                : "N/A";
        }

        // National Programs Submissions: total_incentives raw count
        var incentiveVal = incentiveRows.Count > 0
            ? Str(incentiveRows[0], "total_incentives")
            : "0";

        // Back Orders Total: currency no cents (NULL → "$0")
        var backorderVal = "$0";
        if (backorderRows.Count > 0)
        {
            var raw = Str(backorderRows[0], "total_back_orders");
            if (!string.IsNullOrEmpty(raw) && double.TryParse(raw, out var bo))
                backorderVal = Dollar(bo);
        }

        var hasBackorders = backorderVal != "$0";
        return new[]
        {
            new StatTile("DPPP Accrual",                  dpppVal,       ""),
            new StatTile("FV Parts Sales vs Total",       fvVal,         ""),
            new StatTile("National Programs Submissions", incentiveVal,  ""),
            new StatTile("Back Orders Total",             backorderVal,  hasBackorders ? "red" : ""),
        };
    }

    // Service RDL displays msg2 (not msg1) for all three message panels
    private static IReadOnlyList<MessagePanel> BuildServiceMessages(
        List<Dictionary<string, object?>> execMsgRows,
        List<Dictionary<string, object?>> locMsgRows,
        List<Dictionary<string, object?>> regnMsgRows)
    {
        var messages = new List<MessagePanel>();

        if (execMsgRows.Count > 0)
        {
            var msg = Str(execMsgRows[0], "msg2");
            if (string.IsNullOrEmpty(msg)) msg = Str(execMsgRows[0], "msg1");
            if (!string.IsNullOrEmpty(msg))
                messages.Add(new MessagePanel("exec-message", "red", "Executive Message", msg));
        }

        if (regnMsgRows.Count > 0)
        {
            var msg = Str(regnMsgRows[0], "msg2");
            if (string.IsNullOrEmpty(msg)) msg = Str(regnMsgRows[0], "msg1");
            if (!string.IsNullOrEmpty(msg))
                messages.Add(new MessagePanel("region-message", "blue", "Region Message", msg));
        }

        if (locMsgRows.Count > 0)
        {
            var row  = locMsgRows[0];
            var body = Str(row, "msg2");
            if (string.IsNullOrEmpty(body)) body = Str(row, "msg1");
            var id   = Str(row, "location_id");
            messages.Add(new MessagePanel(
                Id:          string.IsNullOrEmpty(id) ? "0" : id,
                IconTone:    "green",
                Title:       "DSPM Comments",
                Body:        string.IsNullOrEmpty(body) ? "No comments yet." : body,
                ActionLabel: "Update DSPM Comment",
                Editable:    true));
        }

        return messages;
    }

    // ── Service-Parts drill-through builders ─────────────────────────────────

    private static ServicePartsDrillThrough EmptyServicePartsDrillThrough()
    {
        var empty = new DrillTable(Array.Empty<DrillCol>(), Array.Empty<DrillRow>());
        return new ServicePartsDrillThrough(
            Array.Empty<DrillTable>(), empty, empty,
            new ServiceTrainingDrill(empty, empty, empty),
            empty, empty, empty, empty, empty);
    }

    private static string DollarStr(double d) => ((long)Math.Round(d)).ToString("$#,##0");

    // Parts Purchasing (chart drill): two tables — Wholesale and Retail/MSRP — by month.
    // Mirrors dcn_dashboard_parts_whcl_detail.rdl (sp_rpt_dcn_parts_PVO).
    private static IReadOnlyList<DrillTable> BuildPartsPurchasingDrillTables(List<Dictionary<string, object?>> rows)
    {
        var wholesaleCols = new[]
        {
            new DrillCol("year",        "Year",        "13%"),
            new DrillCol("month",       "Month",       "14%"),
            new DrillCol("captive",     "Captive",     "15%", "right"),
            new DrillCol("competitive", "Competitive", "16%", "right"),
            new DrillCol("fleetValue",  "FleetValue",  "14%", "right"),
            new DrillCol("total",       "Total",       "14%", "right"),
            new DrillCol("objective",   "Objective",   "14%", "right"),
        };
        var retailCols = new[]
        {
            new DrillCol("year",        "Year",        "16%"),
            new DrillCol("month",       "Month",       "16%"),
            new DrillCol("captive",     "Captive",     "17%", "right"),
            new DrillCol("competitive", "Competitive", "17%", "right"),
            new DrillCol("fleetValue",  "FleetValue",  "17%", "right"),
            new DrillCol("total",       "Total",       "17%", "right"),
        };

        if (rows.Count == 0)
            return new[] { new DrillTable(wholesaleCols, Array.Empty<DrillRow>()), new DrillTable(retailCols, Array.Empty<DrillRow>()) };

        var ordered = rows.OrderBy(r => Int(r, "date_id")).ToList();

        var wholesaleRows = ordered.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["year"]        = Str(r, "date_year"),
            ["month"]       = Str(r, "month_name_short"),
            ["captive"]     = DollarStr(Dbl(r, "captive_amount")),
            ["competitive"] = DollarStr(Dbl(r, "competitive_amount") + Dbl(r, "fleetval_amount")),
            ["fleetValue"]  = DollarStr(Dbl(r, "fleetvalue_amount")),
            ["total"]       = DollarStr(Dbl(r, "total parts")),
            ["objective"]   = DollarStr(Dbl(r, "Objective")),
        })).ToList();

        var retailRows = ordered.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["year"]        = Str(r, "date_year"),
            ["month"]       = Str(r, "month_name_short"),
            ["captive"]     = DollarStr(Dbl(r, "Captive_retail")),
            ["competitive"] = DollarStr(Dbl(r, "Competitive_retail")),
            ["fleetValue"]  = DollarStr(Dbl(r, "fv_retail")),
            ["total"]       = DollarStr(Dbl(r, "total_retail")),
        })).ToList();

        return new[] { new DrillTable(wholesaleCols, wholesaleRows), new DrillTable(retailCols, retailRows) };
    }

    // CSI detail: Dealer / National Average / COE Minimum rows × CSI metric columns.
    // Mirrors dcn_dashboard_csi_detail.rdl (sp_rpt_csi_summary lookups, COE min = 92%).
    private static DrillTable BuildCsiDrillTable(
        List<Dictionary<string, object?>> csiRows,
        List<Dictionary<string, object?>> csiScoreRows)
    {
        var cols = new[]
        {
            new DrillCol("metric", "Metric",              "26%"),
            new DrillCol("ct",     "Customer Treatment",  "16%", "center"),
            new DrillCol("re",     "Repair Expectation",  "16%", "center"),
            new DrillCol("st",     "Schedule & Timing",   "15%", "center"),
            new DrillCol("doc",    "Document",            "13%", "center"),
            new DrillCol("total",  "Total",               "14%", "center"),
        };

        static string P(string v) => string.IsNullOrEmpty(v) ? v : v + "%";

        DrillRow MakeRow(string metric, string ct, string re, string st, string doc, string total) =>
            new(new Dictionary<string, string>
            {
                ["metric"] = metric, ["ct"] = P(ct), ["re"] = P(re),
                ["st"] = P(st), ["doc"] = P(doc), ["total"] = P(total),
            });

        var dataRows = new List<DrillRow>();

        if (csiScoreRows.Count > 0)
        {
            const string ct = "Customer Treatment", cre = "Customer Repair Expectations",
                         st = "Schedule & Timing",  doc = "Documentation on Repairs & Charges";
            string G(string q) => CsiLookup(csiScoreRows, q, "g_avg");
            string N(string q) => CsiLookup(csiScoreRows, q, "n_avg");

            dataRows.Add(MakeRow("Dealer", G(ct), G(cre), G(st), G(doc), CsiAvg(G(ct), G(cre), G(st), G(doc))));
            dataRows.Add(MakeRow("National Average", N(ct), N(cre), N(st), N(doc), CsiAvg(N(ct), N(cre), N(st), N(doc))));
        }
        else if (csiRows.Count > 0)
        {
            var r = csiRows[0];
            dataRows.Add(MakeRow("Dealer",
                Str(r, "csi_cst_trt"), Str(r, "csi_rpr_exp"), Str(r, "csi_sch_tim"), Str(r, "csi_doc_chg"), Str(r, "csi_overall")));
            dataRows.Add(MakeRow("National Average",
                Str(r, "csi_cst_trt_nat"), Str(r, "csi_rpr_exp_nat"), Str(r, "csi_sch_tim_nat"), Str(r, "csi_doc_chg_nat"), Str(r, "csi_tot_nat")));
        }

        return new DrillTable(cols, dataRows);
    }

    // IRIS detail: horizontal — YTD / 1st half / 2nd half net utilization as one row.
    private static DrillTable BuildIrisDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("ytd",    "Year to Date",            "34%", "center"),
            new DrillCol("first",  "First half of the year",  "33%", "center"),
            new DrillCol("second", "Second half of the year", "33%", "center"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        static string P(string v)
        {
            if (!double.TryParse(v, out var d)) return v;
            return Math.Round(d, 1).ToString("0.#") + "%";
        }

        var r = rows[0];
        var dataRows = new[]
        {
            new DrillRow(new Dictionary<string, string>
            {
                ["ytd"]    = P(Str(r, "ytd_net_utilization")),
                ["first"]  = P(Str(r, "1st_half_net_utilization")),
                ["second"] = P(Str(r, "2nd_half_net_utilization")),
            }),
        };

        return new DrillTable(cols, dataRows);
    }

    // Service training: summary (% certified by category) + person list + per-person courses.
    // Each list row carries userId so the frontend can filter the individuals on row click.
    private static ServiceTrainingDrill BuildServiceTrainingDrill(
        List<Dictionary<string, object?>> summaryRows,
        List<Dictionary<string, object?>> listRows,
        List<Dictionary<string, object?>> indRows)
    {
        // Table 1 — % Certified by training category (sp_rpt_dcn_service_training).
        var summaryCols = new[]
        {
            new DrillCol("training", "Training",        "60%"),
            new DrillCol("percent",  "Percent Trained", "40%", "right"),
        };
        var summaryData = summaryRows.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["training"] = Str(r, "training"),
            ["percent"]  = Str(r, "percent_trained"),
        })).ToList();
        var summaryTable = new DrillTable(summaryCols, summaryData);

        var listCols = new[]
        {
            new DrillCol("name",     "Name",             "30%"),
            new DrillCol("role",     "Role",             "24%"),
            new DrillCol("completed","Courses Completed","16%", "center"),
            new DrillCol("total",    "Total Courses",    "15%", "center"),
            new DrillCol("percent",  "Percent Complete", "15%", "center"),
            new DrillCol("userId",   "userId",           "0%"),
        };

        static string Pct(Dictionary<string, object?> r)
        {
            var done = Dbl(r, "Courses_Completed");
            var total = Dbl(r, "total_courses_available");
            return total == 0 ? "0%" : Math.Round(done / total * 100).ToString("0") + "%";
        }

        var listData = listRows.Select(r =>
        {
            var first = Str(r, "first_name");
            var last  = Str(r, "last_name");
            var role  = Str(r, "primary_role") + (Str(r, "is_primary") == "0" ? "*" : "");
            return new DrillRow(new Dictionary<string, string>
            {
                ["name"]      = $"{first} {last}".Trim(),
                ["role"]      = role,
                ["completed"] = Str(r, "Courses_Completed"),
                ["total"]     = Str(r, "total_courses_available"),
                ["percent"]   = Pct(r),
                ["userId"]    = Str(r, "user_id"),
            });
        }).ToList();

        var indCols = new[]
        {
            new DrillCol("name",        "Name",            "13%"),
            new DrillCol("type",        "Learning Type",   "11%", "center"),
            new DrillCol("courseNo",    "Course No",       "9%",  "center"),
            new DrillCol("course",      "Course Name",     "22%"),
            new DrillCol("status",      "Completion Flag", "9%",  "center"),
            new DrillCol("date",        "Completion Date", "10%", "center"),
            new DrillCol("iltDate",     "ILT Date",        "9%",  "center"),
            new DrillCol("iltLocation", "ILT Location",    "9%",  "center"),
            new DrillCol("trainer",     "ILT Trainer",     "8%"),
            new DrillCol("userId",      "userId",          "0%"),
        };

        var indData = indRows.Select(r =>
        {
            var trainer = $"{Str(r, "ilt_trainer_first_name")} {Str(r, "ilt_trainer_last_name")}".Trim();
            return new DrillRow(new Dictionary<string, string>
            {
                ["name"]        = Str(r, "Name"),
                ["type"]        = Str(r, "learning_type"),
                ["courseNo"]    = Str(r, "course_no"),
                ["course"]      = Str(r, "course_name"),
                ["status"]      = Str(r, "completion_flag"),
                ["date"]        = Str(r, "completion_date"),
                ["iltDate"]     = Str(r, "ilt_date"),
                ["iltLocation"] = Str(r, "ilt_location"),
                ["trainer"]     = trainer,
                ["userId"]      = Str(r, "user_id"),
            });
        }).ToList();

        return new ServiceTrainingDrill(
            summaryTable,
            new DrillTable(listCols, listData),
            new DrillTable(indCols, indData));
    }

    // Service & Parts Co-Op detail (sp_rpt_dcn_service_Co_Op).
    private static DrillTable BuildServiceCoopDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("period",    "Time Period",     "25%"),
            new DrillCol("earned",    "Total Reward",    "25%", "right"),
            new DrillCol("used",      "Reward Used",     "25%", "right"),
            new DrillCol("remaining", "Remaining Reward","25%", "right"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = rows.Select(r =>
        {
            var period = Str(r, "part of year");
            if (string.IsNullOrEmpty(period)) period = Str(r, "period");
            return new DrillRow(new Dictionary<string, string>
            {
                ["period"]    = period,
                ["earned"]    = DollarStr(Dbl(r, "total_reward")),
                ["used"]      = DollarStr(Dbl(r, "reward_used")),
                ["remaining"] = DollarStr(Dbl(r, "reward_remaining")),
            });
        }).ToList();

        return new DrillTable(cols, dataRows);
    }

    // DPPP Accruals detail (sp_rpt_dcn_parts_dppp rpt_lvl 2).
    private static DrillTable BuildDpppDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("periodEnd",  "Period Ending",    "13%"),
            new DrillCol("purchases",  "Parts Purch. YTD", "14%", "right"),
            new DrillCol("accrual",    "DPPP Accrual",     "13%", "right"),
            new DrillCol("scrap",      "Mandatory Scrap",  "12%", "right"),
            new DrillCol("cleanInv",   "Max Clean Inv",    "12%", "right"),
            new DrillCol("status",     "Status",           "9%",  "center"),
            new DrillCol("creditDate", "Credit Date",      "11%", "center"),
            new DrillCol("creditMemo", "Credit Memo #",    "12%", "center"),
            new DrillCol("claim",      "Claim Amount",     "12%", "right"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = rows.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["periodEnd"]  = Str(r, "period_end"),
            ["purchases"]  = DollarStr(Dbl(r, "part_purchase_ytd")),
            ["accrual"]    = DollarStr(Dbl(r, "dppp_accrual")),
            ["scrap"]      = Str(r, "mandatory_scrap"),
            ["cleanInv"]   = Str(r, "max_clean_inv"),
            ["status"]     = Str(r, "status"),
            ["creditDate"] = Str(r, "credit_date"),
            ["creditMemo"] = Str(r, "credit_memo_num"),
            ["claim"]      = DollarStr(Dbl(r, "claim_amount")),
        })).ToList();

        return new DrillTable(cols, dataRows);
    }

    // FV Parts Sales vs Total detail (sp_rpt_dcn_parts_PVO, monthly fleet value share).
    private static DrillTable BuildFvPartsSalesDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("year",    "Year",                "18%"),
            new DrillCol("month",   "Month",               "20%"),
            new DrillCol("fv",      "Fleetvalue",          "20%", "right"),
            new DrillCol("total",   "Total Parts",         "20%", "right"),
            new DrillCol("percent", "Percent Total Parts", "22%", "right"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        var ordered = rows.OrderBy(r => Int(r, "date_id")).ToList();
        var dataRows = ordered.Select(r =>
        {
            var fv    = Dbl(r, "fleetvalue_amount");
            var total = Dbl(r, "total parts");
            var pct   = total == 0 ? "" : Math.Round(fv / total * 100, 1).ToString("0.#") + "%";
            return new DrillRow(new Dictionary<string, string>
            {
                ["year"]    = Str(r, "date_year"),
                ["month"]   = Str(r, "month_name_short"),
                ["fv"]      = DollarStr(fv),
                ["total"]   = DollarStr(total),
                ["percent"] = pct,
            });
        }).ToList();

        return new DrillTable(cols, dataRows);
    }

    // National Program Submissions detail (sp_rpt_dcn_parts_incentives rpt_lvl 2).
    // Pivoted: one row per personnel, one column per FV incentive category (payment_reference),
    // value = incentive count, plus a Total Payments ($) column. A Total footer row sums each column.
    private static DrillTable BuildNationalSubmissionsDrillTable(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new DrillTable(
                new[]
                {
                    new DrillCol("personnel", "Dealer Personnel", "30%"),
                    new DrillCol("payments",  "Total Payments",   "20%", "right"),
                },
                Array.Empty<DrillRow>());

        // Distinct incentive categories (payment_reference) become the pivot columns.
        var categories = rows
            .Select(r => Str(r, "payment_reference"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        // Stable key per category for col.key (avoid spaces/specials).
        string CatKey(int i) => "cat" + i;

        var cols = new List<DrillCol> { new DrillCol("personnel", "Dealer Personnel", "20%") };
        for (int i = 0; i < categories.Count; i++)
            cols.Add(new DrillCol(CatKey(i), categories[i], "10%", "right"));
        cols.Add(new DrillCol("payments", "Total Payments", "14%", "right"));

        var byPerson = rows
            .GroupBy(r => Str(r, "personnel"))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .OrderBy(g => g.Key);

        var dataRows = new List<DrillRow>();
        foreach (var g in byPerson)
        {
            var cells = new Dictionary<string, string> { ["personnel"] = g.Key };
            for (int i = 0; i < categories.Count; i++)
            {
                var count = g.Where(r => Str(r, "payment_reference") == categories[i])
                             .Sum(r => Dbl(r, "total_incentives"));
                cells[CatKey(i)] = ((long)Math.Round(count)).ToString();
            }
            cells["payments"] = DollarStr(g.Sum(r => Dbl(r, "total_payments")));
            dataRows.Add(new DrillRow(cells));
        }

        return new DrillTable(cols, dataRows);
    }

    // Back Orders detail (sp_rpt_dcn_backorder_Service).
    private static DrillTable BuildBackOrderDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("dlrCode",  "Dealer Code",   "9%",  "center"),
            new DrillCol("ordNo",    "Order Number",  "11%", "center"),
            new DrillCol("ordDate",  "Order Date",    "10%", "center"),
            new DrillCol("ordRef",   "Order Ref",     "9%",  "center"),
            new DrillCol("part",     "Order Part",    "12%"),
            new DrillCol("fillCode", "Fill Code",     "8%",  "center"),
            new DrillCol("qty",      "BO Qty",        "7%",  "right"),
            new DrillCol("desc",     "Description",   "20%"),
            new DrillCol("upgrade",  "Upgrade Flag",  "8%",  "center"),
            new DrillCol("remarks",  "Remarks",       "6%"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = rows.Select(r =>
        {
            var remarks = string.Join(" ", new[]
            {
                Str(r, "boremarks1"), Str(r, "boremarks2"), Str(r, "boremarks3")
            }.Where(s => !string.IsNullOrEmpty(s)));

            return new DrillRow(new Dictionary<string, string>
            {
                ["dlrCode"]  = Str(r, "dlrcd"),
                ["ordNo"]    = Str(r, "dlrOrdNo"),
                ["ordDate"]  = Str(r, "orddte"),
                ["ordRef"]   = Str(r, "ordref"),
                ["part"]     = Str(r, "ordpart"),
                ["fillCode"] = Str(r, "fillcode"),
                ["qty"]      = Str(r, "boqty"),
                ["desc"]     = Str(r, "desc"),
                ["upgrade"]  = Str(r, "upgradeflag"),
                ["remarks"]  = remarks,
            });
        }).ToList();

        return new DrillTable(cols, dataRows);
    }

    // ── EV Readiness page data ───────────────────────────────────────────────

    public EvReadinessDashboard GetEvReadinessData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new EvReadinessDashboard("EV Readiness", "0%", string.Empty,
                Array.Empty<EvSection>(), string.Empty, EmptyEvDrillThrough());

        var xmlParms        = BuildOverviewXmlParms(filters);
        var ciRows          = TryExecute("sp_rpt_dcn_ci_overview", xmlParms);
        var surveyRows      = TryExecuteEvSurvey(xmlParms, 5, null);
        // HV course completion: rpt_level 6 = Service Technician (box4), 7 = Svc/Parts Mgmt (box5).
        var box4Rows        = TryExecuteEvSurvey(xmlParms, 6, 21);
        var box5Rows        = TryExecuteEvSurvey(xmlParms, 7, 21);
        // Step2 instructor-led person lists come from EV training views (keyed by user_id).
        var techRows        = TryExecuteEvView("vw_ev_tech_training",    filters.GeoValue);
        var serviceRows     = TryExecuteEvView("vw_ev_service_training", filters.GeoValue);
        var partsRows       = TryExecuteEvView("vw_ev_parts_training",   filters.GeoValue);

        // Individual EV training detail per person (sp_rpt_dcn_ev_training_ind needs @userid).
        var indByUser = new List<Dictionary<string, object?>>();
        var seenUsers = new HashSet<string>();
        foreach (var r in techRows.Concat(serviceRows).Concat(partsRows))
        {
            var uid = Str(r, "user_id");
            var gid = Str(r, "personnel_group_id");
            if (string.IsNullOrEmpty(uid) || !seenUsers.Add(uid)) continue;
            foreach (var ind in TryExecuteEvTrainingInd(xmlParms, uid, gid))
            {
                ind["user_id"] = uid;
                indByUser.Add(ind);
            }
        }

        return BuildEvReadinessDashboard(ciRows, surveyRows, box4Rows, box5Rows,
            techRows, serviceRows, partsRows, indByUser);
    }

    // Raw SQL on the EV training views (vw_ev_tech_training / vw_ev_service_training / vw_ev_parts_training).
    private List<Dictionary<string, object?>> TryExecuteEvView(string viewName, string geoValue)
    {
        try
        {
            using var conn = OpenConnection();
            var sql =
                $"SELECT a.* FROM [ICTA_DEALER_SURVEY_App].[dbo].[{viewName}] a " +
                "JOIN dim_location b ON a.dealer_code = b.location_code " +
                "WHERE b.location_id = @geo ORDER BY a.dealer_code, a.primary_role DESC";
            return conn.Query(sql, new { geo = geoValue })
                .Select(r =>
                {
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in (IDictionary<string, object>)r) d[kv.Key] = kv.Value;
                    return d;
                })
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{ViewName} query failed", viewName);
            return new List<Dictionary<string, object?>>();
        }
    }

    // sp_rpt_dcn_ev_mobile_survey: @rpt_level + optional @survey_id as extra SQL params (not in XML)
    private List<Dictionary<string, object?>> TryExecuteEvSurvey(string xmlParms, int rptLevel, int? surveyId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = new SqlCommand("sp_rpt_dcn_ev_mobile_survey", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 60
            };
            cmd.Parameters.Add("@parms", SqlDbType.Xml).Value = xmlParms;
            cmd.Parameters.AddWithValue("@debug", "n");
            cmd.Parameters.AddWithValue("@rpt_level", rptLevel);
            cmd.Parameters.Add("@survey_id", SqlDbType.Int).Value = (object?)surveyId ?? DBNull.Value;

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
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_dcn_ev_mobile_survey failed");
            return new List<Dictionary<string, object?>>();
        }
    }

    // sp_rpt_dcn_ev_training_ind: per-person individual EV training (@userid + @personnel_group_id).
    private List<Dictionary<string, object?>> TryExecuteEvTrainingInd(string xmlParms, string userId, string groupId)
    {
        try
        {
            using var conn = OpenConnection();
            using var cmd = new SqlCommand("sp_rpt_dcn_ev_training_ind", conn)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 60
            };
            cmd.Parameters.Add("@parms", SqlDbType.Xml).Value = xmlParms;
            cmd.Parameters.AddWithValue("@debug", "n");
            cmd.Parameters.Add("@userid", SqlDbType.VarChar, 50).Value = string.IsNullOrEmpty(userId) ? DBNull.Value : userId;
            cmd.Parameters.Add("@personnel_group_id", SqlDbType.Int).Value =
                int.TryParse(groupId, out var g) ? g : (object)DBNull.Value;

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
        catch (Exception ex)
        {
            Log.Warning(ex, "sp_rpt_dcn_ev_training_ind failed");
            return new List<Dictionary<string, object?>>();
        }
    }

    private static EvDrillThrough EmptyEvDrillThrough()
    {
        var empty = new DrillTable(Array.Empty<DrillCol>(), Array.Empty<DrillRow>());
        return new EvDrillThrough(empty, empty, empty, empty, empty, empty);
    }

    private static EvReadinessDashboard BuildEvReadinessDashboard(
        List<Dictionary<string, object?>> ciRows,
        List<Dictionary<string, object?>> surveyRows,
        List<Dictionary<string, object?>> box4Rows,
        List<Dictionary<string, object?>> box5Rows,
        List<Dictionary<string, object?>> techRows,
        List<Dictionary<string, object?>> serviceRows,
        List<Dictionary<string, object?>> partsRows,
        List<Dictionary<string, object?>> individualsRows)
    {
        if (surveyRows.Count == 0)
            return new EvReadinessDashboard("EV Readiness", "0%", string.Empty,
                Array.Empty<EvSection>(), string.Empty, EmptyEvDrillThrough());

        var r = surveyRows[0];

        var evCert  = Str(r, "ev_cert");
        var whslPct = Str(r, "whsl_pct");
        var nnnPct  = Str(r, "nnn_pct");

        // RDL renders ev_cert as CStr(value) & "%" — the SP already returns an integer
        var progressValue = $"EV Certified: {(string.IsNullOrEmpty(evCert) ? "0" : evCert)}%";

        // interest: binary 0/1 → state "idle"/"complete"
        var interest      = Int(r, "interest");
        var interestState = interest > 0 ? "complete" : "idle";

        // isuzu_connect SWITCH: both=0 → 0%, registered=1 → 50%, active=1 → 100%
        var isuzuConnect    = Int(r, "isuzu_connect");
        var isuzuRegistered = Int(r, "isuzu_connect_registered");
        var isuzuCompletion = (isuzuConnect == 0 && isuzuRegistered == 0) ? 0.0
                            : isuzuRegistered == 1 ? 50.0
                            : 100.0;
        var isuzuSubtitle   = (isuzuConnect == 0 && isuzuRegistered == 0) ? "Not Using Isuzu Connect"
                            : isuzuRegistered == 1 ? "Registered for Isuzu Connect"
                            : "Using Isuzu Connect";
        var isuzuState      = isuzuCompletion >= 100 ? "complete" : isuzuCompletion > 0 ? "active" : "idle";

        static EvCard TrainingCard(string title, string subtitle, double raw)
        {
            var pct   = Math.Round(raw);
            var state = pct >= 100 ? "complete" : pct > 0 ? "active" : "idle";
            return new EvCard(title, subtitle, pct, state, "training");
        }

        var step1Cards = new EvCard[]
        {
            new("Becoming EV Certified",
                "Dealer has expressed interest in EV certification",
                interest > 0 ? 100.0 : 0.0, interestState, "ribbon"),
            new("Isuzu Connect Status",
                isuzuSubtitle,
                isuzuCompletion, isuzuState, "plug"),
            TrainingCard("High Voltage Vehicle Awareness Training",
                "Required safety training for high-voltage vehicle handling",
                Dbl(r, "hv_handle_safety")),
            // Box 4 — clickable drill-through (Service Technician course completion).
            TrainingCard("High Voltage Vehicle Awareness Service Technician Training",
                "HV awareness training for service technicians",
                Dbl(r, "lvl_1_2_techs_training")) with { DrillType = "ev/step1/box4" },
            // Box 5 — clickable drill-through (Svc/Parts management course completion).
            TrainingCard("High Voltage Vehicle Awareness Svc Mgr/Svc Adv & Part Mgr/Part Cntr Training",
                "HV awareness training for service & parts management",
                Dbl(r, "svc_parts_training")) with { DrillType = "ev/step1/box5" },
            TrainingCard("EV Special Service Tools & High Voltage PPE",
                "Required EV special service tools and high-voltage PPE",
                Dbl(r, "tools_equip_ppe")),
        };

        var step2Cards = new EvCard[]
        {
            TrainingCard("High Voltage Vehicle Awareness Sales Training",
                "HV vehicle awareness training for the sales team",
                Dbl(r, "sales_training")),
            TrainingCard("Instructor-led & Computer-based Service Technician Training",
                "Advanced instructor-led EV training for service technicians",
                Dbl(r, "tech_training")) with { DrillType = "ev/step2/instructor-led/tech" },
            TrainingCard("Instructor-led & Computer-based Svc Mgr/Svc Adv Training",
                "EV training for service managers and advisors",
                Dbl(r, "service_training")) with { DrillType = "ev/step2/instructor-led/service" },
            TrainingCard("Instructor-led & Computer-based Part Mgr/Part Cntr Training",
                "EV training for parts managers and counter staff",
                Dbl(r, "parts_training")) with { DrillType = "ev/step2/instructor-led/parts" },
            TrainingCard("Dedicated EV Charger",
                "Dedicated EV charging equipment installed at dealership",
                Dbl(r, "charge_equip")),
        };

        return new EvReadinessDashboard(
            ProgressLabel:  "Overall EV Certification Progress",
            ProgressValue:  progressValue,
            ProgressHelp:   "EV Certification requires completion of both wholesale and retail requirements.",
            Sections: new[]
            {
                new EvSection($"Step 1: Wholesale Requirements", $"{(string.IsNullOrEmpty(whslPct) ? "0" : whslPct)}%", step1Cards),
                new EvSection($"Step 2: Retail Requirements",    $"{(string.IsNullOrEmpty(nnnPct)  ? "0" : nnnPct)}%",  step2Cards),
            },
            FooterAlert: "Complete all steps to achieve EV certification. Contact your District Manager for assistance.",
            DrillThrough: new EvDrillThrough(
                Box4:                 BuildEvCourseDrillTable(box4Rows),
                Box5:                 BuildEvCourseDrillTable(box5Rows),
                InstructorLedTech:    BuildEvInstructorLedDrillTable(techRows),
                InstructorLedService: BuildEvInstructorLedDrillTable(serviceRows),
                InstructorLedParts:   BuildEvInstructorLedDrillTable(partsRows),
                Individuals:          BuildEvIndividualsDrillTable(individualsRows)));
    }

    // ── EV drill-through builders ────────────────────────────────────────────

    // Box4 / Box5: per-personnel course completion (sp_rpt_dcn_ev_mobile_survey rpt_level 6 / 7).
    private static DrillTable BuildEvCourseDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("personnel", "Personnel Name",  "20%"),
            new DrillCol("goIsuzuId", "GoIsuzu ID",      "13%", "center"),
            new DrillCol("role",      "Role",            "16%"),
            new DrillCol("courseNo",  "Courses Number",  "12%", "center"),
            new DrillCol("courseName","Course Name",     "21%"),
            new DrillCol("completed", "Course Completed","10%", "center"),
            new DrillCol("date",      "Completion Date", "8%",  "center"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = rows.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["personnel"]  = Str(r, "personnel_name"),
            ["goIsuzuId"]  = Str(r, "goisuzu_id"),
            ["role"]       = Str(r, "primary_role"),
            ["courseNo"]   = Str(r, "course_no"),
            ["courseName"] = Str(r, "course_name"),
            ["completed"]  = Str(r, "completion_flag"),
            ["date"]       = Str(r, "completion_date"),
        })).ToList();

        return new DrillTable(cols, dataRows);
    }

    // Step2 instructor-led person list (sp_rpt_dcn_ev_mobile_survey rpt_level 2, one row per person).
    // Carries userId so the frontend can open that person's individual training on click.
    private static DrillTable BuildEvInstructorLedDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("personnel", "Personnel Name",              "30%"),
            new DrillCol("role",      "Role",                        "24%"),
            new DrillCol("assigned",  "Courses Assigned",            "15%", "center"),
            new DrillCol("completed", "Course Completed",            "15%", "center"),
            new DrillCol("percent",   "Percent of Courses Completed","16%", "center"),
            new DrillCol("userId",    "userId",                      "0%"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        // One row per distinct personnel (survey detail repeats personnel per course).
        var dataRows = rows
            .GroupBy(r => Str(r, "user_id"))
            .Select(g =>
            {
                var r = g.First();
                var pct = Str(r, "pct_complete");
                if (double.TryParse(pct, out var p)) pct = Math.Round(p).ToString("0") + "%";
                return new DrillRow(new Dictionary<string, string>
                {
                    ["personnel"] = Str(r, "personnel_name"),
                    ["role"]      = Str(r, "primary_role"),
                    ["assigned"]  = Str(r, "course_assigned"),
                    ["completed"] = Str(r, "course_completed"),
                    ["percent"]   = pct,
                    ["userId"]    = Str(r, "user_id"),
                });
            })
            .ToList();

        return new DrillTable(cols, dataRows);
    }

    // Individual EV training detail (sp_rpt_dcn_ev_training_ind). userId kept for client filtering.
    private static DrillTable BuildEvIndividualsDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("name",       "Name",            "14%"),
            new DrillCol("type",       "Learning Type",   "12%", "center"),
            new DrillCol("courseNo",   "Course Number",   "10%", "center"),
            new DrillCol("courseName", "Course Name",     "20%"),
            new DrillCol("completed",  "Completed",       "9%",  "center"),
            new DrillCol("date",       "Completion Date", "11%", "center"),
            new DrillCol("trainDate",  "Training Date",   "11%", "center"),
            new DrillCol("location",   "Location",        "8%",  "center"),
            new DrillCol("trainer",    "Trainer Name",    "5%"),
            new DrillCol("userId",     "userId",          "0%"),
        };

        if (rows.Count == 0) return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = rows.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["name"]       = Str(r, "Name"),
            ["type"]       = Str(r, "learning_type"),
            ["courseNo"]   = Str(r, "course_no"),
            ["courseName"] = Str(r, "course_name"),
            ["completed"]  = Str(r, "completion_flag"),
            ["date"]       = Str(r, "completion_date"),
            ["trainDate"]  = Str(r, "ilt_date"),
            ["location"]   = Str(r, "ilt_location"),
            ["trainer"]    = Str(r, "trainer_name"),
            ["userId"]     = Str(r, "user_id"),
        })).ToList();

        return new DrillTable(cols, dataRows);
    }

    // ── Sales page data ──────────────────────────────────────────────────────

    public SalesDashboard GetSalesData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new SalesDashboard(Array.Empty<SalesRow>(), Array.Empty<MessagePanel>(), EmptySalesDrillThrough());

        var xmlParms          = BuildOverviewXmlParms(filters);
        var salesTotalRows         = TryExecute("sp_rpt_dcn_sales_total",              xmlParms);
        var salesSummaryRows       = TryExecute("sp_rpt_dcn_sales_total_subreport",    xmlParms);
        var salesDetailRows        = TryExecuteWithRptLvl("sp_rpt_dcn_sales_total",    xmlParms, 2);
        var inventoryRows          = TryExecute("sp_rpt_dcn_inven_type_sales",         xmlParms);
        var vehicleInfoRows        = TryExecute("sp_rpt_dcn_vehicleinfo_Sales",        xmlParms);
        var vehicleOrderDetailRows = TryExecuteWithRptLvl("sp_rpt_dcn_vehicleinfo_Sales", xmlParms, 2);
        var coopTruckRows          = TryExecute("sp_rpt_dcn_coop_trucks",              xmlParms);
        var demosRows              = TryExecute("sp_rpt_dcn_Demos_overview",           xmlParms);
        var trainingRows           = TryExecute("sp_rpt_dcn_sale_training",            xmlParms);
        var trainingDetailRows     = TryExecuteWithRptLvl("sp_rpt_dcn_sale_training",  xmlParms, 2);
        var indShrRows             = TryExecute("sp_rpt_dcn_ind_shr",                  xmlParms);
        var execMsgRows            = TryExecute("sp_rpt_dcn_exec_message",             xmlParms);
        var locMsgRows             = TryExecute("sp_rpt_dcn_loc_message",              xmlParms);
        var regnMsgRows            = TryExecute("sp_rpt_dcn_regn_message",             xmlParms);

        return new SalesDashboard(
            Rows:        BuildSalesChartRows(salesTotalRows, inventoryRows, vehicleInfoRows, coopTruckRows, demosRows, trainingRows, indShrRows),
            Messages:    BuildSalesMessages(execMsgRows, locMsgRows, regnMsgRows),
            DrillThrough: BuildSalesDrillThrough(salesSummaryRows, salesDetailRows, inventoryRows, vehicleInfoRows, demosRows, coopTruckRows, indShrRows, vehicleOrderDetailRows, trainingDetailRows));
    }

    private static SalesDrillThrough EmptySalesDrillThrough()
    {
        var empty = new DrillTable(Array.Empty<DrillCol>(), Array.Empty<DrillRow>());
        return new SalesDrillThrough(
            Array.Empty<DrillTable>(), Array.Empty<DrillTable>(), Array.Empty<DrillTable>(),
            Array.Empty<DrillTable>(), Array.Empty<DrillTable>(),
            empty, empty, empty, empty);
    }

    private static SalesDrillThrough BuildSalesDrillThrough(
        List<Dictionary<string, object?>> salesSummaryRows,
        List<Dictionary<string, object?>> salesDetailRows,
        List<Dictionary<string, object?>> inventoryRows,
        List<Dictionary<string, object?>> vehicleInfoRows,
        List<Dictionary<string, object?>> demosRows,
        List<Dictionary<string, object?>> coopTruckRows,
        List<Dictionary<string, object?>> indShrRows,
        List<Dictionary<string, object?>> vehicleOrderDetailRows,
        List<Dictionary<string, object?>> trainingDetailRows)
    {
        // Each truck-sales drill shows 2 tables: [0] monthly summary (footer total handled
        // client-side), [1] VIN-level detail (all series for Total, prefix-filtered for N/F).
        var summaryTable = BuildSalesSummaryDrillTable(salesSummaryRows);

        return new SalesDrillThrough(
            TotalTruckSales: new[] { summaryTable, BuildVinDetailDrillTable(salesDetailRows, "")  },
            NSeries:         new[] { summaryTable, BuildVinDetailDrillTable(salesDetailRows, "N") },
            FSeries:         new[] { summaryTable, BuildVinDetailDrillTable(salesDetailRows, "F") },
            Inventory:       BuildInventoryDrillTables(inventoryRows),
            Orders:          BuildOrdersDrillTables(vehicleInfoRows, vehicleOrderDetailRows),
            DemosPaid:       BuildDemosPaidDrillTable(demosRows),
            CoOpUtilization: BuildCoOpDrillTable(coopTruckRows),
            SoaMarketShare:  BuildSoaMarketShareDrillTable(indShrRows),
            SalesTraining:   BuildSalesTrainingDrillTable(trainingDetailRows));
    }

    // Monthly truck-sales summary. The "Total" row is rendered as a client-side footer,
    // so it is intentionally not appended here.
    private static DrillTable BuildSalesSummaryDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("year",    "Year",      "10%"),
            new DrillCol("month",   "Month",     "12%"),
            new DrillCol("nGas",    "N-Gas",     "14%", "center"),
            new DrillCol("nDiesel", "N-Diesel",  "14%", "center"),
            new DrillCol("nEv",     "N-EV",      "14%", "center"),
            new DrillCol("fSeries", "F-Series",  "14%", "center"),
            new DrillCol("total",   "Total",     "22%", "center"),
        };

        if (rows.Count == 0)
            return new DrillTable(cols, Array.Empty<DrillRow>());

        var ordered = rows.OrderBy(r => Int(r, "date_year")).ThenBy(r => Int(r, "date_month")).ToList();
        var dataRows = ordered.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["year"]    = Str(r, "date_year"),
            ["month"]   = Str(r, "month_name_short"),
            ["nGas"]    = Str(r, "N_Series_Gas"),
            ["nDiesel"] = Str(r, "N_Series_Diesel"),
            ["nEv"]     = Str(r, "N_Series_EV"),
            ["fSeries"] = Str(r, "F_Series"),
            ["total"]   = Str(r, "units"),
        })).ToList();

        return new DrillTable(cols, dataRows);
    }

    private static DrillTable BuildVinDetailDrillTable(List<Dictionary<string, object?>> rows, string seriesPrefix)
    {
        var cols = new[]
        {
            new DrillCol("modelYear",   "Model Year",              "8%",  "center"),
            new DrillCol("model",       "Model",                   "8%",  "center"),
            new DrillCol("series",      "Truck Series",            "9%",  "center"),
            new DrillCol("occ",         "OCC",                     "5%",  "center"),
            new DrillCol("vin",         "VIN",                     "17%"),
            new DrillCol("saleDate",    "Retail Sales Date",       "10%", "center"),
            new DrillCol("customer",    "Customer Bussiness Name", "18%"),
            new DrillCol("salesperson", "Sales Person Name",       "13%"),
            new DrillCol("group",       "Customer Group",          "8%",  "center"),
            new DrillCol("qty",         "Quantity",                "4%",  "center"),
        };

        var filtered = rows
            .Where(r => Str(r, "series").StartsWith(seriesPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => Str(r, "model_year"))
            .ThenBy(r => Date(r, "retail_sales_date"))
            .ToList();

        if (filtered.Count == 0)
            return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = filtered.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["modelYear"]   = Str(r, "model_year"),
            ["model"]       = Str(r, "model"),
            ["series"]      = Str(r, "series"),
            ["occ"]         = Str(r, "occ"),
            ["vin"]         = Str(r, "vin"),
            ["saleDate"]    = DateFmt(r, "retail_sales_date"),
            ["customer"]    = Str(r, "cust_bussiness_name"),
            ["salesperson"] = Str(r, "sales_person_name"),
            ["group"]       = Str(r, "customer_group"),
            ["qty"]         = Str(r, "quantity"),
        })).ToList();

        return new DrillTable(cols, dataRows);
    }

    private static IReadOnlyList<DrillTable> BuildInventoryDrillTables(List<Dictionary<string, object?>> rows)
    {
        // table1: series grouped summary (vertical). Total is a client-side footer.
        var summaryCols = new[]
        {
            new DrillCol("series",    "Series",    "60%"),
            new DrillCol("inventory", "Inventory", "40%", "right"),
        };

        var grouped = rows
            .GroupBy(r => Str(r, "series"))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var inv = g.Sum(r => Int(r, "inventroy"));
                if (inv == 0) inv = g.Sum(r => Int(r, "inventory"));
                return new DrillRow(new Dictionary<string, string>
                {
                    ["series"]    = g.Key,
                    ["inventory"] = inv.ToString(),
                });
            }).ToList();
        var table1 = new DrillTable(summaryCols, grouped);

        // table2: raw rows per (date_id, series) — horizontal zebra
        var detailCols = new[]
        {
            new DrillCol("dateId",    "Date",      "30%"),
            new DrillCol("series",    "Series",    "40%"),
            new DrillCol("inventory", "Inventory", "30%", "right"),
        };

        var detailRows = rows
            .OrderBy(r => Int(r, "date_id"))
            .ThenBy(r => Str(r, "series"))
            .Where(r => !string.IsNullOrEmpty(Str(r, "series")))
            .Select(r =>
            {
                var inv = Int(r, "inventroy");
                if (inv == 0) inv = Int(r, "inventory");
                return new DrillRow(new Dictionary<string, string>
                {
                    ["dateId"]    = Str(r, "date_id"),
                    ["series"]    = Str(r, "series"),
                    ["inventory"] = inv.ToString(),
                });
            }).ToList();
        var table2 = new DrillTable(detailCols, detailRows);

        return new[] { table1, table2 };
    }

    private static IReadOnlyList<DrillTable> BuildOrdersDrillTables(
        List<Dictionary<string, object?>> vehicleInfoRows,
        List<Dictionary<string, object?>> vehicleOrderDetailRows)
    {
        // table1: sale-type summary (vertical). Total is a client-side footer.
        var summaryCols = new[]
        {
            new DrillCol("type",  "Sale Type", "60%"),
            new DrillCol("units", "Units",     "40%", "right"),
        };

        var typeSaleKey = vehicleInfoRows.Count > 0 && vehicleInfoRows[0].ContainsKey("type_sale")
            ? "type_sale" : "type sale";

        var summaryRows = vehicleInfoRows
            .Select(r =>
            {
                var type = Str(r, typeSaleKey);
                if (string.IsNullOrEmpty(type)) type = Str(r, "type sale");
                return new DrillRow(new Dictionary<string, string>
                {
                    ["type"]  = type,
                    ["units"] = Str(r, "units"),
                });
            })
            .Where(dr => !string.IsNullOrEmpty(dr.Cells["type"]))
            .ToList();
        var table1 = new DrillTable(summaryCols, summaryRows);

        // table2: detailed order rows (model_year, model, series, occ, …) — horizontal zebra
        var detailCols = new[]
        {
            new DrillCol("year",         "Year",         "7%",  "center"),
            new DrillCol("model",        "Model",        "9%",  "center"),
            new DrillCol("series",       "Series",       "9%"),
            new DrillCol("occ",          "OCC",          "5%",  "center"),
            new DrillCol("salesOrder",   "Sales Order",  "10%", "center"),
            new DrillCol("salesItem",    "Item",         "7%",  "center"),
            new DrillCol("vin",          "VIN",          "16%"),
            new DrillCol("orderStatus",  "Status",       "9%",  "center"),
            new DrillCol("etsDate",      "ETS Date",     "9%",  "center"),
            new DrillCol("shipTo",       "Ship To",      "10%", "center"),
            new DrillCol("customerName", "Customer",     "9%"),
        };

        var detailRows = vehicleOrderDetailRows
            .OrderByDescending(r => Date(r, "ets_date"))
            .Select(r => new DrillRow(new Dictionary<string, string>
            {
                ["year"]         = Str(r, "model_year"),
                ["model"]        = Str(r, "model"),
                ["series"]       = Str(r, "series"),
                ["occ"]          = Str(r, "occ"),
                ["salesOrder"]   = Str(r, "sales_order"),
                ["salesItem"]    = Str(r, "sales_item"),
                ["vin"]          = Str(r, "vin"),
                ["orderStatus"]  = Str(r, "order_status"),
                ["etsDate"]      = DateFmt(r, "ets_date"),
                ["shipTo"]       = Str(r, "ship_to"),
                ["customerName"] = Str(r, "customer_name"),
            })).ToList();
        var table2 = new DrillTable(detailCols, detailRows);

        return new[] { table1, table2 };
    }

    private static DrillTable BuildDemosPaidDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("validFrom",    "Valid From",         "12.5%"),
            new DrillCol("validTo",      "Valid To",           "12.5%"),
            new DrillCol("nEarned",      "N-Demo Earned",      "12.5%", "right"),
            new DrillCol("nPaid",        "N-Demo Paid",        "12.5%", "right"),
            new DrillCol("nRemaining",   "N-Demo Remaining",   "12.5%", "right"),
            new DrillCol("ftrEarned",    "FTR Demo Earned",    "12.5%", "right"),
            new DrillCol("ftrPaid",      "FTR Demo Paid",      "12.5%", "right"),
            new DrillCol("ftrRemaining", "FTR Demo Remaining", "12.5%", "right"),
        };

        if (rows.Count == 0)
            return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = rows.Select(r => new DrillRow(new Dictionary<string, string>
        {
            ["validFrom"]    = DateFmt(r, "valid_from"),
            ["validTo"]      = DateFmt(r, "Valid_to"),
            ["nEarned"]      = Str(r, "n_demo_earned"),
            ["nPaid"]        = Str(r, "n_demo_paid"),
            ["nRemaining"]   = Str(r, "n_demo_rem"),
            ["ftrEarned"]    = Str(r, "ftr_demo_earned"),
            ["ftrPaid"]      = Str(r, "ftr_demo_paid"),
            ["ftrRemaining"] = Str(r, "ftr_demo_rem"),
        })).ToList();

        return new DrillTable(cols, dataRows);
    }

    private static DrillTable BuildCoOpDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("period",    "Period",           "22%"),
            new DrillCol("earned",    "Total Reward",     "26%", "right"),
            new DrillCol("utilized",  "Reward Used",      "26%", "right"),
            new DrillCol("remaining", "Reward Remaining", "26%", "right"),
        };

        var dataRows = rows.Select(r =>
        {
            var period = Str(r, "Period");
            if (string.IsNullOrEmpty(period)) period = Str(r, "year");
            return new DrillRow(new Dictionary<string, string>
            {
                ["period"]    = period,
                ["earned"]    = DollarFmt(r, "total_reward"),
                ["utilized"]  = DollarFmt(r, "reward_used"),
                ["remaining"] = DollarFmt(r, "reward_remaining"),
            });
        }).ToList();

        // Total row
        var totalEarned    = rows.Sum(r => Dbl(r, "total_reward"));
        var totalUtilized  = rows.Sum(r => Dbl(r, "reward_used"));
        var totalRemaining = rows.Sum(r => Dbl(r, "reward_remaining"));
        dataRows.Add(new DrillRow(new Dictionary<string, string>
        {
            ["period"]    = "Total",
            ["earned"]    = ((long)Math.Round(totalEarned)).ToString("$#,##0"),
            ["utilized"]  = ((long)Math.Round(totalUtilized)).ToString("$#,##0"),
            ["remaining"] = ((long)Math.Round(totalRemaining)).ToString("$#,##0"),
        }));

        return new DrillTable(cols, dataRows);
    }

    private static DrillTable BuildSoaMarketShareDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("date",  "Date",   "11%"),
            new DrillCol("make",  "Make",   "14%"),
            new DrillCol("model", "Model",  "22%"),
            new DrillCol("class", "Class",  "11%", "center"),
            new DrillCol("cab",   "Cab",    "11%", "center"),
            new DrillCol("fuel",  "Fuel",   "11%", "center"),
            new DrillCol("drive", "Drive",  "11%", "center"),
            new DrillCol("fleet", "Fleet",  "11%", "center"),
            new DrillCol("units", "Units",   "9%", "right"),
        };

        var dataRows = rows
            .OrderBy(r => Int(r, "date_id"))
            .ThenBy(r => Str(r, "make"))
            .Where(r => !string.IsNullOrEmpty(Str(r, "make")))
            .Select(r => new DrillRow(new Dictionary<string, string>
            {
                ["date"]  = Str(r, "date_name_yy_mon"),
                ["make"]  = Str(r, "make"),
                ["model"] = Str(r, "model"),
                ["class"] = Str(r, "class"),
                ["cab"]   = Str(r, "cab"),
                ["fuel"]  = Str(r, "fuel"),
                ["drive"] = Str(r, "drive"),
                ["fleet"] = Str(r, "fleet"),
                ["units"] = Str(r, "units"),
            })).ToList();

        return new DrillTable(cols, dataRows);
    }

    private static DrillTable BuildSalesTrainingDrillTable(List<Dictionary<string, object?>> rows)
    {
        var cols = new[]
        {
            new DrillCol("consultant",       "Sales Consultant",  "40%"),
            new DrillCol("pke",              "PKE",               "30%", "center"),
            new DrillCol("trainingWorkshop", "Training Workshop", "30%", "center"),
        };

        if (rows.Count == 0)
            return new DrillTable(cols, Array.Empty<DrillRow>());

        var dataRows = rows
            .Select(r => new DrillRow(new Dictionary<string, string>
            {
                ["consultant"]       = Str(r, "Person"),
                ["pke"]              = Str(r, "PKE"),
                ["trainingWorkshop"] = Str(r, "Training Workshop"),
            }))
            .Where(r => !string.IsNullOrEmpty(r.Cells["consultant"]))
            .ToList();

        return new DrillTable(cols, dataRows);
    }

    private static IReadOnlyList<SalesRow> BuildSalesChartRows(
        List<Dictionary<string, object?>> salesTotalRows,
        List<Dictionary<string, object?>> inventoryRows,
        List<Dictionary<string, object?>> vehicleInfoRows,
        List<Dictionary<string, object?>> coopTruckRows,
        List<Dictionary<string, object?>> demosRows,
        List<Dictionary<string, object?>> trainingRows,
        List<Dictionary<string, object?>> indShrRows)
    {
        // Unique months ordered by date_id; label comes from date_name_yy_mon
        var months = salesTotalRows
            .Select(r => (DateId: Int(r, "date_id"), Label: Str(r, "date_name_yy_mon")))
            .Where(m => !string.IsNullOrEmpty(m.Label))
            .GroupBy(m => m.DateId)
            .Select(g => g.First())
            .OrderBy(m => m.DateId)
            .ToList();

        var labels = months.Select(m => m.Label).ToArray();

        // Sum units per month filtered by the category column ("N-Series" / "F-Series")
        IReadOnlyList<double> MonthlyUnits(Func<string, bool> categoryFilter) =>
            months.Select(m =>
                (double)salesTotalRows
                    .Where(r => Int(r, "date_id") == m.DateId && categoryFilter(Str(r, "category")))
                    .Sum(r => Int(r, "units")))
            .ToArray();

        // RDL field name for inventory column is "inventroy" (SP typo); try both spellings
        int TotalInv(Func<string, bool> seriesFilter)
        {
            var filtered = inventoryRows.Where(r => seriesFilter(Str(r, "series"))).ToList();
            var v = filtered.Sum(r => Int(r, "inventroy"));
            return v > 0 ? v : filtered.Sum(r => Int(r, "inventory"));
        }

        // N-Series Co-Op Utilization: round(reward_used / total_reward * 100, 0)%
        static string CoopPct(List<Dictionary<string, object?>> rows)
        {
            var totalReward = rows.Sum(r => Dbl(r, "total_reward"));
            var rewardUsed  = rows.Sum(r => Dbl(r, "reward_used"));
            return totalReward == 0 ? "N/A" : ((int)Math.Round(rewardUsed / totalReward * 100)) + "%";
        }

        // F-Series Sales Training: round(Average_dealer_score * 100, 0)%
        static string TrainingPct(List<Dictionary<string, object?>> rows)
        {
            if (rows.Count == 0) return "N/A";
            var score = Dbl(rows[0], "Average_dealer_score");
            return ((int)Math.Round(score * 100)) + "%";
        }

        // SOA Market Share: round(ISUZU units / total units * 100, 0)%
        static string MarketShare(List<Dictionary<string, object?>> rows)
        {
            var total = rows.Sum(r => Int(r, "units"));
            var isuzu = rows
                .Where(r => Str(r, "make").Equals("ISUZU", StringComparison.OrdinalIgnoreCase))
                .Sum(r => Int(r, "units"));
            return total == 0 ? "N/A" : ((int)Math.Round((double)isuzu / total * 100)) + "%";
        }

        // Orders: from sp_rpt_dcn_vehicleinfo_Sales (field: units)
        var orders = vehicleInfoRows.Sum(r => Int(r, "units"));

        // Demos Paid: from sp_rpt_dcn_Demos_overview (field: total_paid)
        var demosPaid = demosRows.Count > 0 ? Int(demosRows[0], "total_paid") : 0;

        return new[]
        {
            new SalesRow(
                new MiniChartCard("Total Truck Sales", labels, MonthlyUnits(_ => true), "#f8a108"),
                new[]
                {
                    new SalesMetric("Inventory", TotalInv(_ => true).ToString()),
                    new SalesMetric("Orders",    orders.ToString()),
                }),
            new SalesRow(
                new MiniChartCard("N-Series Sales", labels,
                    MonthlyUnits(s => s.Equals("N-Series", StringComparison.OrdinalIgnoreCase)), "#4b83ff"),
                new[]
                {
                    new SalesMetric("Demos Paid",              demosPaid.ToString()),
                    new SalesMetric("Sales Co-Op Utilization", CoopPct(coopTruckRows)),
                }),
            new SalesRow(
                new MiniChartCard("F-Series Sales", labels,
                    MonthlyUnits(s => s.Equals("F-Series", StringComparison.OrdinalIgnoreCase)), "#ef2c30"),
                new[]
                {
                    new SalesMetric("SOA Market Share (III - VII)", MarketShare(indShrRows)),
                    new SalesMetric("Sales Training",               TrainingPct(trainingRows)),
                }),
        };
    }

    private static IReadOnlyList<MessagePanel> BuildSalesMessages(
        List<Dictionary<string, object?>> execMsgRows,
        List<Dictionary<string, object?>> locMsgRows,
        List<Dictionary<string, object?>> regnMsgRows)
    {
        var messages = new List<MessagePanel>();

        if (execMsgRows.Count > 0)
        {
            var msg = Str(execMsgRows[0], "msg1");
            if (!string.IsNullOrEmpty(msg))
                messages.Add(new MessagePanel("exec-message", "red", "Executive Message", msg));
        }

        if (regnMsgRows.Count > 0)
        {
            var msg = Str(regnMsgRows[0], "msg1");
            if (!string.IsNullOrEmpty(msg))
                messages.Add(new MessagePanel("region-message", "blue", "Region Message", msg));
        }

        if (locMsgRows.Count > 0)
        {
            var row  = locMsgRows[0];
            var body = Str(row, "msg1");
            var id   = Str(row, "location_id");
            messages.Add(new MessagePanel(
                Id:          string.IsNullOrEmpty(id) ? "0" : id,
                IconTone:    "green",
                Title:       "DSM Comments",
                Body:        string.IsNullOrEmpty(body) ? "No comments yet." : body,
                ActionLabel: "Update DSM Comment",
                Editable:    true));
        }

        return messages;
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

    public void UpdateLocationMessage(int locationId, string body)
    {
        // Mirror DCNRepository.SaveMessage: split on '|' into msg1/msg2, unescape <basl> → /
        var parts = body.Split('|');
        var msg1 = parts[0].Replace("<basl>", "/");
        var msg2 = parts.Length > 1 ? parts[1].Replace("<basl>", "/") : string.Empty;
        var locId = locationId.ToString();

        using var conn = OpenConnection();
        conn.Execute("sp_utl_update_data",
            new { tbl = "dcn.location_msg", key = "location_id", value = locId, col = "msg1", data = msg1 },
            commandType: CommandType.StoredProcedure);

        if (!string.IsNullOrEmpty(msg2))
            conn.Execute("sp_utl_update_data",
                new { tbl = "dcn.location_msg", key = "location_id", value = locId, col = "msg2", data = msg2 },
                commandType: CommandType.StoredProcedure);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildInitials(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return "?";
        var parts = userId.Split('@')[0].Split('.');
        return parts.Length >= 2
            ? $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}"
            : userId.Length > 0
                ? $"{char.ToUpper(userId[0])}"
                : "?";
    }

    private static readonly string[] _tones = { "indigo", "teal", "emerald", "amber" };

    private static string PickTone(string userId) =>
        _tones[Math.Abs(userId.GetHashCode()) % _tones.Length];

    private static string DetermineTimePeriod(string statusName) =>
        statusName.Equals("active", StringComparison.OrdinalIgnoreCase) ? "Active" : "Closed";

    private static string DetermineAchieved(double kpi1, double kpi2, string kpiUnit)
    {
        if (kpi1 < 0) return kpi2 >= 0 ? "Yes" : "No";
        return kpi2 >= kpi1 ? "Yes" : "No";
    }

    private static IReadOnlyList<KpiComment> ParseComment(KpiMsgRow row)
    {
        var comments = new List<KpiComment>();
        var parts = row.Msg.Split("<br>", StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            string authorName = "ICTA User", authorRole = "District Manager", body = trimmed;
            string createdAt = row.UpdateDate.ToString("yyyy-MM-ddTHH:mm:ss");

            if (trimmed.StartsWith('['))
            {
                var closeBracket = trimmed.IndexOf(']');
                if (closeBracket > 0)
                {
                    var meta = trimmed[1..closeBracket];
                    body = trimmed[(closeBracket + 1)..].Trim();

                    if (DateTime.TryParse(meta, out var parsedDate))
                        createdAt = parsedDate.ToString("yyyy-MM-ddTHH:mm:ss");
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
