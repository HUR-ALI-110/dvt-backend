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
                Array.Empty<OverviewMetricCard>());

        var xmlParms = BuildOverviewXmlParms(filters);

        // sp_rpt_dcn_personel drives both people sections (dealer personnel + winner's circle)
        var personnelRows = TryExecute("sp_rpt_dcn_personel", xmlParms);
        var ichibanRows = TryExecute("sp_rpt_dcn_ichiban_overview", xmlParms);
        var vehicleRows = TryExecute("sp_rpt_dcn_vehicleinfo_overview", xmlParms);
        var coopRows = TryExecute("sp_rpt_dcn_coop_trucks", xmlParms);
        var objRows = TryExecute("sp_rpt_dcn_truck_sales_objectives", xmlParms);
        var partSalesRows = TryExecute("sp_rpt_dcn_partsales_overview", xmlParms);
        var csiRows = TryExecute("sp_rpt_dcn_csi_overview", xmlParms);
        var partCoopRows = TryExecute("sp_rpt_dcn_parts_coop_overview", xmlParms);
        var irisRows = TryExecute("sp_rpt_dcn_iris_overview", xmlParms);

        return new OverviewDashboard(
            LeftColumnTitle: "Personnel",
            CenterColumnTitle: "Performance",
            RightColumnTitle: "Metrics",
            PeopleSections: new[]
            {
                BuildDealerPersonnelSection(personnelRows),
                BuildWinnersCircleSection(personnelRows, ichibanRows),
            },
            PerformanceTables: new[]
            {
                BuildVehicleInfoTable(vehicleRows),
                BuildCoopTrucksTable(coopRows),
                BuildTruckSalesObjectivesTable(objRows),
            },
            MetricCards: new[]
            {
                BuildPartSalesMetricCard(partSalesRows),
                BuildCsiMetricCard(csiRows),
                BuildPartsCoopMetricCard(partCoopRows),
                BuildIrisMetricCard(irisRows),
            });
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
        List<Dictionary<string, object?>> ichibanRows)
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
                    ExtraAccentValue: ev);
            })
            .Where(pr => !string.IsNullOrEmpty(pr.Name))
            .ToList();

        var footerLinks = ichibanRows
            .Select(r => new FooterLink(Str(r, "location_name"), Str(r, "qualified")))
            .Where(fl => !string.IsNullOrEmpty(fl.Label))
            .ToList();

        return new OverviewPeopleSection(
            "Winner's Circle",
            personRows,
            footerLinks.Count > 0 ? footerLinks : null);
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
                .Sum(r => { double.TryParse(Str(r, "units"), out var v); return v; })
                .ToString("0")
        ).ToList();
        tableRows.Add(new OverviewTableRow("Total", totals));

        return new OverviewTableCard("Truck Sales", columns, tableRows);
    }

    private static OverviewTableCard BuildCoopTrucksTable(List<Dictionary<string, object?>> rows)
    {
        var columns = new[] { "Period", "Total Reward", "Used", "Remaining", "Ordered" };
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
            new[] { Str(r, "sales"), Str(r, "objective"), Str(r, "pct_achieved") }
        )).ToList();
        return new OverviewTableCard("Truck Sales Objectives", columns, tableRows);
    }

    private static OverviewMetricCard BuildPartSalesMetricCard(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewMetricCard("Dealer Parts Purchasing", Array.Empty<OverviewMetricRow>());
        var r = rows[0];
        var metricRows = new List<OverviewMetricRow>
        {
            SingleMetricRow("Captive", Str(r, "Captive")),
            SingleMetricRow("Competitive", Str(r, "Competitive")),
            SingleMetricRow("FV", Str(r, "FV")),
            SingleMetricRow("Total", Str(r, "Total")),
            SingleMetricRow("Objective", Str(r, "Objective")),
            SingleMetricRow("Retail Total", Str(r, "retail_total")),
        }.Where(row => row.Values.Any(v => !string.IsNullOrEmpty(v.Value))).ToList();
        return new OverviewMetricCard("Dealer Parts Purchasing", metricRows);
    }

    private static OverviewMetricCard BuildCsiMetricCard(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewMetricCard("CSI", Array.Empty<OverviewMetricRow>());
        var r = rows[0];
        return new OverviewMetricCard("CSI", new[]
        {
            SingleMetricRow("Total Surveys", Str(r, "total_survey")),
            DualMetricRow("Customer Treatment", Str(r, "csi_cst_trt"), Str(r, "csi_cst_trt_nat")),
            DualMetricRow("Repair Experience", Str(r, "csi_rpr_exp"), Str(r, "csi_rpr_exp_nat")),
            DualMetricRow("Scheduling", Str(r, "csi_sch_tim"), Str(r, "csi_sch_tim_nat")),
            DualMetricRow("Documentation", Str(r, "csi_doc_chg"), Str(r, "csi_doc_chg_nat")),
            DualMetricRow("Overall", Str(r, "csi_overall"), Str(r, "csi_overall_nat")),
        });
    }

    private static OverviewMetricCard BuildPartsCoopMetricCard(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewMetricCard("Service & Parts Co-Op", Array.Empty<OverviewMetricRow>());
        var r = rows[0];
        return new OverviewMetricCard("Service & Parts Co-Op", new[]
        {
            SingleMetricRow("Total Reward", Str(r, "total_reward")),
            SingleMetricRow("Reward Used", Str(r, "reward_used")),
            SingleMetricRow("Remaining", Str(r, "reward_remaining")),
            SingleMetricRow("Ordered", Str(r, "ordered")),
        });
    }

    private static OverviewMetricCard BuildIrisMetricCard(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return new OverviewMetricCard("IRIS Performance", Array.Empty<OverviewMetricRow>());
        var r = rows[0];
        return new OverviewMetricCard("IRIS Performance", new[]
        {
            DualMetricRow("YTD Net Utilization", Str(r, "ytd_net_utilization"), Str(r, "National_Avg")),
            SingleMetricRow("1st Half", Str(r, "1st_half_net_utilization")),
            SingleMetricRow("2nd Half", Str(r, "2nd_half_net_utilization")),
        });
    }

    private static OverviewMetricRow SingleMetricRow(string label, string value) =>
        new(label, new[] { new OverviewMetricValue("Value", value) });

    private static OverviewMetricRow DualMetricRow(string label, string dealerVal, string natVal) =>
        new(label, new[]
        {
            new OverviewMetricValue("Dealer", dealerVal),
            new OverviewMetricValue("National", natVal),
        });

    // ── Service-Parts page data ──────────────────────────────────────────────

    public ServicePartsDashboard GetServicePartsData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new ServicePartsDashboard(
                new MixedChartCard("Parts Sales Overview", Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<MixedChartSeries>(), Array.Empty<ChartLegendItem>()),
                Array.Empty<StatTile>(), Array.Empty<StatTile>(), Array.Empty<MessagePanel>());

        var xmlParms = BuildOverviewXmlParms(filters);
        TryExecute("sp_rpt_dcn_partsales_overview", xmlParms);

        var emptyChart = new MixedChartCard(
            "Parts Sales Overview",
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<MixedChartSeries>(),
            Array.Empty<ChartLegendItem>());

        return new ServicePartsDashboard(emptyChart, Array.Empty<StatTile>(), Array.Empty<StatTile>(),
            Array.Empty<MessagePanel>());
    }

    // ── EV Readiness page data ───────────────────────────────────────────────

    public EvReadinessDashboard GetEvReadinessData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new EvReadinessDashboard("EV Readiness", "0%", string.Empty, Array.Empty<EvSection>(), string.Empty);


        var xmlParms = BuildOverviewXmlParms(filters);
        TryExecute("sp_rpt_dcn_ev_mobile_survey", xmlParms);

        return new EvReadinessDashboard(
            ProgressLabel: "EV Readiness",
            ProgressValue: "0%",
            ProgressHelp: string.Empty,
            Sections: Array.Empty<EvSection>(),
            FooterAlert: string.Empty);
    }

    // ── Sales page data ──────────────────────────────────────────────────────

    public SalesDashboard GetSalesData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new SalesDashboard(Array.Empty<SalesRow>(), Array.Empty<MessagePanel>());


        var xmlParms = BuildOverviewXmlParms(filters);
        TryExecute("sp_rpt_dcn_sales_total", xmlParms);
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
