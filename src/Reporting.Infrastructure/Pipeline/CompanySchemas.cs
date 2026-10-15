using System.Text.RegularExpressions;

namespace Reporting.Infrastructure.Pipeline;

/// <summary>
/// Resolves a MOVEX company to its DB2 schema. The schema is per company
/// (CONO 100 = MVXCDTA, CONO 300 = MVXC300), from DataSources:Db2:CompanySchemas; the default
/// company is per environment (DataSources:Db2:DefaultCompany), never a catalog value.
///
/// Schemas and company codes end up in SQL text, so both are validated here: schemas must be
/// plain identifiers and company codes digits only.
/// </summary>
public sealed class CompanySchemas
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex Digits = new("^[0-9]+$", RegexOptions.Compiled);

    private readonly IReadOnlyDictionary<string, string> _schemas;

    public string DefaultCompany { get; }
    public string DefaultSchema { get; }

    public CompanySchemas(IReadOnlyDictionary<string, string> schemas, string defaultCompany)
    {
        foreach (var (company, schema) in schemas)
        {
            if (!SafeIdentifier.IsMatch(schema))
                throw new InvalidOperationException(
                    $"DB2 schema '{schema}' for company {company} is not a plain identifier.");
            if (!Digits.IsMatch(company))
                throw new InvalidOperationException($"Company code '{company}' must be digits only.");
        }

        _schemas = schemas;
        DefaultCompany = defaultCompany.Trim();

        if (!_schemas.TryGetValue(DefaultCompany, out var defaultSchema))
            throw new InvalidOperationException(
                $"Default company {defaultCompany} has no DB2 schema configured (DataSources:Db2:CompanySchemas).");
        DefaultSchema = defaultSchema;
    }

    /// <summary>Blank → the environment's default company; otherwise the requested one.</summary>
    public (string Company, string Schema) Resolve(string? requestedCompany)
    {
        var company = string.IsNullOrWhiteSpace(requestedCompany) ? DefaultCompany : requestedCompany.Trim();
        if (!_schemas.TryGetValue(company, out var schema))
            throw new ArgumentException(
                $"Company {company} has no DB2 schema configured (DataSources:Db2:CompanySchemas).");
        return (company, schema);
    }
}
