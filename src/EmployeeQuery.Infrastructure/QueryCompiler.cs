using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

public sealed class QueryCompiler(IQueryCatalog catalog) : IQueryCompiler
{
    private static readonly Regex Forbidden = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|ATTACH|DETACH|PRAGMA|VACUUM|REINDEX)\b|;\s*\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public CompiledQuery Compile(QueryPlan plan, ApplicationSession session)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(session);
        if (plan.Semantics is not null)
        {
            return new SemanticSqlCompiler().Compile(plan, session);
        }

        if (!catalog.TryGet(plan.QueryId, out QueryDefinition? definition)
            || definition.Family != plan.Family
            || definition.Grain != plan.Grain)
        {
            throw new InvalidOperationException("The semantic query plan is not a valid catalog capability.");
        }

        string sql = definition.Sql.Trim();
        if (!(sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
            || Forbidden.IsMatch(sql))
        {
            throw new InvalidOperationException("The compiler catalog contains a non-read-only statement.");
        }

        if (!Regex.IsMatch(sql, @"\bDepartment\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !sql.Contains(":department", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The compiler refused a query without the mandatory department predicate.");
        }

        int? appliedLimit = ExtractLimit(sql);
        if (plan.Family is PlanFamily.RecordList or PlanFamily.TopRecord && appliedLimit is null)
        {
            // The supplied acceptance SQL returns at most a few dozen rows today, but
            // the runtime contract must remain bounded if the source data grows.
            // Add the trusted hard cap without changing the reviewed query semantics.
            sql = sql.TrimEnd().TrimEnd(';') + " LIMIT :limit";
            appliedLimit = SemanticQueryValidator.MaximumRows;
        }

        Dictionary<string, object?> parameters = new(StringComparer.Ordinal)
        {
            ["department"] = session.AuthorizedDepartment.ToString(),
        };
        if (sql.Contains(":limit", StringComparison.Ordinal))
        {
            parameters["limit"] = appliedLimit;
        }

        return new CompiledQuery(
            sql,
            new ReadOnlyDictionary<string, object?>(parameters),
            definition.Columns,
            definition.Grain,
            new QueryPolicyProof(session.AuthorizedDepartment, true, true, "catalog-compiler/1.0"),
            $"trusted-{definition.Family.ToString().ToLowerInvariant()}-{definition.Id.ToLowerInvariant()}",
            appliedLimit,
            ResultDescriptors.Create(plan, definition.Columns, appliedLimit is not null));
    }

    private static int? ExtractLimit(string sql)
    {
        Match match = Regex.Match(sql, @"\bLIMIT\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
