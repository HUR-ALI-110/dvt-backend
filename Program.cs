using ICTA_DVT.Middleware;
using ICTA_DVT.Routes;
using ICTA_DVT.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Paths / folders ──────────────────────────────────────────────────────────
var webRootPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot");
var logDirectory = Path.Combine(webRootPath, "Log");
Directory.CreateDirectory(logDirectory);

// ── Serilog ──────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logDirectory, "api-.log"),
        rollingInterval: RollingInterval.Month,
        retainedFileCountLimit: 24,
        shared: true)
    .CreateLogger();

builder.Host.UseSerilog();

// ── Services ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<DvtDbService>();
builder.Services.AddSingleton<UserSecurityService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── CORS ─────────────────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173", "http://127.0.0.1:5173" };

builder.Services.AddCors(o => o.AddPolicy("dvt", p =>
{
    if (allowedOrigins.Contains("*"))
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    else
        p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Environment.WebRootPath))
    app.Environment.WebRootPath = webRootPath;

app.UseSerilogRequestLogging();
app.UseCors("dvt");
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<RequestParameterLoggingMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();
app.UseStaticFiles();

// ── Health ───────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }))
    .WithTags("Health");

// ── Routes ───────────────────────────────────────────────────────────────────
app.MapAuthRoutes();
app.MapDashboardRoutes();
app.MapTrackingRoutes();

app.Run();
