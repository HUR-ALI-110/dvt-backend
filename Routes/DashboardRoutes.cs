using ICTA_DVT.Models;
using ICTA_DVT.Services;
using Serilog;

namespace ICTA_DVT.Routes;

public static class DashboardRoutes
{
    public static void MapDashboardRoutes(this WebApplication app)
    {
        // GET /dashboard?view=overview&dealer=329&scope=all&district=all-district&date=2024-01-01
        app.MapGet("/dashboard", (
            string? view,
            string? dealer,
            string? scope,
            string? district,
            string? date,
            IConfiguration config,
            DvtDbService db) =>
        {
            try
            {
                var resolvedView = view ?? "overview";
                var geo = DvtDbService.ResolveGeoParams(dealer, scope, district);

                var filters = new DashboardFilters(
                    Dealer: dealer ?? "all-dealer",
                    Scope: scope ?? "all",
                    District: district ?? "all-district",
                    Date: date ?? $"{DateTime.Now.Year}-01-01");

                var filterOptions = db.GetFilterOptions();
                var summary = db.GetDealerSummary(geo, date, config.GetValue<int>("ReportIds:ManagementOverview"));

                var shell = new DashboardShellData(
                    BrandLabel: "DVT",
                    ReportButtonLabel: "View Full Report",
                    PrimaryTabs: BuildPrimaryTabs(),
                    DashboardPages: BuildDashboardPages(),
                    Filters: filters,
                    FilterOptions: filterOptions,
                    Summary: summary);

                object page = resolvedView switch
                {
                    "service-parts" => db.GetServicePartsData(geo, date, config.GetValue<int>("ReportIds:PartsOverview")),
                    "ev-readiness" => db.GetEvReadinessData(geo, date, config.GetValue<int>("ReportIds:EvMobileSurvey")),
                    "sales" => db.GetSalesData(geo, date, config.GetValue<int>("ReportIds:SalesTotal")),
                    _ => db.GetOverviewData(geo, date, config.GetValue<int>("ReportIds:ManagementOverview"))
                };

                return Results.Ok(new DashboardPayload(resolvedView, shell, page));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Dashboard error for view={View} dealer={Dealer}", view, dealer);
                return Results.Problem("Failed to load dashboard data.");
            }
        })
        .WithName("GetDashboard")
        .WithSummary("Get dashboard payload for a given view and filter set")
        .WithTags("Dashboard");

        // PATCH /dashboard?view=overview&messageId=930
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
                    Id: messageId,
                    IconTone: "blue",
                    Title: "Note",
                    Body: body.Body,
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
        new ShellNavItem("overview",      "Overview",       "/",                "primary"),
        new ShellNavItem("tracking",      "Tracking",       "/tracking",        "primary"),
        new ShellNavItem("discussion",    "Discussion",     "/discussion",      "primary")
    };

    private static IReadOnlyList<ShellNavItem> BuildDashboardPages() => new[]
    {
        new ShellNavItem("overview",      "Overview",       "/",                "dashboard"),
        new ShellNavItem("sales",         "Sales",          "/sales",           "dashboard"),
        new ShellNavItem("service-parts", "Service & Parts","/service-parts",   "dashboard"),
        new ShellNavItem("ev-readiness",  "EV Readiness",   "/ev-readiness",    "dashboard")
    };
}
