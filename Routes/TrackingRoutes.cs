using ICTA_DVT.Models;
using ICTA_DVT.Services;
using Serilog;

namespace ICTA_DVT.Routes;

public static class TrackingRoutes
{
    public static void MapTrackingRoutes(this WebApplication app)
    {
        // GET /meetings?dealer=329&scope=all&district=all-district&date1=2024-01-01&date2=2024-12-31&deptId=1
        app.MapGet("/meetings", (
            string? dealer,
            string? scope,
            string? district,
            string? date1,
            string? date2,
            int? deptId,
            IConfiguration config,
            DvtDbService db) =>
        {
            try
            {
                var geo = DvtDbService.ResolveGeoParams(dealer, scope, district);
                var reportId = config.GetValue<int>("ReportIds:Package");
                var meetings = db.GetMeetings(geo, date1, date2, deptId, reportId);
                return Results.Ok(new MeetingsResponse(meetings));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load meetings");
                return Results.Problem("Failed to load meetings.");
            }
        })
        .WithName("GetMeetings")
        .WithSummary("Get dealer visit packages as meeting records")
        .WithTags("Tracking");

        // PATCH /meetings/{meetingId}/note
        app.MapMethods("/meetings/{meetingId}/note", new[] { "PATCH" }, (
            string meetingId,
            UpdateMeetingNoteRequest body,
            IConfiguration config,
            DvtDbService db) =>
        {
            try
            {
                if (!int.TryParse(meetingId, out var pkgId))
                    return Results.BadRequest(new { error = "Invalid meetingId." });

                db.SaveMeetingNote(pkgId, body.Note);

                // Reload the meeting to return updated record
                var geo = new GeoParams(4, "0");
                var reportId = config.GetValue<int>("ReportIds:Package");
                var meetings = db.GetMeetings(geo, null, null, null, reportId);
                var updated = meetings.FirstOrDefault(m => m.Id == meetingId);

                if (updated is null)
                    return Results.NotFound();

                return Results.Ok(new UpdateMeetingNoteResponse(updated));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save meeting note for pkg {MeetingId}", meetingId);
                return Results.Problem("Failed to save meeting note.");
            }
        })
        .WithName("UpdateMeetingNote")
        .WithSummary("Append a note to a dealer visit package")
        .WithTags("Tracking");

        // GET /kpis?dealer=329&scope=all&district=all-district&date1=2024-01-01&date2=2024-12-31&deptId=1
        app.MapGet("/kpis", (
            string? dealer,
            string? scope,
            string? district,
            string? date1,
            string? date2,
            int? deptId,
            IConfiguration config,
            DvtDbService db) =>
        {
            try
            {
                var geo = DvtDbService.ResolveGeoParams(dealer, scope, district);
                var reportId = config.GetValue<int>("ReportIds:PackageKpi");
                var kpis = db.GetKpis(geo, date1, date2, deptId, reportId);
                return Results.Ok(new KpisResponse(kpis));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load KPIs");
                return Results.Problem("Failed to load KPIs.");
            }
        })
        .WithName("GetKpis")
        .WithSummary("Get KPI action items from dealer visit packages")
        .WithTags("Tracking");

        // GET /kpis/{kpiId}?pkgId=930
        app.MapGet("/kpis/{kpiId}", (
            string kpiId,
            string? pkgId,
            DvtDbService db) =>
        {
            try
            {
                if (string.IsNullOrEmpty(pkgId))
                    return Results.BadRequest(new { error = "pkgId query param is required." });

                var detail = db.GetKpiDetail(kpiId, pkgId);
                if (detail is null)
                    return Results.NotFound();

                return Results.Ok(new KpiDetailResponse(detail));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load KPI detail for kpi={KpiId} pkg={PkgId}", kpiId, pkgId);
                return Results.Problem("Failed to load KPI detail.");
            }
        })
        .WithName("GetKpiDetail")
        .WithSummary("Get KPI detail with comment history")
        .WithTags("Tracking");

        // PATCH /kpis/{kpiId}/comments?pkgId=930&actionId=5
        app.MapMethods("/kpis/{kpiId}/comments", new[] { "PATCH" }, (
            string kpiId,
            string? pkgId,
            string? actionId,
            AddKpiCommentRequest body,
            DvtDbService db) =>
        {
            try
            {
                if (!int.TryParse(kpiId, out var kpiInt))
                    return Results.BadRequest(new { error = "Invalid kpiId." });
                if (!int.TryParse(pkgId, out var pkgInt))
                    return Results.BadRequest(new { error = "pkgId query param is required and must be an integer." });
                if (!int.TryParse(actionId, out var actionInt))
                    return Results.BadRequest(new { error = "actionId query param is required and must be an integer." });

                db.AddKpiComment(pkgInt, actionInt, kpiInt, body.Body, body.AuthorName, body.AuthorRole);

                var detail = db.GetKpiDetail(kpiId, pkgId!);
                if (detail is null)
                    return Results.NotFound();

                return Results.Ok(new AddKpiCommentResponse(detail));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to add KPI comment for kpi={KpiId}", kpiId);
                return Results.Problem("Failed to add KPI comment.");
            }
        })
        .WithName("AddKpiComment")
        .WithSummary("Append a comment to a KPI action item")
        .WithTags("Tracking");
    }
}
