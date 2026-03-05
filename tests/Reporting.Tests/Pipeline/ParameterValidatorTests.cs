using FluentAssertions;
using Reporting.Core.Catalog;
using Reporting.Infrastructure.Pipeline;

namespace Reporting.Tests.Pipeline;

public sealed class ParameterValidatorTests
{
    private readonly ParameterValidator _validator = new();

    // ── Happy paths ────────────────────────────────────────────────────────

    [Fact]
    public void Validate_AllRequiredParamsPresent_ReturnsNoErrors()
    {
        var report = BuildReport(
            Param("startDate", "date", required: true),
            Param("endDate", "date", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["startDate"] = "2026-01-01", ["endDate"] = "2026-03-31" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_OptionalParamOmitted_ReturnsNoErrors()
    {
        var report = BuildReport(Param("notes", "string", required: false));

        var errors = _validator.Validate(report, new Dictionary<string, string?>());

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_NoParamsInReport_ReturnsNoErrors()
    {
        var report = BuildReport();

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["extra"] = "ignored" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ParamWithDefaultOmitted_ReturnsNoErrors()
    {
        var report = BuildReport(ParamWithDefault("format", "string", defaultValue: "json"));

        var errors = _validator.Validate(report, new Dictionary<string, string?>());

        errors.Should().BeEmpty();
    }

    // ── Required parameter validation ─────────────────────────────────────

    [Fact]
    public void Validate_MissingRequiredParam_ReturnsError()
    {
        var report = BuildReport(Param("startDate", "date", required: true));

        var errors = _validator.Validate(report, new Dictionary<string, string?>());

        errors.Should().ContainSingle()
              .Which.Should().Contain("startDate");
    }

    [Fact]
    public void Validate_RequiredParamIsWhitespace_ReturnsError()
    {
        var report = BuildReport(Param("startDate", "date", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["startDate"] = "   " });

        errors.Should().ContainSingle();
    }

    [Fact]
    public void Validate_RequiredParamIsNull_ReturnsError()
    {
        var report = BuildReport(Param("startDate", "date", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["startDate"] = null });

        errors.Should().ContainSingle();
    }

    // ── Type validation ────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidDateIso_ReturnsNoErrors()
    {
        var report = BuildReport(Param("dt", "date", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["dt"] = "2026-01-15" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidDate_ReturnsError()
    {
        var report = BuildReport(Param("dt", "date", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["dt"] = "not-a-date" });

        errors.Should().ContainSingle()
              .Which.Should().Contain("dt");
    }

    [Fact]
    public void Validate_ValidInteger_ReturnsNoErrors()
    {
        var report = BuildReport(Param("maxRows", "integer", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["maxRows"] = "500" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidInteger_ReturnsError()
    {
        var report = BuildReport(Param("maxRows", "integer", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["maxRows"] = "abc" });

        errors.Should().ContainSingle()
              .Which.Should().Contain("maxRows");
    }

    [Fact]
    public void Validate_ValidDecimal_ReturnsNoErrors()
    {
        var report = BuildReport(Param("threshold", "decimal", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["threshold"] = "3.14" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_InvalidDecimal_ReturnsError()
    {
        var report = BuildReport(Param("threshold", "decimal", required: true));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["threshold"] = "not-a-number" });

        errors.Should().ContainSingle();
    }

    // ── Allowed-value validation ───────────────────────────────────────────

    [Fact]
    public void Validate_ValueInAllowedList_ReturnsNoErrors()
    {
        var report = BuildReport(ParamWithAllowed("format", "string", "json", "excel", "pdf"));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["format"] = "excel" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_AllowedValuesCaseInsensitive_ReturnsNoErrors()
    {
        var report = BuildReport(ParamWithAllowed("format", "string", "json", "excel"));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["format"] = "JSON" });

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ValueNotInAllowedList_ReturnsError()
    {
        var report = BuildReport(ParamWithAllowed("format", "string", "json", "excel"));

        var errors = _validator.Validate(report,
            new Dictionary<string, string?> { ["format"] = "xml" });

        errors.Should().ContainSingle()
              .Which.Should().Contain("format").And.Contain("xml");
    }

    [Fact]
    public void Validate_MultipleErrors_ReturnsAllErrors()
    {
        var report = BuildReport(
            Param("startDate", "date", required: true),
            Param("count", "integer", required: true));

        var errors = _validator.Validate(report, new Dictionary<string, string?>
        {
            ["startDate"] = "bad-date",
            ["count"] = "not-int"
        });

        errors.Should().HaveCount(2);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ReportDefinition BuildReport(params ReportParameter[] parameters) =>
        new()
        {
            Id = "test.report",
            DisplayName = "Test Report",
            Domain = "Test",
            DataSource = "Db2Direct",
            Parameters = parameters
        };

    private static ReportParameter Param(string name, string type, bool required) =>
        new()
        {
            Name = name,
            DisplayName = name,
            Type = type,
            Required = required
        };

    private static ReportParameter ParamWithDefault(string name, string type, string defaultValue) =>
        new()
        {
            Name = name,
            DisplayName = name,
            Type = type,
            Required = true,
            DefaultValue = defaultValue
        };

    private static ReportParameter ParamWithAllowed(string name, string type, params string[] allowedValues) =>
        new()
        {
            Name = name,
            DisplayName = name,
            Type = type,
            Required = true,
            AllowedValues = allowedValues
        };
}
