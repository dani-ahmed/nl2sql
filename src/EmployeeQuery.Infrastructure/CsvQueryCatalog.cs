using System.Globalization;
using System.Text;
using System.Text.Json;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

public sealed record CatalogOutcome(string Status, string QueryId, string Message);

public sealed class CsvQueryCatalog : IQueryCatalog
{
    private static readonly HashSet<string> Scalar = ["EMP-003", "EMP-008", "CERT-004", "BEN-009"];
    private static readonly HashSet<string> Grouped = ["EMP-010", "EMP-013", "CERT-007", "BEN-006", "BEN-007"];
    private static readonly HashSet<string> Top = ["EMP-004", "EMP-006", "EMP-011", "CERT-008", "CERT-013", "BEN-004", "BEN-005", "BEN-010"];
    private static readonly HashSet<string> EmployeeGrain =
    [
        "CERT-005", "CERT-008", "CERT-012", "BEN-002", "BEN-003", "BEN-004", "BEN-008",
        "XDOM-001", "XDOM-002", "XDOM-003", "XDOM-004", "XDOM-005",
    ];
    private static readonly HashSet<string> BenefitGrain = ["BEN-001", "BEN-005", "BEN-010", "BEN-011", "BEN-012"];

    private readonly Dictionary<string, QueryDefinition> _definitions;
    private readonly Dictionary<string, CatalogOutcome> _outcomes;

    public CsvQueryCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        (_definitions, _outcomes) = Load(path);
    }

    public IReadOnlyCollection<QueryDefinition> All => _definitions.Values;

    public bool TryGet(string queryId, out QueryDefinition definition) =>
        _definitions.TryGetValue(queryId, out definition!);

    public bool TryGetByQuestion(string question, out QueryDefinition definition)
    {
        if (_outcomes.TryGetValue(Normalize(question), out CatalogOutcome? outcome)
            && outcome.Status == "success")
        {
            return _definitions.TryGetValue(outcome.QueryId, out definition!);
        }

        definition = null!;
        return false;
    }

    public bool TryGetOutcome(string question, out CatalogOutcome outcome) =>
        _outcomes.TryGetValue(Normalize(question), out outcome!);

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static (Dictionary<string, QueryDefinition>, Dictionary<string, CatalogOutcome>) Load(string path)
    {
        using StreamReader reader = File.OpenText(path);
        List<string[]> rows = CsvReader.Read(reader).ToList();
        if (rows.Count < 2)
        {
            throw new InvalidDataException("The semantic query catalog is empty.");
        }

        Dictionary<string, int> headers = rows[0]
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        string Get(string[] row, string name) => row[headers[name]];

        Dictionary<string, QueryDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CatalogOutcome> outcomes = new(StringComparer.Ordinal);
        foreach (string[] row in rows.Skip(1))
        {
            if (row.Length != headers.Count)
            {
                throw new InvalidDataException("The semantic query catalog contains a malformed CSV row.");
            }

            string id = Get(row, "base_case_id");
            string question = Get(row, "natural_language_question");
            string status = Get(row, "expected_status");
            if (!outcomes.ContainsKey(Normalize(question)))
            {
                outcomes[Normalize(question)] = new CatalogOutcome(status, status == "success" ? id : string.Empty, Message(id, status));
            }

            if (status != "success" || definitions.ContainsKey(id))
            {
                continue;
            }

            string[] columns = JsonSerializer.Deserialize<string[]>(Get(row, "expected_columns_json"))
                ?? throw new InvalidDataException($"Catalog entry {id} has invalid columns.");
            ResultGrain grain = GetGrain(id);
            definitions[id] = new QueryDefinition(
                id,
                Get(row, "category"),
                question,
                GetFamily(id),
                grain,
                Get(row, "canonical_sql"),
                columns,
                bool.Parse(Get(row, "order_sensitive")),
                Summary(id, grain));
        }

        if (definitions.Count != 43 || outcomes.Count != 65)
        {
            throw new InvalidDataException($"Expected 43 query capabilities and 65 outcomes; loaded {definitions.Count} and {outcomes.Count}.");
        }

        return (definitions, outcomes);
    }

    private static PlanFamily GetFamily(string id) =>
        Scalar.Contains(id) ? PlanFamily.ScalarAggregate
        : Grouped.Contains(id) ? PlanFamily.GroupedAggregate
        : Top.Contains(id) ? PlanFamily.TopRecord
        : PlanFamily.RecordList;

    private static ResultGrain GetGrain(string id)
    {
        if (Scalar.Contains(id) || Grouped.Contains(id))
        {
            return ResultGrain.Summary;
        }

        if (id.StartsWith("EMP-", StringComparison.Ordinal) || id.StartsWith("XDOM-", StringComparison.Ordinal) || EmployeeGrain.Contains(id))
        {
            return ResultGrain.Employee;
        }

        if (BenefitGrain.Contains(id))
        {
            return ResultGrain.Benefit;
        }

        return id.StartsWith("BEN-", StringComparison.Ordinal) ? ResultGrain.Summary : ResultGrain.Certification;
    }

    private static string Summary(string id, ResultGrain grain) =>
        Scalar.Contains(id) ? "Calculated the requested aggregate for the authorized department."
        : Grouped.Contains(id) ? "Returned {count} aggregate group(s)."
        : Top.Contains(id) ? "Returned {count} top-ranked record(s), including ties where required."
        : $"Returned {{count}} {grain.ToString().ToLower(CultureInfo.InvariantCulture)} record(s).";

    private static string Message(string id, string status)
    {
        if (status == "refused")
        {
            return "That request is outside the authorized read-only employee-data scope.";
        }

        return id switch
        {
            "AMB-001" => "Do you mean the highest balance on one benefits record, or the highest total after summing each employee's records?",
            "AMB-002" => "Should employees with a missing bonus be excluded, or should a missing bonus be treated as zero?",
            "AMB-003" => "What start date or time period should define recently?",
            "AMB-004" => "How many employees should I return, and does earnings mean base salary or salary plus bonus?",
            "AMB-005" => "Should employees without certifications also be included?",
            "AMB-006" => "Should I count benefits records, distinct packages, covered employees, or sum remaining balances?",
            "ERR-002" => "What would you like to know about employees, certifications, or benefits?",
            "ERR-003" => "Could you rephrase that as a question about employees, certifications, or benefits?",
            "ERR-004" => "The database has no forecasting rules. Would you like current salary information instead?",
            _ => "Could you clarify the requested employee-data query?",
        };
    }

    private static class CsvReader
    {
        public static IEnumerable<string[]> Read(TextReader reader)
        {
            List<string> row = [];
            StringBuilder field = new();
            bool quoted = false;
            while (true)
            {
                int current = reader.Read();
                if (current < 0)
                {
                    if (quoted)
                    {
                        throw new InvalidDataException("Unterminated quoted CSV field.");
                    }

                    if (field.Length > 0 || row.Count > 0)
                    {
                        row.Add(field.ToString());
                        yield return row.ToArray();
                    }

                    yield break;
                }

                char value = (char)current;
                if (quoted)
                {
                    if (value == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            _ = reader.Read();
                            field.Append('"');
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        field.Append(value);
                    }

                    continue;
                }

                switch (value)
                {
                    case '"' when field.Length == 0:
                        quoted = true;
                        break;
                    case ',':
                        row.Add(field.ToString());
                        field.Clear();
                        break;
                    case '\r':
                        if (reader.Peek() == '\n')
                        {
                            _ = reader.Read();
                        }

                        row.Add(field.ToString());
                        yield return row.ToArray();
                        row = [];
                        field.Clear();
                        break;
                    case '\n':
                        row.Add(field.ToString());
                        yield return row.ToArray();
                        row = [];
                        field.Clear();
                        break;
                    default:
                        field.Append(value);
                        break;
                }
            }
        }
    }
}
