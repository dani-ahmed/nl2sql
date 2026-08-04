using System.Text.RegularExpressions;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

public sealed class HybridQueryPlanner(
    CsvQueryCatalog catalog,
    IQueryPlanner? openAiPlanner = null,
    bool modelEvaluationMode = false) : IContextualQueryPlanner
{
    private static readonly Regex FollowUp = new(
        @"\b(those|them|now|only|among those|what about|sort them)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Unsafe = new(
        @"\b(ignore|disable|bypass|override).{0,30}\b(department|guardrail|restriction)|\b(drop|delete|update|insert|alter|attach|detach|pragma|vacuum)\b|sqlite_master|union\s+select|raw\s+bytes|other\s+(two\s+)?departments|outside\s+my\s+assigned\s+department",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public Task<PlannerOutcome> PlanAsync(string question, CancellationToken cancellationToken) =>
        PlanAsync(new PlannerRequest(question), cancellationToken);

    public async Task<PlannerOutcome> PlanAsync(PlannerRequest request, CancellationToken cancellationToken)
    {
        string question = request.Question;
        string normalized = CsvQueryCatalog.Normalize(question);
        if (!modelEvaluationMode && Unsafe.IsMatch(question))
        {
            return new PlannerOutcome.Unsupported("That request conflicts with the fixed department scope or read-only policy.");
        }

        if (normalized.Length == 0)
        {
            return new PlannerOutcome.Clarification("What would you like to know about employees, certifications, or benefits?");
        }

        if (FollowUp.IsMatch(question) && request.PreviousValidatedPlan is null)
        {
            return new PlannerOutcome.Clarification("What earlier result should this follow-up refer to?");
        }

        if (openAiPlanner is not null)
        {
            try
            {
                PlannerOutcome modelOutcome = openAiPlanner is IContextualQueryPlanner contextual
                    ? await contextual.PlanAsync(request, cancellationToken).ConfigureAwait(false)
                    : await openAiPlanner.PlanAsync(question, cancellationToken).ConfigureAwait(false);
                return modelOutcome;
            }
            catch (QueryPlannerUnavailableException exception) when (!modelEvaluationMode && IsTransientFailure(exception.Category))
            {
                if (catalog.TryGetOutcome(question, out CatalogOutcome? exactFallback))
                {
                    return FromCatalog(exactFallback, "catalog-after-openai-failure");
                }

                throw;
            }
        }

        throw new QueryPlannerUnavailableException(
            "OpenAI semantic planning is required. Set OPENAI_API_KEY and restart the application.",
            "configuration");
    }

    private static bool IsTransientFailure(string category) => category is
        "transport" or "rateLimit" or "server" or "timeout" or "provider";

    private PlannerOutcome FromCatalog(CatalogOutcome outcome, string planner) => outcome.Status switch
    {
        "success" => Ready(outcome.QueryId, planner),
        "clarification" => new PlannerOutcome.Clarification(outcome.Message, planner),
        _ => new PlannerOutcome.Unsupported(outcome.Message, planner),
    };

    private PlannerOutcome Ready(string queryId, string planner)
    {
        if (!catalog.TryGet(queryId, out QueryDefinition? definition))
        {
            return new PlannerOutcome.Unsupported("The requested query capability is unavailable.");
        }

        return new PlannerOutcome.Ready(QueryPlan.FromDefinition(definition), planner);
    }

}
