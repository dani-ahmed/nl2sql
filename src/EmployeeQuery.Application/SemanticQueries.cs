using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace EmployeeQuery.Application;

public enum OutputField
{
    EmployeeId,
    EmployeeName,
    Role,
    EmploymentStartDate,
    SalaryAmount,
    YearlyBonusAmount,
    TotalCompensation,
    CertificationId,
    CertificationName,
    DateAchieved,
    BenefitId,
    BenefitsPackage,
    RemainingBalance,
    CertificationCount,
    BenefitRecordCount,
    BenefitCount,
    TotalRemainingBenefitsBalance,
}

public enum TextFilterField { EmployeeName, Role, CertificationName, BenefitsPackage }
public enum NumericFilterField { SalaryAmount, YearlyBonusAmount, TotalCompensation, RemainingBalance, TotalRemainingBenefitsBalance, CertificationCount, BenefitRecordCount }
public enum DateFilterField { EmploymentStartDate, DateAchieved }
public enum BooleanFilterField { HasCertification, HasBenefits, HasRecordedYearlyBonus, CertificationAchievedBeforeEmploymentStart }
public enum AggregateMeasure { Employees, Certifications, BenefitRecords, Salary, YearlyBonus, YearlyBonusIncludingMissingAsZero, TotalCompensation, RemainingBalance, TotalRemainingBenefitsBalance, CertificationCount }
public enum GroupableField { EmployeeName, Role, CertificationName, BenefitsPackage }
public enum SortableField { EmployeeId, CertificationId, BenefitId, EmployeeName, Role, EmploymentStartDate, SalaryAmount, YearlyBonusAmount, TotalCompensation, CertificationName, DateAchieved, BenefitsPackage, RemainingBalance, TotalRemainingBenefitsBalance, CertificationCount, BenefitRecordCount, BenefitCount, AggregateValue }
public enum TextFilterOperator { Equals, Contains, StartsWith }
public enum NumericFilterOperator { Equals, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual, Between }
public enum DateFilterOperator { Equals, After, OnOrAfter, Before, OnOrBefore, Between }
public enum AggregateFunction { Count, Sum, Average, Minimum, Maximum }
public enum SortDirection { Ascending, Descending }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextFilterClause), "text")]
[JsonDerivedType(typeof(NumericFilterClause), "numeric")]
[JsonDerivedType(typeof(DateFilterClause), "date")]
[JsonDerivedType(typeof(BooleanFilterClause), "boolean")]
public abstract record FilterClause;
public sealed record TextFilterClause(TextFilterField Field, TextFilterOperator Operator, string Value) : FilterClause;
public sealed record NumericFilterClause(NumericFilterField Field, NumericFilterOperator Operator, decimal Value, decimal? UpperValue = null) : FilterClause;
public sealed record DateFilterClause(DateFilterField Field, DateFilterOperator Operator, DateOnly Value, DateOnly? UpperValue = null) : FilterClause;
public sealed record BooleanFilterClause(BooleanFilterField Field, bool Value) : FilterClause;
public sealed record FilterGroup(IReadOnlyList<FilterClause> Clauses);
public sealed record AggregateSpec(AggregateFunction Function, AggregateMeasure Measure);
public sealed record SortSpec(SortableField Field, SortDirection Direction);
public sealed record AggregateFilterSpec(NumericFilterOperator Operator, decimal Value, decimal? UpperValue = null);

public sealed record SemanticQuerySpec(
    IReadOnlyList<OutputField> OutputFields,
    IReadOnlyList<FilterGroup> FilterGroups,
    AggregateSpec? Aggregate = null,
    GroupableField? GroupBy = null,
    SortSpec? Sort = null,
    int? Limit = null,
    bool IncludeTies = false,
    bool IncludeEmployeesWithoutChildRecords = false,
    AggregateFilterSpec? Having = null,
    SortSpec? ThenSort = null)
{
    public static SemanticQuerySpec Empty { get; } = new(
        Array.Empty<OutputField>(),
        Array.Empty<FilterGroup>());
}

public static class EmployeeBusinessRules
{
    public static decimal BonusOrZero(decimal? bonus) => bonus ?? 0m;

    public static decimal TotalCompensation(decimal salary, decimal? bonus) => salary + BonusOrZero(bonus);

    public static DateOnly AfterYear(int year) => new(year, 12, 31);

    public static string FormatMoney(decimal value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record SemanticValidationError(
    string Code,
    string Path,
    string Message,
    bool RepairEligible);

public sealed record SemanticValidationResult(IReadOnlyList<SemanticValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public bool RepairEligible => Errors.Count > 0 && Errors.All(error => error.RepairEligible);

    public static SemanticValidationResult Success { get; } = new(Array.Empty<SemanticValidationError>());
}

public sealed class SemanticQueryValidator
{
    public const int MaximumGroups = 8;
    public const int MaximumClausesPerGroup = 5;
    public const int MaximumClauses = 20;
    public const int DefaultRows = 100;
    public const int MaximumRows = 200;

    public static SemanticValidationResult Validate(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Semantics is null)
        {
            return SemanticValidationResult.Success;
        }

        SemanticQuerySpec query = plan.Semantics;
        List<SemanticValidationError> errors = [];
        ValidateShape(plan, query, errors);
        ValidateFields(plan, query, errors);
        ValidateFilters(plan, query, errors);
        ValidateCrossChildSemantics(query, errors);
        return errors.Count == 0
            ? SemanticValidationResult.Success
            : new SemanticValidationResult(new ReadOnlyCollection<SemanticValidationError>(errors));
    }

    private static void ValidateShape(QueryPlan plan, SemanticQuerySpec query, List<SemanticValidationError> errors)
    {
        if (query.FilterGroups is null)
        {
            Add(errors, "filters.null", "filterGroups", "Filter groups cannot be null.", true);
            return;
        }

        if (query.OutputFields is null)
        {
            Add(errors, "output.null", "outputFields", "Output fields cannot be null.", true);
            return;
        }

        if (query.OutputFields.Count != query.OutputFields.Distinct().Count())
        {
            Add(errors, "output.duplicate", "outputFields", "Output fields cannot be duplicated.", true);
        }

        if (query.Limit is <= 0 or > MaximumRows)
        {
            Add(errors, "limit.range", "limit", $"Limit must be between 1 and {MaximumRows}.", true);
        }

        switch (plan.Family)
        {
            case PlanFamily.RecordList when query.Aggregate is not null || query.GroupBy is not null || plan.Grain == ResultGrain.Summary:
                Add(errors, "family.record-list", "plan", "Record-list plans require a record grain and cannot contain aggregate or grouping fields.", true);
                break;
            case PlanFamily.ScalarAggregate when query.Aggregate is null || query.GroupBy is not null || plan.Grain != ResultGrain.Summary:
                Add(errors, "family.scalar", "plan", "Scalar aggregates require one aggregate, summary grain, and no group field.", true);
                break;
            case PlanFamily.GroupedAggregate when query.Aggregate is null || query.GroupBy is null || plan.Grain != ResultGrain.Summary:
                Add(errors, "family.grouped", "plan", "Grouped aggregates require an aggregate, group field, and summary grain.", true);
                break;
            case PlanFamily.TopRecord when query.Sort is null || query.Limit is null || plan.Grain == ResultGrain.Summary
                || query.Aggregate is not null || query.GroupBy is not null:
                Add(errors, "family.top", "plan", "Top-record plans require a record grain, explicit sort, and limit, and cannot contain aggregate or grouping fields.", true);
                break;
        }

        if (plan.Family is PlanFamily.ScalarAggregate or PlanFamily.GroupedAggregate && query.OutputFields.Count != 0)
        {
            Add(errors, "family.aggregate.output", "outputFields", "Aggregate plans derive their output and cannot request record fields.", true);
        }
        if (query.IncludeTies && plan.Family != PlanFamily.TopRecord)
        {
            Add(errors, "ties.family", "includeTies", "Tied-winner behavior is valid only for top-record plans.", true);
        }
        if (query.IncludeTies && query.Limit != 1)
        {
            Add(errors, "ties.limit", "limit", "Tied-winner plans require a semantic limit of one rank.", true);
        }
        if (plan.Family is PlanFamily.ScalarAggregate or PlanFamily.GroupedAggregate && query.Limit is not null)
        {
            Add(errors, "family.aggregate.limit", "limit", "Aggregate plans cannot contain a record limit.", true);
        }
        if (query.IncludeEmployeesWithoutChildRecords
            && (plan.Family != PlanFamily.RecordList || plan.Grain is not (ResultGrain.Certification or ResultGrain.Benefit)))
        {
            Add(errors, "children.inclusion", "includeEmployeesWithoutChildRecords", "Optional child inclusion is valid only for certification- or benefit-grain record lists.", true);
        }
        if (query.Having is not null && plan.Family != PlanFamily.GroupedAggregate)
        {
            Add(errors, "having.family", "having", "Aggregate result filters are valid only for grouped aggregates.", true);
        }
        if (query.Having is { } having)
        {
            ValidateAggregateFilter(having, errors);
        }
        if (query.ThenSort is not null && query.Sort is null)
        {
            Add(errors, "sort.secondary.without-primary", "thenSortField", "A secondary sort requires a primary sort.", true);
        }
    }

    private static void ValidateFields(QueryPlan plan, SemanticQuerySpec query, List<SemanticValidationError> errors)
    {
        HashSet<OutputField> employee =
        [
            OutputField.EmployeeId, OutputField.EmployeeName, OutputField.Role,
            OutputField.EmploymentStartDate, OutputField.SalaryAmount, OutputField.YearlyBonusAmount,
            OutputField.TotalCompensation, OutputField.CertificationCount, OutputField.BenefitRecordCount,
            OutputField.BenefitCount, OutputField.TotalRemainingBenefitsBalance,
        ];
        HashSet<OutputField> certification =
        [
            OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationId,
            OutputField.EmploymentStartDate, OutputField.CertificationName, OutputField.DateAchieved,
        ];
        HashSet<OutputField> benefit =
        [
            OutputField.EmployeeId, OutputField.EmployeeName, OutputField.BenefitId,
            OutputField.BenefitsPackage, OutputField.RemainingBalance,
        ];
        HashSet<OutputField> allowed = plan.Grain switch
        {
            ResultGrain.Employee => employee,
            ResultGrain.Certification => certification,
            ResultGrain.Benefit => benefit,
            _ => [],
        };
        foreach (OutputField field in query.OutputFields.Where(field => !allowed.Contains(field)))
        {
            Add(errors, "output.incompatible", $"outputFields.{field}", $"{field} is incompatible with {plan.Grain} grain.", true);
        }

        if (query.Aggregate is { } aggregate && !AggregateCompatible(aggregate))
        {
            Add(errors, "aggregate.incompatible", "aggregate", $"{aggregate.Function} is incompatible with {aggregate.Measure}.", true);
        }

        if (query.Sort is { } sort && !SortCompatible(plan, query, sort.Field))
        {
            Add(errors, "sort.incompatible", "sortField", $"{sort.Field} is incompatible with {plan.Family}/{plan.Grain}.", true);
        }
        if (query.ThenSort is { } thenSort && !SortCompatible(plan, query, thenSort.Field))
        {
            Add(errors, "sort.secondary.incompatible", "thenSortField", $"{thenSort.Field} is incompatible with {plan.Family}/{plan.Grain}.", true);
        }

        if (query.GroupBy is { } group && query.Aggregate is { } groupedAggregate && !GroupCompatible(group, groupedAggregate.Measure))
        {
            Add(errors, "group.incompatible", "groupBy", $"{groupedAggregate.Measure} cannot be grouped by {group} without undefined attribution.", false);
        }
    }

    private static void ValidateFilters(QueryPlan plan, SemanticQuerySpec query, List<SemanticValidationError> errors)
    {
        if (query.FilterGroups.Count > MaximumGroups)
        {
            Add(errors, "filters.groups.limit", "filterGroups", $"At most {MaximumGroups} OR groups are allowed.", true);
        }

        int clauseCount = 0;
        for (int groupIndex = 0; groupIndex < query.FilterGroups.Count; groupIndex++)
        {
            FilterGroup? group = query.FilterGroups[groupIndex];
            if (group?.Clauses is null || group.Clauses.Count == 0)
            {
                Add(errors, "filters.group.empty", $"filterGroups[{groupIndex}]", "A filter group must contain at least one clause.", true);
                continue;
            }

            clauseCount += group.Clauses.Count;
            if (group.Clauses.Count > MaximumClausesPerGroup)
            {
                Add(errors, "filters.clauses.limit", $"filterGroups[{groupIndex}]", $"At most {MaximumClausesPerGroup} clauses are allowed per group.", true);
            }

            if (group.Clauses.Count != group.Clauses.Distinct().Count())
            {
                Add(errors, "filters.duplicate", $"filterGroups[{groupIndex}]", "Duplicate filter clauses are not allowed.", true);
            }

            for (int clauseIndex = 0; clauseIndex < group.Clauses.Count; clauseIndex++)
            {
                FilterClause clause = group.Clauses[clauseIndex];
                string path = $"filterGroups[{groupIndex}].clauses[{clauseIndex}]";
                if (clause is BooleanFilterClause { Field: BooleanFilterField.CertificationAchievedBeforeEmploymentStart }
                    && plan.Grain != ResultGrain.Certification)
                {
                    Add(errors, "filter.relative-date.grain", path, "Certification-to-employment date comparison requires certification grain.", true);
                }
                switch (clause)
                {
                    case TextFilterClause text when string.IsNullOrWhiteSpace(text.Value):
                        Add(errors, "filter.text.blank", path, "Text filter values cannot be blank.", true);
                        break;
                    case NumericFilterClause numeric when numeric.Operator == NumericFilterOperator.Between
                        && (numeric.UpperValue is null || numeric.Value > numeric.UpperValue):
                        Add(errors, "filter.numeric.range", path, "Numeric ranges require an upper value not below the lower value.", true);
                        break;
                    case NumericFilterClause numeric when numeric.Operator != NumericFilterOperator.Between && numeric.UpperValue is not null:
                        Add(errors, "filter.numeric.upper", path, "Only a between filter may contain an upper numeric value.", true);
                        break;
                    case DateFilterClause date when date.Operator == DateFilterOperator.Between
                        && (date.UpperValue is null || date.Value > date.UpperValue):
                        Add(errors, "filter.date.range", path, "Date ranges require an upper date not before the lower date.", true);
                        break;
                    case DateFilterClause date when date.Operator != DateFilterOperator.Between && date.UpperValue is not null:
                        Add(errors, "filter.date.upper", path, "Only a between filter may contain an upper date value.", true);
                        break;
                }
            }
        }

        if (clauseCount > MaximumClauses)
        {
            Add(errors, "filters.total.limit", "filterGroups", $"At most {MaximumClauses} clauses are allowed in total.", true);
        }
    }

    private static void ValidateCrossChildSemantics(SemanticQuerySpec query, List<SemanticValidationError> errors)
    {
        if (query.GroupBy == GroupableField.CertificationName
            && query.Aggregate?.Measure is AggregateMeasure.RemainingBalance or AggregateMeasure.TotalRemainingBenefitsBalance)
        {
            Add(errors, "cross-child.attribution", "aggregate", "Benefits cannot be attributed across an employee's multiple certifications.", false);
        }
        if (query.GroupBy == GroupableField.BenefitsPackage
            && query.Aggregate?.Measure == AggregateMeasure.CertificationCount)
        {
            Add(errors, "cross-child.attribution", "aggregate", "Certifications cannot be attributed across an employee's multiple benefits packages.", false);
        }
    }

    private static bool AggregateCompatible(AggregateSpec aggregate) => aggregate.Measure switch
    {
        AggregateMeasure.Employees or AggregateMeasure.Certifications or AggregateMeasure.BenefitRecords => aggregate.Function == AggregateFunction.Count,
        AggregateMeasure.CertificationCount => aggregate.Function is AggregateFunction.Sum or AggregateFunction.Average or AggregateFunction.Minimum or AggregateFunction.Maximum,
        _ => aggregate.Function is AggregateFunction.Sum or AggregateFunction.Average or AggregateFunction.Minimum or AggregateFunction.Maximum,
    };

    private static bool SortCompatible(QueryPlan plan, SemanticQuerySpec query, SortableField field)
    {
        if (plan.Family == PlanFamily.GroupedAggregate)
        {
            return field == SortableField.AggregateValue || query.GroupBy switch
            {
                GroupableField.EmployeeName => field == SortableField.EmployeeName,
                GroupableField.Role => field == SortableField.Role,
                GroupableField.CertificationName => field == SortableField.CertificationName,
                GroupableField.BenefitsPackage => field == SortableField.BenefitsPackage,
                _ => false,
            };
        }

        return plan.Grain switch
        {
            ResultGrain.Employee => field is SortableField.EmployeeName or SortableField.Role
                or SortableField.EmployeeId
                or SortableField.EmploymentStartDate or SortableField.SalaryAmount
                or SortableField.YearlyBonusAmount or SortableField.TotalCompensation
                or SortableField.TotalRemainingBenefitsBalance or SortableField.CertificationCount
                or SortableField.BenefitRecordCount or SortableField.BenefitCount,
            ResultGrain.Certification => field is SortableField.EmployeeId or SortableField.CertificationId
                or SortableField.EmployeeName or SortableField.CertificationName or SortableField.DateAchieved,
            ResultGrain.Benefit => field is SortableField.EmployeeId or SortableField.BenefitId
                or SortableField.EmployeeName or SortableField.BenefitsPackage or SortableField.RemainingBalance,
            _ => false,
        };
    }

    private static bool GroupCompatible(GroupableField group, AggregateMeasure measure) => group switch
    {
        GroupableField.EmployeeName => measure == AggregateMeasure.Employees,
        GroupableField.Role => true,
        GroupableField.CertificationName => measure is AggregateMeasure.Employees or AggregateMeasure.Certifications,
        GroupableField.BenefitsPackage => measure is AggregateMeasure.Employees or AggregateMeasure.BenefitRecords or AggregateMeasure.RemainingBalance,
        _ => false,
    };

    private static void ValidateAggregateFilter(AggregateFilterSpec filter, List<SemanticValidationError> errors)
    {
        if (filter.Operator == NumericFilterOperator.Between
            && (filter.UpperValue is null || filter.Value > filter.UpperValue))
        {
            Add(errors, "having.range", "having", "Aggregate ranges require an upper value not below the lower value.", true);
        }
        if (filter.Operator != NumericFilterOperator.Between && filter.UpperValue is not null)
        {
            Add(errors, "having.upper", "having", "Only a between aggregate filter may contain an upper value.", true);
        }
    }

    private static void Add(List<SemanticValidationError> errors, string code, string path, string message, bool repair) =>
        errors.Add(new SemanticValidationError(code, path, message, repair));
}
