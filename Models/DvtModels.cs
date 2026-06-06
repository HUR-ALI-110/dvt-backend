using System.Text.Json.Serialization;

namespace ICTA_DVT.Models;

// ── Shared ───────────────────────────────────────────────────────────────────

public record GeoParams(int GeoTypeId, string GeoValue);

// ── Dashboard – Shell ────────────────────────────────────────────────────────

public record FilterOption(string Value, string Label);

public record DashboardFilters(
    [property: JsonPropertyName("report_id")]
    string ReportId,
    [property: JsonPropertyName("report_title")]
    string ReportTitle,
    [property: JsonPropertyName("geo_type_id")]
    string GeoTypeId,
    [property: JsonPropertyName("geo_value")]
    string GeoValue,
    [property: JsonPropertyName("geo_title")]
    string GeoTitle,
    [property: JsonPropertyName("date_type_id")]
    string DateTypeId,
    [property: JsonPropertyName("time_title")]
    string TimeTitle);

public record ShellNavItem(string Id, string Label, string Href, string Kind);

public record Certification(string Label, string Status);

public record DealerSummary(
    string Eyebrow,
    string Title,
    string Subtitle,
    string DealerIdLabel,
    string Region,
    string SalesDistrict,
    string ServicePartsDistrict,
    IReadOnlyList<Certification> Certifications,
    bool CompactMeta = false);

public record DashboardFilterOptions(
    IReadOnlyList<FilterOption> Dealers,
    IReadOnlyList<FilterOption> Scopes,
    IReadOnlyList<FilterOption> Districts,
    IReadOnlyList<FilterOption> Dates);

public record DashboardShellData(
    DashboardFilterOptions FilterOptions);

// ── Dashboard – Overview page ────────────────────────────────────────────────

public record PersonRow(
    string Role,
    string Name,
    string? Secondary = null,
    string? AccentLabel = null,
    string? AccentValue = null,
    string? ExtraAccentLabel = null,
    string? ExtraAccentValue = null,
    string? Accent2Label = null,
    string? Accent2Value = null,
    string? DrillType = null);

public record OverviewPeopleSection(
    string Title,
    IReadOnlyList<PersonRow> Rows,
    IReadOnlyList<FooterLink>? FooterLinks = null);

public record FooterLink(string Label, string Value, string LinkType = "ichiban");

public record VerticalDetailRow(string Label, string Value);

public record OverviewTableRow(string Label, IReadOnlyList<string> Values);

public record OverviewTableCard(
    string Title,
    IReadOnlyList<string> Columns,
    IReadOnlyList<OverviewTableRow> Rows);

public record OverviewMetricValue(string Label, string Value, string Tone = "neutral");

public record OverviewMetricRow(string Label, IReadOnlyList<OverviewMetricValue> Values);

public record OverviewMetricCard(string Title, IReadOnlyList<OverviewMetricRow> Rows);

public record SalesConsultantRow(
    string Person,
    string CurrentLevel,
    string Pke,
    string TrainingWorkshop,
    string YtdSales,
    string YtdRetailSales,
    string YtdFleetSales,
    string YtdMegaSales,
    string Rank,
    string WaVideo);

public record OverviewDashboard(
    string LeftColumnTitle,
    string CenterColumnTitle,
    string RightColumnTitle,
    IReadOnlyList<OverviewPeopleSection> PeopleSections,
    IReadOnlyList<OverviewTableCard> PerformanceTables,
    IReadOnlyList<OverviewMetricCard> MetricCards,
    IReadOnlyList<SalesConsultantRow> SalesConsultants,
    IReadOnlyList<VerticalDetailRow> IchibanDetail,
    IReadOnlyList<VerticalDetailRow> CoeDetail,
    IReadOnlyList<VerticalDetailRow> SalesManagerDetail,
    IReadOnlyList<VerticalDetailRow> ServiceManagerDetail,
    IReadOnlyList<VerticalDetailRow> PartsManagerDetail);

// ── Dashboard – Service-Parts page ───────────────────────────────────────────

public record ChartLegendItem(string Label, string Color, string Kind);

public record MixedChartSeries(string Label, string Color, string Kind, IReadOnlyList<double> Values);

public record MixedChartCard(
    string Title,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> YAxisLabels,
    IReadOnlyList<MixedChartSeries> Series,
    IReadOnlyList<ChartLegendItem> Legend);

public record StatTile(string Label, string Value, string Tone);

public record MessagePanel(
    string Id,
    string IconTone,
    string Title,
    string Body,
    string? ActionLabel = null,
    bool? Editable = null);

public record ServicePartsDashboard(
    MixedChartCard Chart,
    IReadOnlyList<StatTile> PrimaryStats,
    IReadOnlyList<StatTile> SecondaryStats,
    IReadOnlyList<MessagePanel> Messages,
    ServicePartsDrillThrough DrillThrough);

// Service training drill: summary (% certified by category) + person list + per-person
// individual course rows. Clicking a person row opens the matching individual rows (nested).
public record ServiceTrainingDrill(
    [property: JsonPropertyName("summary")] DrillTable Summary,
    [property: JsonPropertyName("list")] DrillTable List,
    [property: JsonPropertyName("individuals")] DrillTable Individuals);

public record ServicePartsDrillThrough(
    IReadOnlyList<DrillTable> PartsPurchasing,
    DrillTable CsiOverall,
    DrillTable IrisNetUtilization,
    ServiceTrainingDrill Training,
    DrillTable CoOpUtilization,
    DrillTable Dppp,
    DrillTable FvPartsSales,
    DrillTable NationalSubmissions,
    DrillTable BackOrderTotal);

// ── Dashboard – EV Readiness page ────────────────────────────────────────────

public record EvCard(
    string Title,
    string Subtitle,
    double Completion,
    string State,
    string Icon,
    // Drill-through key; empty = not clickable.
    [property: JsonPropertyName("drillType")] string DrillType = "");

public record EvSection(
    string Title,
    string ProgressLabel,
    IReadOnlyList<EvCard> Cards);

// Step1 boxes 4/5 = per-course completion tables; Step2 instructor-led = three person
// lists (tech / service / parts). Clicking a name filters the shared Individuals rows
// (sp_rpt_dcn_ev_training_ind) by user_id — nested drill.
public record EvDrillThrough(
    DrillTable Box4,
    DrillTable Box5,
    DrillTable InstructorLedTech,
    DrillTable InstructorLedService,
    DrillTable InstructorLedParts,
    DrillTable Individuals);

public record EvReadinessDashboard(
    string ProgressLabel,
    string ProgressValue,
    string ProgressHelp,
    IReadOnlyList<EvSection> Sections,
    string FooterAlert,
    EvDrillThrough DrillThrough);

// ── Dashboard – Drill-through shared ─────────────────────────────────────────

public record DrillCol(
    [property: JsonPropertyName("key")]       string Key,
    [property: JsonPropertyName("col_label")] string Label,
    [property: JsonPropertyName("col_width")] string Width,
    [property: JsonPropertyName("align")]     string Align = "left");

public record DrillRow(
    [property: JsonPropertyName("rowContent")] Dictionary<string, string> Cells);

public record DrillTable(
    [property: JsonPropertyName("cols")] IReadOnlyList<DrillCol> Cols,
    [property: JsonPropertyName("rows")] IReadOnlyList<DrillRow> Rows);

// ── Dashboard – Sales page ───────────────────────────────────────────────────

public record MiniChartCard(
    string Title,
    IReadOnlyList<string> Labels,
    IReadOnlyList<double> Values,
    string Color);

public record SalesMetric(string Label, string Value);

public record SalesRow(MiniChartCard Chart, IReadOnlyList<SalesMetric> Metrics);

public record SalesDrillThrough(
    IReadOnlyList<DrillTable> TotalTruckSales,
    IReadOnlyList<DrillTable> NSeries,
    IReadOnlyList<DrillTable> FSeries,
    IReadOnlyList<DrillTable> Inventory,
    IReadOnlyList<DrillTable> Orders,
    DrillTable DemosPaid,
    DrillTable CoOpUtilization,
    DrillTable SoaMarketShare,
    DrillTable SalesTraining);

public record SalesDashboard(
    IReadOnlyList<SalesRow> Rows,
    IReadOnlyList<MessagePanel> Messages,
    SalesDrillThrough DrillThrough);

// ── Dashboard – full payload ─────────────────────────────────────────────────

public record DashboardPayload(string View, DashboardShellData Shell, object Page);

public record DashboardReportPayload(string View, object Page, DealerSummary Summary);

// ── Dashboard – PATCH request ────────────────────────────────────────────────

public record UpdateMessageRequest(string View, string Body);

// ── Tracking – Meetings ──────────────────────────────────────────────────────

public record CreatedBy(string Initials, string Name, string Tone);

public record DownloadAsset(string Label, string Href);

public record MeetingRecord(
    string Id,
    string DealerName,
    string SalesDistrict,
    string PartsDistrict,
    string Department,
    string ContactDate,
    CreatedBy CreatedBy,
    DownloadAsset Download,
    string MeetingComment);

public record MeetingsResponse(IReadOnlyList<MeetingRecord> Meetings);

public record UpdateMeetingNoteRequest(string Note);

public record UpdateMeetingNoteResponse(MeetingRecord Meeting);

// ── Tracking – KPIs ─────────────────────────────────────────────────────────

public record KpiComment(
    string Id,
    string AuthorName,
    string AuthorRole,
    string Body,
    string CreatedAt);

public record KpiRecord(
    string Id,
    string DealerName,
    string SalesDistrict,
    string PartsDistrict,
    string Department,
    string ActionItemDate,
    string TargetDate,
    string TimePeriod,
    string CentralKpi,
    double YtdKpiAtMeeting,
    double CurrentYtdKpi,
    string Achieved);

public record KpiDetailRecord(
    string Id,
    string DealerName,
    string SalesDistrict,
    string PartsDistrict,
    string Department,
    string ActionItemDate,
    string TargetDate,
    string TimePeriod,
    string CentralKpi,
    double YtdKpiAtMeeting,
    double CurrentYtdKpi,
    string Achieved,
    IReadOnlyList<KpiComment> Comments);

public record KpisResponse(IReadOnlyList<KpiRecord> Kpis);

public record KpiDetailResponse(KpiDetailRecord Kpi);

public record AddKpiCommentRequest(
    string Body,
    string AuthorName = "ICTA User",
    string AuthorRole = "District Manager");

public record AddKpiCommentResponse(KpiDetailRecord Kpi);

// ── Internal – SP result rows ─────────────────────────────────────────────────
// These match the column names returned by the stored procedures.

public record PersonnelRow(
    string? DealerCode,
    string? Role,
    string? Name,
    string? ListOrder,
    string? LocationName,
    string? Region,
    string? SalesDistrict,
    string? ServiceDistrict);

public record PackageRow(
    int PkgId,
    DateTime PkgDate,
    string InsertUserId,
    string LocationCode,
    string LocationName,
    string StatusName,
    string ReportPkg,
    string ContactPkg,
    string DeptName,
    string Regdst1,
    string Regdst2,
    string Items,
    string PkgNotes,
    // KPI columns (present when use_kpi > 0)
    string? KpiName = null,
    DateTime? TgtDate = null,
    double? Kpi1 = null,
    double? Kpi2 = null,
    string? ActionId = null,
    string? KpiId = null,
    string? Msg = null,
    int? KpiStatus = null,
    string? KpiUnit = null);

public record LocationRow(
    int LocationId,
    string LocationCode,
    string LocationName,
    string District1,
    string District2,
    string? Region);

public record KpiMsgRow(int Id, int PkgId, int ActionId, int KpiId, string Msg, DateTime UpdateDate);
