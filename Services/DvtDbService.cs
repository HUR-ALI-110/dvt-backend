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
                BuildWinnersCircleSection(personnelRows, ichibanRows),
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
                Array.Empty<StatTile>(), Array.Empty<StatTile>(), Array.Empty<MessagePanel>());

        var xmlParms      = BuildOverviewXmlParms(filters);
        var pvoRows       = TryExecute("sp_rpt_dcn_parts_pvo",          xmlParms);
        var fvTotalRows   = TryExecute("sp_rpt_dcn_fv_vs_total_parts",  xmlParms);
        var irisRows      = TryExecute("sp_rpt_dcn_iris_util_parts",    xmlParms);
        var trainingRows  = TryExecute("sp_rpt_dcn_service_training",   xmlParms);
        var coopRows      = TryExecute("sp_rpt_dcn_service_co_op",      xmlParms);
        var csiRows       = TryExecute("sp_rpt_dcn_csi_overview",       xmlParms);
        var incentiveRows = TryExecuteWithRptLvl("sp_rpt_dcn_parts_incentives", xmlParms, 1);
        var dpppRows      = TryExecuteWithRptLvl("sp_rpt_dcn_parts_dppp",       xmlParms, 1);
        var backorderRows = TryExecute("sp_rpt_dcn_backorder_service",  xmlParms);
        var execMsgRows   = TryExecute("sp_rpt_dcn_exec_message",       xmlParms);
        var locMsgRows    = TryExecute("sp_rpt_dcn_loc_message",        xmlParms);
        var regnMsgRows   = TryExecute("sp_rpt_dcn_regn_message",       xmlParms);

        return new ServicePartsDashboard(
            Chart:          BuildPartsChart(pvoRows),
            PrimaryStats:   BuildServicePrimaryStats(csiRows, irisRows, trainingRows, coopRows),
            SecondaryStats: BuildServiceSecondaryStats(dpppRows, fvTotalRows, incentiveRows, backorderRows),
            Messages:       BuildServiceMessages(execMsgRows, locMsgRows, regnMsgRows));
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

    // ── EV Readiness page data ───────────────────────────────────────────────

    public EvReadinessDashboard GetEvReadinessData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new EvReadinessDashboard("EV Readiness", "0%", string.Empty, Array.Empty<EvSection>(), string.Empty);

        var xmlParms   = BuildOverviewXmlParms(filters);
        var ciRows     = TryExecute("sp_rpt_dcn_ci_overview", xmlParms);
        var surveyRows = TryExecuteEvSurvey(xmlParms);

        return BuildEvReadinessDashboard(ciRows, surveyRows);
    }

    // sp_rpt_dcn_ev_mobile_survey requires @rpt_level=5 and @survey_id as extra SQL params (not in XML)
    private List<Dictionary<string, object?>> TryExecuteEvSurvey(string xmlParms)
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
            cmd.Parameters.AddWithValue("@rpt_level", 5);
            cmd.Parameters.Add("@survey_id", SqlDbType.Int).Value = DBNull.Value;

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

    private static EvReadinessDashboard BuildEvReadinessDashboard(
        List<Dictionary<string, object?>> ciRows,
        List<Dictionary<string, object?>> surveyRows)
    {
        if (surveyRows.Count == 0)
            return new EvReadinessDashboard("EV Readiness", "0%", string.Empty, Array.Empty<EvSection>(), string.Empty);

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
            TrainingCard("High Voltage Vehicle Awareness Service Technician Training",
                "HV awareness training for service technicians",
                Dbl(r, "lvl_1_2_techs_training")),
            TrainingCard("High Voltage Vehicle Awareness Svc Mgr/Svc Adv & Part Mgr/Part Cntr Training",
                "HV awareness training for service & parts management",
                Dbl(r, "svc_parts_training")),
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
                Dbl(r, "tech_training")),
            TrainingCard("Instructor-led & Computer-based Svc Mgr/Svc Adv Training",
                "EV training for service managers and advisors",
                Dbl(r, "service_training")),
            TrainingCard("Instructor-led & Computer-based Part Mgr/Part Cntr Training",
                "EV training for parts managers and counter staff",
                Dbl(r, "parts_training")),
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
            FooterAlert: "Complete all steps to achieve EV certification. Contact your District Manager for assistance.");
    }

    // ── Sales page data ──────────────────────────────────────────────────────

    public SalesDashboard GetSalesData(DashboardFilters filters)
    {
        if (string.IsNullOrEmpty(filters.GeoValue))
            return new SalesDashboard(Array.Empty<SalesRow>(), Array.Empty<MessagePanel>());

        var xmlParms          = BuildOverviewXmlParms(filters);
        var salesTotalRows    = TryExecute("sp_rpt_dcn_sales_total",       xmlParms);
        var inventoryRows     = TryExecute("sp_rpt_dcn_inven_type_sales",  xmlParms);
        var vehicleInfoRows   = TryExecute("sp_rpt_dcn_vehicleinfo_Sales", xmlParms);
        var coopTruckRows     = TryExecute("sp_rpt_dcn_coop_trucks",       xmlParms);
        var demosRows         = TryExecute("sp_rpt_dcn_Demos_overview",    xmlParms);
        var trainingRows      = TryExecute("sp_rpt_dcn_sale_training",     xmlParms);
        var indShrRows        = TryExecute("sp_rpt_dcn_ind_shr",           xmlParms);
        var execMsgRows       = TryExecute("sp_rpt_dcn_exec_message",      xmlParms);
        var locMsgRows        = TryExecute("sp_rpt_dcn_loc_message",       xmlParms);
        var regnMsgRows       = TryExecute("sp_rpt_dcn_regn_message",      xmlParms);

        return new SalesDashboard(
            Rows:     BuildSalesChartRows(salesTotalRows, inventoryRows, vehicleInfoRows, coopTruckRows, demosRows, trainingRows, indShrRows),
            Messages: BuildSalesMessages(execMsgRows, locMsgRows, regnMsgRows));
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
