using System.Text.Json;

namespace ICTA_DVT.Middleware;

public sealed class RequestParameterLoggingMiddleware(RequestDelegate next, ILogger<RequestParameterLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/dashboard", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/meetings", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/kpis", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = new Dictionary<string, object?>();

            foreach (var rv in context.Request.RouteValues)
                parameters[$"route.{rv.Key}"] = rv.Value?.ToString();

            foreach (var qv in context.Request.Query)
                parameters[$"query.{qv.Key}"] = qv.Value.ToString();

            logger.LogInformation(
                "API call {Method} {Path} with parameters {Parameters}",
                context.Request.Method,
                context.Request.Path.Value,
                JsonSerializer.Serialize(parameters));
        }

        await next(context);
    }
}
