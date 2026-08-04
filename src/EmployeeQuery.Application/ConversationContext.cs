namespace EmployeeQuery.Application;

public sealed record PlannerRequest(
    string Question,
    string? PreviousSuccessfulQuestion = null,
    QueryPlan? PreviousValidatedPlan = null,
    string? SessionId = null,
    string? RequestId = null);

public sealed class ConversationContext
{
    public string? PreviousSuccessfulQuestion { get; private set; }

    public QueryPlan? PreviousValidatedPlan { get; private set; }

    public bool HasContext => PreviousSuccessfulQuestion is not null && PreviousValidatedPlan is not null;

    public void RecordSuccess(string question, QueryPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentNullException.ThrowIfNull(plan);
        PreviousSuccessfulQuestion = question;
        PreviousValidatedPlan = plan;
    }

    public void Clear()
    {
        PreviousSuccessfulQuestion = null;
        PreviousValidatedPlan = null;
    }

    public PlannerRequest CreateRequest(string question) =>
        new(question, PreviousSuccessfulQuestion, PreviousValidatedPlan);
}
