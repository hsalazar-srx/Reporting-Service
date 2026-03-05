using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using Reporting.Api.Middleware;
using Reporting.Core.Catalog;
using Reporting.Infrastructure.Catalog;
using Serilog;

// Bootstrap logger — captures startup failures before Serilog is configured from appsettings
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Logging ──────────────────────────────────────────────────────────────────
    // Reads from "Serilog" config section; enriches with CorrelationId for request tracing
    builder.Host.UseSerilog((context, services, configuration) =>
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.WithCorrelationId());

    // ── API Key options ───────────────────────────────────────────────────────────
    // Bound from "ApiKeys" section; actual values from User Secrets (dev) / Azure Key Vault (prod)
    builder.Services.Configure<ApiKeyOptions>(
        builder.Configuration.GetSection("ApiKeys"));

    // ── Report catalog ────────────────────────────────────────────────────────────
    // Singleton with background reload and last-known-good fallback (Gap 6 mitigation)
    builder.Services.AddSingleton<JsonReportCatalogProvider>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<JsonReportCatalogProvider>>();
        var env = sp.GetRequiredService<IHostEnvironment>();

        var catalogRelativePath = builder.Configuration["Catalog:Path"]
            ?? "config/report-catalog.json";

        var catalogPath = Path.IsPathRooted(catalogRelativePath)
            ? catalogRelativePath
            : Path.GetFullPath(catalogRelativePath, env.ContentRootPath);

        var reloadInterval = TimeSpan.FromSeconds(
            builder.Configuration.GetValue<int>("Catalog:ReloadIntervalSeconds", 300));

        return new JsonReportCatalogProvider(catalogPath, reloadInterval, logger);
    });
    builder.Services.AddSingleton<IReportCatalogProvider>(
        sp => sp.GetRequiredService<JsonReportCatalogProvider>());

    // ── MVC controllers ───────────────────────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    // ── Swagger / OpenAPI — WR-4 ──────────────────────────────────────────────────
    // Disabled in production via Swagger:EnableUI=false in appsettings.json
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "SRX Reporting Service API",
            Version = "v1",
            Description = "REST API for report catalog browsing and report execution. " +
                          "Replaces Crystal Reports across 6 manufacturing reporting domains."
        });

        options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
        {
            Name = "X-API-Key",
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Description = "Primary API key. Required on all endpoints except /api/v1/health."
        });

        options.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "ApiKey"
                    }
                },
                Array.Empty<string>()
            }
        });

        var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
            options.IncludeXmlComments(xmlPath);
    });

    // ── Rate limiting — Gap 1 mitigation ─────────────────────────────────────────
    // Fixed window: 60 req/min per IP. Health controller is exempt via [DisableRateLimiting].
    builder.Services.AddRateLimiter(options =>
    {
        options.AddFixedWindowLimiter("api", opt =>
        {
            opt.PermitLimit = builder.Configuration.GetValue<int>("RateLimiting:PermitLimit", 60);
            opt.Window = TimeSpan.FromSeconds(
                builder.Configuration.GetValue<int>("RateLimiting:WindowSeconds", 60));
            opt.QueueLimit = builder.Configuration.GetValue<int>("RateLimiting:QueueLimit", 0);
            opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        });

        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = async (context, cancellationToken) =>
        {
            context.HttpContext.Response.ContentType = "application/json";
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                code = "RATE_LIMIT_EXCEEDED",
                message = "Too many requests. Please retry after 60 seconds.",
                correlationId = context.HttpContext.TraceIdentifier,
                timestamp = DateTime.UtcNow
            }, cancellationToken);
        };
    });

    // ── Build ─────────────────────────────────────────────────────────────────────
    var app = builder.Build();

    // ── Startup: load catalog (fail fast if missing or invalid at startup) ────────
    var catalogProvider = app.Services.GetRequiredService<JsonReportCatalogProvider>();
    await catalogProvider.LoadAsync();

    Log.Information("Report catalog loaded. Starting SRX Reporting Service");

    // ── Middleware pipeline ───────────────────────────────────────────────────────
    app.UseSerilogRequestLogging();

    if (app.Configuration.GetValue<bool>("Swagger:EnableUI"))
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "SRX Reporting Service v1"));
    }

    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.UseMiddleware<ApiKeyMiddleware>();

    app.MapControllers().RequireRateLimiting("api");

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "SRX Reporting Service terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
