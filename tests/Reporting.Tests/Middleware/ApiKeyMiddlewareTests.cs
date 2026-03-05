using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Reporting.Api.Middleware;

namespace Reporting.Tests.Middleware;

public sealed class ApiKeyMiddlewareTests
{
    private const string PrimaryKey = "test-primary-key-abc123";
    private const string AdminKey = "test-admin-key-xyz789";

    private static ApiKeyMiddleware CreateMiddleware(
        RequestDelegate next,
        string? primaryKey = PrimaryKey,
        string? adminKey = AdminKey,
        bool isDevelopment = false)
    {
        var options = new ApiKeyOptions { Primary = primaryKey, Admin = adminKey };
        var monitor = new Mock<IOptionsMonitor<ApiKeyOptions>>();
        monitor.Setup(m => m.CurrentValue).Returns(options);

        var env = new Mock<IHostEnvironment>();
        env.Setup(e => e.EnvironmentName)
           .Returns(isDevelopment ? Environments.Development : Environments.Production);

        return new ApiKeyMiddleware(next, monitor.Object,
            NullLogger<ApiKeyMiddleware>.Instance, env.Object);
    }

    private static DefaultHttpContext CreateContext(
        string path,
        string? apiKey = null,
        string? adminKey = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (apiKey is not null)
            context.Request.Headers["X-API-Key"] = apiKey;
        if (adminKey is not null)
            context.Request.Headers["X-Admin-Key"] = adminKey;
        // Provide a writable body so WriteAsJsonAsync doesn't throw
        context.Response.Body = new MemoryStream();
        return context;
    }

    // ── Primary key ────────────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ValidPrimaryKey_CallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/api/v1/reports", apiKey: PrimaryKey));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_InvalidPrimaryKey_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/v1/reports", apiKey: "wrong-key");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task InvokeAsync_MissingApiKey_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/v1/reports");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task InvokeAsync_EmptyApiKey_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        var context = CreateContext("/api/v1/reports", apiKey: "");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }

    // ── Admin key ──────────────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_ValidAdminKey_CallsNext()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/api/v1/reports", adminKey: AdminKey));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_InvalidAdminKey_FallsThroughToPrimaryCheck()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask);
        // Wrong admin key but no primary key either → should 401
        var context = CreateContext("/api/v1/reports", adminKey: "bad-admin");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }

    // ── Bypass paths ───────────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_HealthPath_BypassesAuthWithNoKey()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/api/v1/health"));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_HealthSubPath_BypassesAuth()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(CreateContext("/api/v1/health/data-sources"));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_SwaggerPath_Development_BypassesAuth()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            isDevelopment: true);

        await middleware.InvokeAsync(CreateContext("/swagger"));

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_SwaggerPath_Production_Returns401()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, isDevelopment: false);
        var context = CreateContext("/swagger");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }

    // ── Misconfiguration ──────────────────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_PrimaryKeyNull_Returns503()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, primaryKey: null);
        var context = CreateContext("/api/v1/reports", apiKey: "any-key");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task InvokeAsync_PrimaryKeyEmpty_Returns503()
    {
        var middleware = CreateMiddleware(_ => Task.CompletedTask, primaryKey: "");
        var context = CreateContext("/api/v1/reports", apiKey: "any-key");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(503);
    }

    // ── Timing-safe comparison (Gap 2) ────────────────────────────────────

    [Fact]
    public async Task InvokeAsync_KeyDiffersByOneChar_Returns401()
    {
        // Ensures wrong key of same length is rejected (not bypassed by padding logic)
        var middleware = CreateMiddleware(_ => Task.CompletedTask,
            primaryKey: "aaaaaaaaaaaaaaaa");
        var context = CreateContext("/api/v1/reports", apiKey: "aaaaaaaaaaaaaaab");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task InvokeAsync_ShorterKeyThatIsPrefix_Returns401()
    {
        // Ensures a shorter key that is a prefix of the configured key is rejected
        var middleware = CreateMiddleware(_ => Task.CompletedTask,
            primaryKey: "correct-key-longer");
        var context = CreateContext("/api/v1/reports", apiKey: "correct-key");

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(401);
    }
}
