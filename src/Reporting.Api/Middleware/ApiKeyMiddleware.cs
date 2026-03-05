using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Reporting.Api.Middleware;

/// <summary>
/// Validates the X-API-Key header for all requests except /api/v1/health and (dev-only) /swagger.
/// Two-tier: Primary key for normal clients; Admin key for operations access.
///
/// Security improvements over movex-rest-api template (see ai/memory/06-known-risks-and-pitfalls.md):
/// - Gap 2: Uses CryptographicOperations.FixedTimeEquals (prevents timing attacks)
/// - Gap 1: Rate limiting (60 req/min per IP) configured in Program.cs via AddRateLimiter
///
/// Uses skill: architecture/dotnet-api-design v1.0
/// </summary>
public sealed class ApiKeyMiddleware
{
    private const string ApiKeyHeader = "X-API-Key";
    private const string AdminKeyHeader = "X-Admin-Key";

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private readonly IOptionsMonitor<ApiKeyOptions> _optionsMonitor;
    private readonly IHostEnvironment _environment;

    public ApiKeyMiddleware(
        RequestDelegate next,
        IOptionsMonitor<ApiKeyOptions> optionsMonitor,
        ILogger<ApiKeyMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Health endpoint requires no authentication — allows load balancer probes
        if (context.Request.Path.StartsWithSegments("/api/v1/health"))
        {
            await _next(context);
            return;
        }

        // Swagger UI allowed without key in Development only (WR-4: disabled in production)
        if (_environment.IsDevelopment() && context.Request.Path.StartsWithSegments("/swagger"))
        {
            await _next(context);
            return;
        }

        var options = _optionsMonitor.CurrentValue;

        // Admin key check (X-Admin-Key header grants admin access to all routes)
        if (context.Request.Headers.TryGetValue(AdminKeyHeader, out var adminHeaderValue) &&
            !string.IsNullOrWhiteSpace(options.Admin) &&
            IsKeyValid(adminHeaderValue.ToString(), options.Admin))
        {
            _logger.LogInformation("Admin key access granted for {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await _next(context);
            return;
        }

        // Primary key not configured — service misconfigured
        if (string.IsNullOrWhiteSpace(options.Primary))
        {
            _logger.LogWarning("API key (Primary) not configured; denying request to {Path}",
                context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "UNAUTHORIZED",
                message = "Service is not configured to accept requests.",
                correlationId = context.TraceIdentifier,
                timestamp = DateTime.UtcNow
            });
            return;
        }

        // Primary key validation
        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey) ||
            !IsKeyValid(providedKey.ToString(), options.Primary))
        {
            _logger.LogWarning("Invalid or missing API key for {Method} {Path}",
                context.Request.Method, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "UNAUTHORIZED",
                message = "Invalid or missing API key.",
                correlationId = context.TraceIdentifier,
                timestamp = DateTime.UtcNow
            });
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// Gap 2 fix: Constant-time key comparison using CryptographicOperations.FixedTimeEquals
    /// to prevent timing attacks that could reveal the key via response time measurement.
    /// </summary>
    private static bool IsKeyValid(string provided, string configured)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(configured))
            return false;

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);

        // Pad to equal length before comparison (required by FixedTimeEquals)
        if (providedBytes.Length != configuredBytes.Length)
        {
            // Still compare to maintain constant time — result will be false
            var maxLen = Math.Max(providedBytes.Length, configuredBytes.Length);
            var paddedProvided = new byte[maxLen];
            var paddedConfigured = new byte[maxLen];
            providedBytes.CopyTo(paddedProvided, 0);
            configuredBytes.CopyTo(paddedConfigured, 0);
            CryptographicOperations.FixedTimeEquals(paddedProvided, paddedConfigured);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }
}

/// <summary>
/// API key configuration options. Bound from appsettings.json "ApiKeys" section.
/// Actual values come from User Secrets (dev) or Azure Key Vault (prod).
/// </summary>
public sealed class ApiKeyOptions
{
    /// <summary>Primary API key — used by SM-Portal backend.</summary>
    public string? Primary { get; set; }

    /// <summary>Admin key — elevated access for operations/monitoring tools.</summary>
    public string? Admin { get; set; }
}
