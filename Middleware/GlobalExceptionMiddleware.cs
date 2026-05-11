using System.Net;
using System.Text.Json;
using Serilog;

namespace ICTA_DVT.Middleware;

public sealed class GlobalExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Unhandled exception at {Path}", context.Request.Path);
            await HandleExceptionAsync(context, ex);
        }
    }

    private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;

        var response = new
        {
            error = "INTERNAL_SERVER_ERROR",
            message = "An unexpected error occurred. Please try again later.",
#if DEBUG
            details = exception.Message,
            stackTrace = exception.StackTrace
#endif
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response));
    }
}
