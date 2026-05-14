using ICTA_DVT.Models;
using ICTA_DVT.Services;
using Serilog;

namespace ICTA_DVT.Routes;

public static class DashboardRoutes
{
    public static void MapDashboardRoutes(this WebApplication app)
    {
        // GET /dashboard — shell only (nav, dropdowns, dealer summary)
        // Called on every page load; does NOT run the heavy report SPs.
        app.MapGet("/dashboard", (
            string? view,
            string? report_id,
            string? report_title,
            string? geo_type_id,
            string? geo_value,
            string? geo_title,
            string? date_type_id,
            string? time_title,
            DvtDbService db) =>
        {
            try
            {
                var resolvedView = view ?? "overview";

                var filters = new DashboardFilters(
                    ReportId:    report_id    ?? "",
                    ReportTitle: report_title ?? resolvedView,
                    GeoTypeId:   geo_type_id  ?? "1",
                    GeoValue:    geo_value    ?? "",
                    GeoTitle:    geo_title    ?? "",
                    DateTypeId:  date_type_id ?? "11",
                    TimeTitle:   time_title   ?? "");

                var shell = new DashboardShellData(
                    BrandLabel:        "DVT",
                    ReportButtonLabel: "Get Report",
                    PrimaryTabs:       BuildPrimaryTabs(),
                    DashboardPages:    BuildDashboardPages(),
                    Filters:           filters,
                    FilterOptions:     db.GetFilterOptions(),
                    Summary:           db.GetDealerSummary(filters));

                return Results.Ok(shell);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Dashboard shell error");
                return Results.Problem("Failed to load dashboard shell.");
            }
        })
        .WithName("GetDashboardShell")
        .WithSummary("Get dashboard shell: nav tabs, filter options, and dealer summary")
        .WithTags("Dashboard");

        // GET /dashboard/report — page data only (runs the heavy report SPs)
        // Called when the user selects a dealer and clicks "Get Report".
        app.MapGet("/dashboard/report", (
            string? view,
            string? report_id,
            string? report_title,
            string? geo_type_id,
            string? geo_value,
            string? geo_title,
            string? date_type_id,
            string? time_title,
            DvtDbService db) =>
        {
            try
            {
                var resolvedView = view ?? "overview";

                var filters = new DashboardFilters(
                    ReportId:    report_id    ?? "",
                    ReportTitle: report_title ?? resolvedView,
                    GeoTypeId:   geo_type_id  ?? "1",
                    GeoValue:    geo_value    ?? "",
                    GeoTitle:    geo_title    ?? "",
                    DateTypeId:  date_type_id ?? "11",
                    TimeTitle:   time_title   ?? "");

                object page = resolvedView switch
                {
                    "service-parts" => db.GetServicePartsData(filters),
                    "ev-readiness"  => db.GetEvReadinessData(filters),
                    "sales"         => db.GetSalesData(filters),
                    _               => db.GetOverviewData(filters)
                };

                return Results.Ok(new DashboardReportPayload(resolvedView, page));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Dashboard report error for view={View}", view);
                return Results.Problem("Failed to load dashboard report.");
            }
        })
        .WithName("GetDashboardReport")
        .WithSummary("Get dashboard page data for a given view and filter set")
        .WithTags("Dashboard");

        // PATCH /dashboard?messageId=930
        app.MapMethods("/dashboard", new[] { "PATCH" }, (
            string? view,
            string? messageId,
            UpdateMessageRequest body,
            DvtDbService db) =>
        {
            try
            {
                if (string.IsNullOrEmpty(messageId) || !int.TryParse(messageId, out var pkgId))
                    return Results.BadRequest(new { error = "Invalid or missing messageId." });

                db.UpdateMessage(pkgId, body.Body);

                var updated = new MessagePanel(
                    Id:       messageId,
                    IconTone: "blue",
                    Title:    "Note",
                    Body:     body.Body,
                    Editable: true);

                return Results.Ok(new { message = updated });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to update message for pkg {MessageId}", messageId);
                return Results.Problem("Failed to update message.");
            }
        })
        .WithName("UpdateDashboardMessage")
        .WithSummary("Update a message panel body (package notes)")
        .WithTags("Dashboard");
    }

    // ── Static nav config ────────────────────────────────────────────────────

    private static IReadOnlyList<ShellNavItem> BuildPrimaryTabs() => new[]
    {
        new ShellNavItem("overview",   "Overview",   "/",           "primary"),
        new ShellNavItem("tracking",   "Tracking",   "/tracking",   "primary"),
        new ShellNavItem("discussion", "Discussion", "/discussion", "primary")
    };

    private static IReadOnlyList<ShellNavItem> BuildDashboardPages() => new[]
    {
        new ShellNavItem("overview",      "Overview",        "/",             "dashboard"),
        new ShellNavItem("sales",         "Sales",           "/sales",        "dashboard"),
        new ShellNavItem("service-parts", "Service & Parts", "/service-parts","dashboard"),
        new ShellNavItem("ev-readiness",  "EV Readiness",    "/ev-readiness", "dashboard")
    };
}
