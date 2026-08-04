using System.Collections.ObjectModel;
using System.Globalization;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

public sealed class SemanticSqlCompiler
{
    private readonly Dictionary<string, object?> _parameters = new(StringComparer.Ordinal);
    private int _parameterIndex;

    public CompiledQuery Compile(QueryPlan plan, ApplicationSession session)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(session);
        SemanticQuerySpec query = plan.Semantics
            ?? throw new InvalidOperationException("A dynamic compiler plan requires semantic query data.");
        SemanticValidationResult validation = SemanticQueryValidator.Validate(plan);
        if (!validation.IsValid)
        {
            string message = string.Join("; ", validation.Errors.Select(error => $"{error.Code}: {error.Message}"));
            throw new InvalidOperationException($"The semantic query plan is invalid: {message}");
        }

        _parameters.Clear();
        _parameterIndex = 0;
        _parameters["department"] = session.AuthorizedDepartment.ToString();

        (string select, IReadOnlyList<string> columns) = BuildSelect(plan, query);
        string from = BuildFrom(plan, query);
        bool directCertification = from.StartsWith("Certification AS c ", StringComparison.Ordinal);
        bool directBenefits = from.StartsWith("Benefits AS b ", StringComparison.Ordinal);
        List<string> predicates = ["e.Department = :department"];
        predicates.AddRange(query.FilterGroups.Select(group => BuildGroup(group, plan.Grain, directCertification, directBenefits)));
        string where = string.Join(" AND ", predicates.Select(value => $"({value})"));
        string sql = $"SELECT {select} FROM {from} WHERE {where}";

        if (plan.Family == PlanFamily.GroupedAggregate)
        {
            sql += " GROUP BY " + GroupExpression(query.GroupBy!.Value);
            if (query.Having is { } having)
            {
                sql += " HAVING " + Compare(AggregateExpression(query.Aggregate!), having.Operator, having.Value, having.UpperValue);
            }
        }

        if (plan.Family == PlanFamily.TopRecord && query.IncludeTies)
        {
            string direction = query.Sort!.Direction == SortDirection.Descending ? "DESC" : "ASC";
            _parameters["limit"] = SemanticQueryValidator.MaximumRows;
            string secondaryProjection = query.ThenSort is { } thenSort
                ? $", {SortExpression(thenSort.Field)} AS __then_sort"
                : string.Empty;
            string identityOrder = query.ThenSort is { } requestedThenSort
                ? $"__then_sort {(requestedThenSort.Direction == SortDirection.Descending ? "DESC" : "ASC")}, __employee_id ASC, __stable_id ASC"
                : plan.Grain == ResultGrain.Employee
                    ? "__employee_id ASC"
                    : "__employee_id ASC, __stable_id ASC";
            sql = $"WITH ranked AS (SELECT {select}, e.EmployeeId AS __employee_id, {StableIdentity(plan.Grain)} AS __stable_id, " +
                  $"DENSE_RANK() OVER (ORDER BY {SortExpression(query.Sort.Field)} {direction}) AS __rank{secondaryProjection} FROM {from} WHERE {where}) " +
                  $"SELECT {string.Join(", ", columns)} FROM ranked WHERE __rank = 1 ORDER BY {identityOrder} LIMIT :limit";
            return CreateCompiled(plan, session, sql, columns, SemanticQueryValidator.MaximumRows, "-ties");
        }

        sql += BuildOrder(plan, query, columns);
        int? appliedLimit = query.Limit;
        if (plan.Family is PlanFamily.RecordList or PlanFamily.TopRecord)
        {
            appliedLimit ??= SemanticQueryValidator.DefaultRows;
            _parameters["limit"] = appliedLimit.Value;
            sql += " LIMIT :limit";
        }

        return CreateCompiled(plan, session, sql, columns, appliedLimit);
    }

    private CompiledQuery CreateCompiled(
        QueryPlan plan,
        ApplicationSession session,
        string sql,
        IReadOnlyList<string> columns,
        int? appliedLimit,
        string strategySuffix = "") =>
        new(
            sql,
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(_parameters, StringComparer.Ordinal)),
            columns,
            plan.Grain,
            new QueryPolicyProof(session.AuthorizedDepartment, true, true, "semantic-compiler/1.0"),
            $"semantic-{plan.Family.ToString().ToLowerInvariant()}-{plan.Grain.ToString().ToLowerInvariant()}{strategySuffix}",
            appliedLimit,
            ResultDescriptors.Create(plan, columns, appliedLimit is not null));

    private static (string Select, IReadOnlyList<string> Columns) BuildSelect(QueryPlan plan, SemanticQuerySpec query)
    {
        if (plan.Family is PlanFamily.ScalarAggregate or PlanFamily.GroupedAggregate)
        {
            string aggregate = AggregateExpression(query.Aggregate!);
            string aggregateAlias = AggregateAlias(query);
            if (plan.Family == PlanFamily.ScalarAggregate)
            {
                return ($"{aggregate} AS {aggregateAlias}", new[] { aggregateAlias });
            }

            string group = GroupExpression(query.GroupBy!.Value);
            string groupAlias = GroupAlias(query.GroupBy.Value);
            return ($"{group} AS {groupAlias}, {aggregate} AS {aggregateAlias}", new[] { groupAlias, aggregateAlias });
        }

        IReadOnlyList<OutputField> fields = query.OutputFields.Count > 0
            ? query.OutputFields
            : DefaultFields(plan.Grain);
        bool combinedChildSummary = fields.Contains(OutputField.CertificationCount)
            && fields.Any(field => field is OutputField.BenefitRecordCount or OutputField.BenefitCount);
        List<string> projections = fields
            .Select(field => $"{OutputExpression(field)} AS {OutputAlias(field, combinedChildSummary)}")
            .ToList();
        List<string> columns = fields.Select(field => OutputAlias(field, combinedChildSummary)).ToList();
        if (!fields.Contains(OutputField.EmployeeId))
        {
            projections.Add("e.EmployeeId AS __AuthorizedEmployeeId");
            columns.Add("__AuthorizedEmployeeId");
        }
        return (string.Join(", ", projections), columns);
    }

    private static string BuildFrom(QueryPlan plan, SemanticQuerySpec query)
    {
        if (plan.Family is PlanFamily.ScalarAggregate or PlanFamily.GroupedAggregate)
        {
            if (query.GroupBy == GroupableField.CertificationName || query.Aggregate?.Measure == AggregateMeasure.Certifications)
            {
                return "Certification AS c JOIN Employee AS e ON e.EmployeeId = c.EmployeeId";
            }
            if (query.GroupBy == GroupableField.BenefitsPackage
                || query.Aggregate?.Measure is AggregateMeasure.BenefitRecords or AggregateMeasure.RemainingBalance)
            {
                return "Benefits AS b JOIN Employee AS e ON e.EmployeeId = b.EmployeeId";
            }
            return "Employee AS e";
        }

        return plan.Grain switch
        {
            ResultGrain.Certification when query.IncludeEmployeesWithoutChildRecords => "Employee AS e LEFT JOIN Certification AS c ON e.EmployeeId = c.EmployeeId",
            ResultGrain.Certification => "Certification AS c JOIN Employee AS e ON e.EmployeeId = c.EmployeeId",
            ResultGrain.Benefit when query.IncludeEmployeesWithoutChildRecords => "Employee AS e LEFT JOIN Benefits AS b ON e.EmployeeId = b.EmployeeId",
            ResultGrain.Benefit => "Benefits AS b JOIN Employee AS e ON e.EmployeeId = b.EmployeeId",
            _ => "Employee AS e",
        };
    }

    private string BuildGroup(FilterGroup group, ResultGrain grain, bool directCertification, bool directBenefits) =>
        string.Join(" OR ", group.Clauses.Select(clause => $"({BuildClause(clause, grain, directCertification, directBenefits)})"));

    private string BuildClause(FilterClause clause, ResultGrain grain, bool directCertification, bool directBenefits) => clause switch
    {
        TextFilterClause text => BuildText(text, grain, directCertification, directBenefits),
        NumericFilterClause numeric => BuildNumeric(numeric, grain, directBenefits),
        DateFilterClause date => BuildDate(date, grain, directCertification),
        BooleanFilterClause boolean => BuildBoolean(boolean, grain, directCertification),
        _ => throw new InvalidOperationException("Unknown semantic filter clause."),
    };

    private string BuildText(TextFilterClause clause, ResultGrain grain, bool directCertification, bool directBenefits)
    {
        string parameter = AddParameter(clause.Operator == TextFilterOperator.Equals ? clause.Value : EscapeLike(clause.Value));
        string expression = clause.Field switch
        {
            TextFilterField.EmployeeName => "e.Name",
            TextFilterField.Role => "e.Role",
            TextFilterField.CertificationName => "c_filter.CertificationName",
            TextFilterField.BenefitsPackage => "b_filter.BenefitsPackage",
            _ => throw new InvalidOperationException("Unsupported text field."),
        };
        string comparison = clause.Operator switch
        {
            TextFilterOperator.Equals => $"LOWER({expression}) = LOWER({parameter})",
            TextFilterOperator.Contains => $"LOWER({expression}) LIKE '%' || LOWER({parameter}) || '%' ESCAPE '\\'",
            TextFilterOperator.StartsWith => $"LOWER({expression}) LIKE LOWER({parameter}) || '%' ESCAPE '\\'",
            _ => throw new InvalidOperationException("Unsupported text operator."),
        };
        return clause.Field switch
        {
            TextFilterField.CertificationName when grain == ResultGrain.Certification || directCertification => comparison.Replace("c_filter.", "c.", StringComparison.Ordinal),
            TextFilterField.BenefitsPackage when grain == ResultGrain.Benefit || directBenefits => comparison.Replace("b_filter.", "b.", StringComparison.Ordinal),
            TextFilterField.CertificationName => $"EXISTS (SELECT 1 FROM Certification AS c_filter WHERE c_filter.EmployeeId = e.EmployeeId AND {comparison})",
            TextFilterField.BenefitsPackage => $"EXISTS (SELECT 1 FROM Benefits AS b_filter WHERE b_filter.EmployeeId = e.EmployeeId AND {comparison})",
            _ => comparison,
        };
    }

    private string BuildNumeric(NumericFilterClause clause, ResultGrain grain, bool directBenefits)
    {
        string expression = clause.Field switch
        {
            NumericFilterField.SalaryAmount => "e.SalaryAmount",
            NumericFilterField.YearlyBonusAmount => "COALESCE(e.YearlyBonusAmount, 0)",
            NumericFilterField.TotalCompensation => "(e.SalaryAmount + COALESCE(e.YearlyBonusAmount, 0))",
            NumericFilterField.RemainingBalance => "b_filter.RemainingBalance",
            NumericFilterField.TotalRemainingBenefitsBalance => "COALESCE((SELECT SUM(b_total.RemainingBalance) FROM Benefits AS b_total WHERE b_total.EmployeeId = e.EmployeeId), 0)",
            NumericFilterField.CertificationCount => "(SELECT COUNT(*) FROM Certification AS c_count WHERE c_count.EmployeeId = e.EmployeeId)",
            NumericFilterField.BenefitRecordCount => "(SELECT COUNT(*) FROM Benefits AS b_count WHERE b_count.EmployeeId = e.EmployeeId)",
            _ => throw new InvalidOperationException("Unsupported numeric field."),
        };
        string comparison = Compare(expression, clause.Operator, clause.Value, clause.UpperValue);
        return clause.Field == NumericFilterField.RemainingBalance && (grain == ResultGrain.Benefit || directBenefits)
            ? comparison.Replace("b_filter.", "b.", StringComparison.Ordinal)
            : clause.Field == NumericFilterField.RemainingBalance
            ? $"EXISTS (SELECT 1 FROM Benefits AS b_filter WHERE b_filter.EmployeeId = e.EmployeeId AND {comparison})"
            : comparison;
    }

    private string BuildDate(DateFilterClause clause, ResultGrain grain, bool directCertification)
    {
        string expression = clause.Field == DateFilterField.EmploymentStartDate
            ? "e.EmploymentStartDate"
            : "c_date.DateAchieved";
        string comparison = CompareDate(expression, clause.Operator, clause.Value, clause.UpperValue);
        return clause.Field == DateFilterField.DateAchieved && (grain == ResultGrain.Certification || directCertification)
            ? comparison.Replace("c_date.", "c.", StringComparison.Ordinal)
            : clause.Field == DateFilterField.DateAchieved
            ? $"EXISTS (SELECT 1 FROM Certification AS c_date WHERE c_date.EmployeeId = e.EmployeeId AND {comparison})"
            : comparison;
    }

    private static string BuildBoolean(BooleanFilterClause clause, ResultGrain grain, bool directCertification)
    {
        if (clause.Field == BooleanFilterField.HasRecordedYearlyBonus)
        {
            return clause.Value ? "e.YearlyBonusAmount IS NOT NULL" : "e.YearlyBonusAmount IS NULL";
        }
        if (clause.Field == BooleanFilterField.CertificationAchievedBeforeEmploymentStart)
        {
            if (grain != ResultGrain.Certification || !directCertification)
            {
                throw new InvalidOperationException("Certification-to-employment date comparison requires direct certification grain.");
            }
            string comparison = "date(c.DateAchieved) < date(e.EmploymentStartDate)";
            return clause.Value ? comparison : "NOT (" + comparison + ")";
        }

        string table = clause.Field == BooleanFilterField.HasCertification ? "Certification" : "Benefits";
        string alias = clause.Field == BooleanFilterField.HasCertification ? "c_exists" : "b_exists";
        string exists = $"EXISTS (SELECT 1 FROM {table} AS {alias} WHERE {alias}.EmployeeId = e.EmployeeId)";
        return clause.Value ? exists : "NOT " + exists;
    }

    private string Compare(string expression, NumericFilterOperator op, decimal value, decimal? upper)
    {
        string lowerParameter = AddParameter(decimal.ToDouble(value));
        return op switch
        {
            NumericFilterOperator.Equals => $"{expression} = {lowerParameter}",
            NumericFilterOperator.GreaterThan => $"{expression} > {lowerParameter}",
            NumericFilterOperator.GreaterThanOrEqual => $"{expression} >= {lowerParameter}",
            NumericFilterOperator.LessThan => $"{expression} < {lowerParameter}",
            NumericFilterOperator.LessThanOrEqual => $"{expression} <= {lowerParameter}",
            NumericFilterOperator.Between => $"{expression} BETWEEN {lowerParameter} AND {AddParameter(decimal.ToDouble(upper!.Value))}",
            _ => throw new InvalidOperationException("Unsupported numeric operator."),
        };
    }

    private string CompareDate(string expression, DateFilterOperator op, DateOnly value, DateOnly? upper)
    {
        string lowerParameter = AddParameter(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return op switch
        {
            DateFilterOperator.Equals => $"{expression} = {lowerParameter}",
            DateFilterOperator.After => $"{expression} > {lowerParameter}",
            DateFilterOperator.OnOrAfter => $"{expression} >= {lowerParameter}",
            DateFilterOperator.Before => $"{expression} < {lowerParameter}",
            DateFilterOperator.OnOrBefore => $"{expression} <= {lowerParameter}",
            DateFilterOperator.Between => $"{expression} BETWEEN {lowerParameter} AND {AddParameter(upper!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}",
            _ => throw new InvalidOperationException("Unsupported date operator."),
        };
    }

    private static string BuildOrder(QueryPlan plan, SemanticQuerySpec query, IReadOnlyList<string> columns)
    {
        if (query.Sort is { } sort)
        {
            string direction = sort.Direction == SortDirection.Descending ? "DESC" : "ASC";
            if (plan.Family == PlanFamily.GroupedAggregate)
            {
                string expression = sort.Field == SortableField.AggregateValue
                    ? columns[1]
                    : SortExpression(sort.Field);
                List<string> terms = [$"{expression} {direction}"];
                if (query.ThenSort is { } groupedThen && groupedThen.Field != sort.Field)
                {
                    string groupedThenExpression = groupedThen.Field == SortableField.AggregateValue
                        ? columns[1]
                        : SortExpression(groupedThen.Field);
                    terms.Add($"{groupedThenExpression} {(groupedThen.Direction == SortDirection.Descending ? "DESC" : "ASC")}");
                }
                SortableField groupSortField = GroupSortField(query.GroupBy!.Value);
                if (sort.Field != groupSortField && query.ThenSort?.Field != groupSortField)
                {
                    terms.Add($"{columns[0]} ASC");
                }
                return " ORDER BY " + string.Join(", ", terms);
            }
            List<string> recordTerms = [$"{SortExpression(sort.Field)} {direction}"];
            if (query.ThenSort is { } thenSort && thenSort.Field != sort.Field)
            {
                recordTerms.Add($"{SortExpression(thenSort.Field)} {(thenSort.Direction == SortDirection.Descending ? "DESC" : "ASC")}");
            }
            SortableField stableSortField = StableSortField(plan.Grain);
            if (sort.Field != stableSortField && query.ThenSort?.Field != stableSortField)
            {
                recordTerms.Add($"{StableIdentity(plan.Grain)} ASC");
            }
            return " ORDER BY " + string.Join(", ", recordTerms);
        }
        if (plan.Family == PlanFamily.GroupedAggregate)
        {
            return " ORDER BY " + columns[0] + " ASC";
        }
        if (plan.Family == PlanFamily.ScalarAggregate)
        {
            return string.Empty;
        }
        if (plan.Grain == ResultGrain.Certification
            && query.FilterGroups.SelectMany(group => group.Clauses).Any(clause =>
                clause is BooleanFilterClause
                {
                    Field: BooleanFilterField.CertificationAchievedBeforeEmploymentStart,
                    Value: true,
                }))
        {
            return " ORDER BY e.EmployeeId ASC, c.DateAchieved ASC, c.CertificationId ASC";
        }
        return plan.Grain switch
        {
            ResultGrain.Certification => " ORDER BY e.EmployeeId ASC, c.CertificationId ASC",
            ResultGrain.Benefit => " ORDER BY e.EmployeeId ASC, b.BenefitId ASC",
            _ => " ORDER BY e.EmployeeId ASC",
        };
    }

    private static string AggregateExpression(AggregateSpec aggregate)
    {
        string expression = aggregate.Measure switch
        {
            AggregateMeasure.Employees => "DISTINCT e.EmployeeId",
            AggregateMeasure.Certifications => "c.CertificationId",
            AggregateMeasure.BenefitRecords => "b.BenefitId",
            AggregateMeasure.Salary => "e.SalaryAmount",
            AggregateMeasure.YearlyBonus => "e.YearlyBonusAmount",
            AggregateMeasure.YearlyBonusIncludingMissingAsZero => "COALESCE(e.YearlyBonusAmount, 0)",
            AggregateMeasure.TotalCompensation => "(e.SalaryAmount + COALESCE(e.YearlyBonusAmount, 0))",
            AggregateMeasure.RemainingBalance => "b.RemainingBalance",
            AggregateMeasure.TotalRemainingBenefitsBalance => "COALESCE((SELECT SUM(b_total.RemainingBalance) FROM Benefits AS b_total WHERE b_total.EmployeeId=e.EmployeeId), 0)",
            AggregateMeasure.CertificationCount => "(SELECT COUNT(*) FROM Certification AS c_count WHERE c_count.EmployeeId=e.EmployeeId)",
            _ => throw new InvalidOperationException("Unsupported aggregate measure."),
        };
        return aggregate.Function switch
        {
            AggregateFunction.Count => $"COUNT({expression})",
            AggregateFunction.Sum => $"ROUND(SUM({expression}), 2)",
            AggregateFunction.Average => $"ROUND(AVG({expression}), 2)",
            AggregateFunction.Minimum => $"ROUND(MIN({expression}), 2)",
            AggregateFunction.Maximum => $"ROUND(MAX({expression}), 2)",
            _ => throw new InvalidOperationException("Unsupported aggregate function."),
        };
    }

    private static string OutputExpression(OutputField field) => field switch
    {
        OutputField.EmployeeId => "e.EmployeeId",
        OutputField.EmployeeName => "e.Name",
        OutputField.Role => "e.Role",
        OutputField.EmploymentStartDate => "e.EmploymentStartDate",
        OutputField.SalaryAmount => "ROUND(e.SalaryAmount, 2)",
        OutputField.YearlyBonusAmount => "ROUND(COALESCE(e.YearlyBonusAmount, 0), 2)",
        OutputField.TotalCompensation => "ROUND(e.SalaryAmount + COALESCE(e.YearlyBonusAmount, 0), 2)",
        OutputField.CertificationId => "c.CertificationId",
        OutputField.CertificationName => "c.CertificationName",
        OutputField.DateAchieved => "c.DateAchieved",
        OutputField.BenefitId => "b.BenefitId",
        OutputField.BenefitsPackage => "b.BenefitsPackage",
        OutputField.RemainingBalance => "ROUND(b.RemainingBalance, 2)",
        OutputField.CertificationCount => "(SELECT COUNT(*) FROM Certification AS c_count WHERE c_count.EmployeeId=e.EmployeeId)",
        OutputField.BenefitRecordCount or OutputField.BenefitCount => "(SELECT COUNT(*) FROM Benefits AS b_count WHERE b_count.EmployeeId=e.EmployeeId)",
        OutputField.TotalRemainingBenefitsBalance => "ROUND(COALESCE((SELECT SUM(b_total.RemainingBalance) FROM Benefits AS b_total WHERE b_total.EmployeeId=e.EmployeeId), 0), 2)",
        _ => throw new InvalidOperationException("Unsupported output field."),
    };

    private static string OutputAlias(OutputField field, bool combinedChildSummary = false) => field switch
    {
        OutputField.EmployeeName => "Name",
        OutputField.TotalCompensation => "TotalCashCompensation",
        OutputField.TotalRemainingBenefitsBalance => "TotalRemainingBalance",
        OutputField.BenefitRecordCount when combinedChildSummary => "BenefitCount",
        _ => field.ToString(),
    };

    private static IReadOnlyList<OutputField> DefaultFields(ResultGrain grain) => grain switch
    {
        ResultGrain.Certification => [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationName, OutputField.DateAchieved],
        ResultGrain.Benefit => [OutputField.BenefitId, OutputField.EmployeeId, OutputField.EmployeeName, OutputField.BenefitsPackage, OutputField.RemainingBalance],
        _ => [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.Role, OutputField.EmploymentStartDate],
    };

    private static string GroupExpression(GroupableField field) => field switch
    {
        GroupableField.EmployeeName => "e.Name",
        GroupableField.Role => "e.Role",
        GroupableField.CertificationName => "c.CertificationName",
        GroupableField.BenefitsPackage => "b.BenefitsPackage",
        _ => throw new InvalidOperationException("Unsupported group field."),
    };

    private static string SortExpression(SortableField field) => field switch
    {
        SortableField.EmployeeId => "e.EmployeeId",
        SortableField.CertificationId => "c.CertificationId",
        SortableField.BenefitId => "b.BenefitId",
        SortableField.EmployeeName => "e.Name",
        SortableField.Role => "e.Role",
        SortableField.EmploymentStartDate => "e.EmploymentStartDate",
        SortableField.SalaryAmount => "e.SalaryAmount",
        SortableField.YearlyBonusAmount => "COALESCE(e.YearlyBonusAmount, 0)",
        SortableField.TotalCompensation => "e.SalaryAmount + COALESCE(e.YearlyBonusAmount, 0)",
        SortableField.CertificationName => "c.CertificationName",
        SortableField.DateAchieved => "c.DateAchieved",
        SortableField.BenefitsPackage => "b.BenefitsPackage",
        SortableField.RemainingBalance => "b.RemainingBalance",
        SortableField.TotalRemainingBenefitsBalance => "COALESCE((SELECT SUM(b_total.RemainingBalance) FROM Benefits AS b_total WHERE b_total.EmployeeId=e.EmployeeId), 0)",
        SortableField.CertificationCount => "(SELECT COUNT(*) FROM Certification AS c_count WHERE c_count.EmployeeId=e.EmployeeId)",
        SortableField.BenefitRecordCount or SortableField.BenefitCount => "(SELECT COUNT(*) FROM Benefits AS b_count WHERE b_count.EmployeeId=e.EmployeeId)",
        SortableField.AggregateValue => "Value",
        _ => throw new InvalidOperationException("Unsupported sort field."),
    };

    private static string StableIdentity(ResultGrain grain) => grain switch
    {
        ResultGrain.Certification => "c.CertificationId",
        ResultGrain.Benefit => "b.BenefitId",
        _ => "e.EmployeeId",
    };

    private static SortableField StableSortField(ResultGrain grain) => grain switch
    {
        ResultGrain.Certification => SortableField.CertificationId,
        ResultGrain.Benefit => SortableField.BenefitId,
        _ => SortableField.EmployeeId,
    };

    private static SortableField GroupSortField(GroupableField group) => group switch
    {
        GroupableField.EmployeeName => SortableField.EmployeeName,
        GroupableField.Role => SortableField.Role,
        GroupableField.CertificationName => SortableField.CertificationName,
        GroupableField.BenefitsPackage => SortableField.BenefitsPackage,
        _ => throw new InvalidOperationException("Unsupported group field."),
    };

    private string AddParameter(object value)
    {
        string name = $"p{_parameterIndex++}";
        _parameters[name] = value;
        return ":" + name;
    }

    private static string GroupAlias(GroupableField field) => field switch
    {
        GroupableField.EmployeeName => "Name",
        _ => field.ToString(),
    };

    private static string AggregateAlias(SemanticQuerySpec query)
    {
        AggregateSpec aggregate = query.Aggregate!;
        if (aggregate.Function == AggregateFunction.Count)
        {
            return aggregate.Measure switch
            {
                AggregateMeasure.Employees when query.GroupBy is null
                    && query.FilterGroups.SelectMany(group => group.Clauses)
                        .Any(clause => clause is BooleanFilterClause { Field: BooleanFilterField.HasCertification, Value: true })
                    => "CertifiedEmployeeCount",
                AggregateMeasure.Employees => "EmployeeCount",
                AggregateMeasure.Certifications => "CertificationCount",
                AggregateMeasure.BenefitRecords => "BenefitRecordCount",
                _ => aggregate.Measure + "Count",
            };
        }

        return (aggregate.Function, aggregate.Measure) switch
        {
            (AggregateFunction.Average, AggregateMeasure.Salary) => "AverageSalary",
            (AggregateFunction.Average, AggregateMeasure.YearlyBonus) => "AverageRecordedBonus",
            (AggregateFunction.Average, AggregateMeasure.YearlyBonusIncludingMissingAsZero) => "AverageBonusIncludingMissingAsZero",
            (AggregateFunction.Average, AggregateMeasure.RemainingBalance) => "AverageRemainingBalance",
            (AggregateFunction.Sum, AggregateMeasure.RemainingBalance) when query.GroupBy is null => "DepartmentRemainingBalance",
            (AggregateFunction.Sum, AggregateMeasure.RemainingBalance) => "TotalRemainingBalance",
            _ => aggregate.Function + aggregate.Measure.ToString(),
        };
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
