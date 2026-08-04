using System.Text.RegularExpressions;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

/// <summary>
/// Applies deterministic, safety-oriented ambiguity rules after the model has been consulted.
/// This does not map questions to SQL; it prevents an untrusted planner from silently choosing
/// one of several materially different business meanings.
/// </summary>
internal static partial class QuestionAmbiguityPolicy
{
    public static bool TryGetClarification(
        string question,
        PlannerOutcome modelOutcome,
        out string? message)
    {
        string value = Whitespace().Replace(question.Trim().ToLowerInvariant(), " ");

        if (value.Contains("highest remaining benefits balance", StringComparison.Ordinal)
            && !ContainsAny(value, "total", "sum", "summed", "single", "record", "individual"))
        {
            message = "Do you mean the highest balance on one benefits record, or the highest total after summing each employee's benefits records?";
            return true;
        }

        if (value.Contains("average bonus", StringComparison.Ordinal)
            && !ContainsAny(value, "recorded", "non-null", "non null", "missing as zero", "missing bonus as zero", "exclude missing", "including missing"))
        {
            message = "Should employees with a missing bonus be excluded from the average, or should their bonus be treated as zero?";
            return true;
        }

        if ((value.Contains("started recently", StringComparison.Ordinal)
                || value.Contains("recently started", StringComparison.Ordinal))
            && !ContainsDateBoundary(value))
        {
            message = "What date or time period should define recently?";
            return true;
        }

        if (value.Contains("top earners", StringComparison.Ordinal)
            && !(ContainsAny(value, "salary", "base pay", "total compensation", "salary plus bonus")
                && Number().IsMatch(value)))
        {
            message = "How many employees should I return, and should earnings mean base salary or salary plus bonus?";
            return true;
        }

        if (GeneralEmployeeCertificationList().IsMatch(value)
            && !ContainsAny(value, "including employees with none", "including employees without", "only employees with", "who have", "that have", "started", "after", "before", "on or after", "on or before"))
        {
            message = "Should employees without certifications be included, or should I return only employees who have certification records?";
            return true;
        }

        if (value.Contains("how many benefits", StringComparison.Ordinal)
            && !ContainsAny(value, "records", "packages", "covered employees", "balance"))
        {
            message = "Should I count benefits records, distinct packages, covered employees, or summarize balances?";
            return true;
        }

        if (ForecastSalary().IsMatch(value))
        {
            message = "No salary forecasting rule is available. What current salary information would you like instead?";
            return true;
        }

        if (modelOutcome is PlannerOutcome.Unsupported && KeyboardSmash().IsMatch(value))
        {
            message = "I could not understand that request. Could you rephrase it as a question about employees, certifications, salaries, bonuses, start dates, or benefits?";
            return true;
        }

        message = null;
        return false;
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private static bool ContainsDateBoundary(string value) =>
        Number().IsMatch(value)
        || ContainsAny(value, "today", "yesterday", "week", "month", "year", "since", "after", "before");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\b\d+\b", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\b(?:list|show)\b\s+(?:all\s+)?employees?\s+and\s+(?:their\s+)?certifications?\b", RegexOptions.CultureInvariant)]
    private static partial Regex GeneralEmployeeCertificationList();

    [GeneratedRegex(@"\b(will|forecast|predict|project)\b.*\bsalar(?:y|ies)\b.*\b20\d{2}\b|\bsalar(?:y|ies)\b.*\b(will|forecast|predict|project)\b.*\b20\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ForecastSalary();

    [GeneratedRegex(@"^(?:\s*(?:asdf|qwer(?:ty)?|zxcv|hjkl|poiuy|lkjh)\s*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyboardSmash();
}
