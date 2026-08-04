namespace EmployeeQuery.Application;

public enum ApplicationEventName
{
    ApplicationStartup,
    DepartmentSelected,
    ScopedDatabaseInitialization,
    CopyVerification,
    PlannerRequest,
    PlannerRetry,
    SemanticRepair,
    Clarification,
    UnsupportedRequest,
    Compilation,
    Execution,
    ResultValidation,
    GuardrailFailure,
    Shutdown,
}

public sealed record ApplicationLogEvent(
    DateTimeOffset Timestamp,
    ApplicationEventName Event,
    string? SessionId,
    string? RequestId,
    IReadOnlyDictionary<string, object?> Properties);

public interface IApplicationLogger
{
    void Log(ApplicationLogEvent entry);
}

public sealed class NullApplicationLogger : IApplicationLogger
{
    public static NullApplicationLogger Instance { get; } = new();

    public void Log(ApplicationLogEvent entry) => _ = entry;
}
