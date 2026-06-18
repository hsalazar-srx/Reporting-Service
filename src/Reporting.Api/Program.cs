using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using Reporting.Api.Middleware;
using Reporting.Core.Catalog;
using Reporting.Core.Pipeline;
using Reporting.Infrastructure.Catalog;
using Reporting.Infrastructure.Domains.CostManagement;
using Reporting.Infrastructure.ExchangeRate;
using Reporting.Infrastructure.Pipeline;
using Reporting.Infrastructure.Renderers;
using ExcelRenderer = Reporting.Infrastructure.Renderers.ExcelRenderer;
using PdfRenderer = Reporting.Infrastructure.Renderers.PdfRenderer;
using Serilog;

// Bootstrap logger — captures startup failures before Serilog is configured from appsettings
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Server secrets file (Production) ─────────────────────────────────────────
    // Secrets are stored in a protected JSON file OUTSIDE the deployment folder.
    // Path is configurable via REPORTING_SECRETS_PATH env var (set in web.config).
    // Default: C:\ProgramData\SRX\Reporting\secrets.json
    //
    // Security: NTFS ACLs grant Read to "IIS AppPool\ReportingService" only.
    // The file is never in the deployment folder — survives app redeployments.
    //
    // Upgrade path: Replace this block with AddAzureKeyVault() when KV is provisioned (ADR-006).
    //
    // Setup: Run scripts\Setup-ServerSecrets.ps1 on SRXWEBAPP1 to create the file and ACLs.
    var secretsFilePath = builder.Configuration["REPORTING_SECRETS_PATH"]
        ?? @"C:\ProgramData\SRX\Reporting\secrets.json";

    if (File.Exists(secretsFilePath))
    {
        builder.Configuration.AddJsonFile(secretsFilePath, optional: false, reloadOnChange: false);
        Log.Information("Loaded secrets from protected file: {Path}", secretsFilePath);
    }
    else if (!builder.Environment.IsDevelopment())
    {
        Log.Warning(
            "Secrets file not found at {Path}. " +
            "API keys and connection strings may be missing. " +
            "Run scripts\\Setup-ServerSecrets.ps1 on this server.",
            secretsFilePath);
    }

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

    // ── Exchange Rate Sync ────────────────────────────────────────────────────────
    // Fetches daily SPOT rates from RBA and writes to mvxcdta.CCURRA.
    // Provides GET /api/v1/exchange-rates/{currency}/{date} via ExchangeRateController.
    builder.Services.Configure<ExchangeRateSyncOptions>(
        builder.Configuration.GetSection(ExchangeRateSyncOptions.SectionName));
    builder.Services.Configure<Db2ExchangeRateOptions>(
        builder.Configuration.GetSection(Db2ExchangeRateOptions.SectionName));

    builder.Services.AddSingleton<RbaApiClient>();
    builder.Services.AddTransient<Db2ExchangeRateWriter>();
    builder.Services.AddScoped<Db2ExchangeRateReader>();

    builder.Services.AddSingleton<ExchangeRateSyncService>(sp =>
        new ExchangeRateSyncService(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<ExchangeRateSyncOptions>>(),
            sp.GetRequiredService<RbaApiClient>(),
            () => sp.GetRequiredService<Db2ExchangeRateWriter>(),
            sp.GetRequiredService<ILogger<ExchangeRateSyncService>>()));

    // ── Report pipeline ───────────────────────────────────────────────────────────
    // Data fetchers — one per report ID
    var db2ConnStr = builder.Configuration["DataSources:Db2:ConnectionString"] ?? string.Empty;
    var db2Schema = builder.Configuration["DataSources:Db2:Schema"] ?? "mvxcdta";
    var db2Timeout = builder.Configuration.GetValue<int>("DataSources:Db2:DefaultTimeoutSeconds", 300);

    builder.Services.AddSingleton<IDataFetcher>(sp =>
        new AverageCostFetcher(db2ConnStr, db2Schema, db2Timeout,
            sp.GetRequiredService<ILogger<AverageCostFetcher>>()));
    builder.Services.AddSingleton<IDataFetcher>(sp =>
        new WacHistoryFetcher(db2ConnStr, db2Schema, db2Timeout,
            sp.GetRequiredService<ILogger<WacHistoryFetcher>>()));
    builder.Services.AddSingleton<IDataFetcher>(sp =>
        new CostVarianceFetcher(db2ConnStr, db2Schema, db2Timeout,
            sp.GetRequiredService<ILogger<CostVarianceFetcher>>()));

    // Transformers
    builder.Services.AddSingleton<ITransformer, CostManagementTransformer>();

    // Renderers
    builder.Services.AddSingleton<IRenderer, JsonRenderer>();
    builder.Services.AddSingleton<IRenderer, ExcelRenderer>();
    builder.Services.AddSingleton<IRenderer, PdfRenderer>();

    // Pipeline orchestrator
    builder.Services.AddSingleton<IParameterValidator, ParameterValidator>();
    builder.Services.AddSingleton<ReportPipelineService>();

    // ── MVC controllers ───────────────────────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    // ── Swagger / OpenAPI — WR-4 ──────────────────────────────────────────────────
    // Disabled in production via Swagger:EnableUI=false in appsettings.json
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Scanfil APAC Reporting Service API",
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

    // ── Startup: start exchange rate sync (background, non-blocking on failure) ──
    // NOTE (B6): IIS app pool idleTimeout MUST be set to 0 on SRXWEBAPP1 app pool,
    // otherwise the pool will be recycled before the 23:00 UTC timer fires.
    // Command: appcmd set apppool /apppool.name:"ReportingService" /processModel.idleTimeout:00:00:00
    var exchangeRateSync = app.Services.GetRequiredService<ExchangeRateSyncService>();
    try
    {
        await exchangeRateSync.StartAsync();
        Log.Information("Exchange rate sync service started");
    }
    catch (Exception ex)
    {
        // Non-fatal: log and continue — reporting API still works without exchange rates
        Log.Warning(ex, "Exchange rate sync startup failed — service will retry on next scheduled run");
    }

    // ── Middleware pipeline ───────────────────────────────────────────────────────
    app.UseSerilogRequestLogging();

    if (app.Configuration.GetValue<bool>("Swagger:EnableUI"))
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Scanfil APAC Reporting Service v1"));
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
