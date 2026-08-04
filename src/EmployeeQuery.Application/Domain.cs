using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmployeeQuery.Application;

public enum Department
{
    Sales,
    Marketing,
    Engineering,
}

[JsonConverter(typeof(AuthorizedDepartmentJsonConverter))]
public readonly record struct AuthorizedDepartment
{
    public AuthorizedDepartment(Department value) => Value = value;

    public Department Value { get; }

    public override string ToString() => Value.ToString();

    public static bool TryParse(string? value, out AuthorizedDepartment department)
    {
        if (Enum.TryParse(value, ignoreCase: true, out Department parsed) && Enum.IsDefined(parsed))
        {
            department = new AuthorizedDepartment(parsed);
            return true;
        }

        department = default;
        return false;
    }
}

public sealed class AuthorizedDepartmentJsonConverter : JsonConverter<AuthorizedDepartment>
{
    public override AuthorizedDepartment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.GetString();
        if (!AuthorizedDepartment.TryParse(value, out AuthorizedDepartment department))
        {
            throw new JsonException($"Unknown department '{value}'.");
        }

        return department;
    }

    public override void Write(Utf8JsonWriter writer, AuthorizedDepartment value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed record ApplicationSession(
    Guid SessionId,
    AuthorizedDepartment AuthorizedDepartment,
    DateTimeOffset StartedAt);

public enum PlanFamily
{
    RecordList,
    ScalarAggregate,
    GroupedAggregate,
    TopRecord,
}

public enum ResultGrain
{
    Employee,
    Certification,
    Benefit,
    Summary,
}

public abstract record QueryPlan(string QueryId, ResultGrain Grain, SemanticQuerySpec? Semantics = null)
{
    public abstract PlanFamily Family { get; }

    public static QueryPlan FromDefinition(QueryDefinition definition) => definition.Family switch
    {
        PlanFamily.RecordList => new RecordListPlan(definition.Id, definition.Grain),
        PlanFamily.ScalarAggregate => new ScalarAggregatePlan(definition.Id),
        PlanFamily.GroupedAggregate => new GroupedAggregatePlan(definition.Id),
        PlanFamily.TopRecord => new TopRecordPlan(definition.Id, definition.Grain),
        _ => throw new ArgumentOutOfRangeException(nameof(definition)),
    };
}

public sealed record RecordListPlan(string Id, ResultGrain ResultGrain, SemanticQuerySpec? Query = null) : QueryPlan(Id, ResultGrain, Query)
{
    public override PlanFamily Family => PlanFamily.RecordList;
}

public sealed record ScalarAggregatePlan(string Id, SemanticQuerySpec? Query = null) : QueryPlan(Id, ResultGrain.Summary, Query)
{
    public override PlanFamily Family => PlanFamily.ScalarAggregate;
}

public sealed record GroupedAggregatePlan(string Id, SemanticQuerySpec? Query = null) : QueryPlan(Id, ResultGrain.Summary, Query)
{
    public override PlanFamily Family => PlanFamily.GroupedAggregate;
}

public sealed record TopRecordPlan(string Id, ResultGrain ResultGrain, SemanticQuerySpec? Query = null) : QueryPlan(Id, ResultGrain, Query)
{
    public override PlanFamily Family => PlanFamily.TopRecord;
}

public sealed record QueryDefinition(
    string Id,
    string Category,
    string Question,
    PlanFamily Family,
    ResultGrain Grain,
    string Sql,
    IReadOnlyList<string> Columns,
    bool OrderSensitive,
    string Summary);

public abstract record PlannerOutcome
{
    private PlannerOutcome()
    {
    }

    public sealed record Ready(
        QueryPlan Plan,
        string Planner,
        string? PromptVersion = null,
        string? PromptFingerprint = null,
        string? Model = null,
        string? ReasoningEffort = null) : PlannerOutcome;

    public sealed record Clarification(string Message, string? Planner = null) : PlannerOutcome;

    public sealed record Unsupported(string Message, string? Planner = null) : PlannerOutcome;
}

public class QueryPlannerUnavailableException(string message, string category) : Exception(message)
{
    public string Category { get; } = category;
}

public sealed record QueryPolicyProof(
    AuthorizedDepartment Department,
    bool DepartmentPredicateApplied,
    bool ReadOnly,
    string CompilerVersion);

public enum ResultValueKind
{
    Text,
    WholeNumber,
    Number,
    Money,
    Date,
}

public enum ResultSummaryStrategy
{
    RecordList,
    ScalarAggregate,
    GroupedAggregate,
    RankedRecords,
}

public sealed record ResultColumnDescriptor(
    string Name,
    string Label,
    ResultValueKind ValueKind,
    bool Hidden = false);

public sealed record ResultDescriptor(
    ResultGrain Grain,
    IReadOnlyList<ResultColumnDescriptor> Columns,
    ResultSummaryStrategy SummaryStrategy,
    bool CanBeTruncated,
    bool IsRanked,
    bool IsGrouped);

public sealed record CompiledQuery(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> Columns,
    ResultGrain Grain,
    QueryPolicyProof PolicyProof,
    string Strategy,
    int? AppliedLimit,
    ResultDescriptor? Descriptor = null);

public sealed record ExecutionResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    TimeSpan Duration);

public sealed record QueryResponse(
    string Status,
    AuthorizedDepartment Department,
    string? Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    string Message,
    string? QueryId,
    string? Planner,
    string? Strategy,
    long DurationMilliseconds,
    string? PromptVersion = null,
    string? PromptFingerprint = null,
    string? Model = null,
    string? ReasoningEffort = null,
    string? RequestId = null,
    ResultDescriptor? Descriptor = null,
    QueryPlan? Plan = null)
{
    public static QueryResponse Clarification(AuthorizedDepartment department, string message) =>
        Empty("clarification", department, message);

    public static QueryResponse Refused(AuthorizedDepartment department, string message) =>
        Empty("refused", department, message);

    public static QueryResponse Failure(AuthorizedDepartment department, string message) =>
        Empty("error", department, message);

    private static QueryResponse Empty(string status, AuthorizedDepartment department, string message) =>
        new(
            status,
            department,
            null,
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>()),
            Array.Empty<string>(),
            Array.Empty<IReadOnlyList<object?>>(),
            message,
            null,
            null,
            null,
            0);
}

public interface IDepartmentSelector
{
    AuthorizedDepartment SelectDepartment();
}

public interface IQueryCatalog
{
    IReadOnlyCollection<QueryDefinition> All { get; }

    bool TryGet(string queryId, out QueryDefinition definition);

    bool TryGetByQuestion(string question, out QueryDefinition definition);
}

public interface IQueryPlanner
{
    Task<PlannerOutcome> PlanAsync(string question, CancellationToken cancellationToken);
}

public interface IContextualQueryPlanner : IQueryPlanner
{
    Task<PlannerOutcome> PlanAsync(PlannerRequest request, CancellationToken cancellationToken);
}

public interface IQueryCompiler
{
    CompiledQuery Compile(QueryPlan plan, ApplicationSession session);
}

public interface IQueryExecutor
{
    Task<ExecutionResult> ExecuteAsync(
        CompiledQuery query,
        ApplicationSession session,
        CancellationToken cancellationToken);
}

public interface IScopedDatabaseSession : IAsyncDisposable
{
    AuthorizedDepartment Department { get; }

    IReadOnlySet<long> AuthorizedEmployeeIds { get; }

    bool SourceConnectionClosed { get; }
}
