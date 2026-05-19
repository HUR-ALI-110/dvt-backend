using ICTA_DVT.Models;
using ICTA_DVT.Services;
using Serilog;

namespace ICTA_DVT.Routes;

public static class DashboardRoutes
{
    public static void MapDashboardRoutes(this WebApplication app)
    {
        // GET /dashboard — shell only (nav, dropdowns, filter options)
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
                var shell = new DashboardShellData(
                    FilterOptions:     db.GetFilterOptions());

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

                return Results.Ok(new DashboardReportPayload(resolvedView, page, db.GetDealerSummary(filters)));
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

        // PATCH /dashboard?view=sales&messageId=3719
        app.MapMethods("/dashboard", new[] { "PATCH" }, (
            string? view,
            string? messageId,
            UpdateMessageRequest body,
            DvtDbService db) =>
        {
            try
            {
                if (string.IsNullOrEmpty(messageId) || !int.TryParse(messageId, out var id))
                    return Results.BadRequest(new { error = "Invalid or missing messageId." });

                MessagePanel updated;

                if (view == "sales")
                {
                    db.UpdateLocationMessage(id, body.Body);
                    updated = new MessagePanel(
                        Id:          messageId,
                        IconTone:    "green",
                        Title:       "DSM Comments",
                        Body:        body.Body,
                        ActionLabel: "Update DSM Comment",
                        Editable:    true);
                }
                else if (view == "service-parts")
                {
                    db.UpdateLocationMessage(id, body.Body);
                    updated = new MessagePanel(
                        Id:          messageId,
                        IconTone:    "green",
                        Title:       "DSPM Comments",
                        Body:        body.Body,
                        ActionLabel: "Update DSPM Comment",
                        Editable:    true);
                }
                else
                {
                    db.UpdateMessage(id, body.Body);
                    updated = new MessagePanel(
                        Id:       messageId,
                        IconTone: "blue",
                        Title:    "Note",
                        Body:     body.Body,
                        Editable: true);
                }

                return Results.Ok(new { message = updated });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to update message for {View} id={MessageId}", view, messageId);
                return Results.Problem("Failed to update message.");
            }
        })
        .WithName("UpdateDashboardMessage")
        .WithSummary("Update a message panel body (package notes)")
        .WithTags("Dashboard");
    }
}
