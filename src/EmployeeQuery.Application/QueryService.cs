namespace EmployeeQuery.Application;

public sealed class QueryService(
    IQueryPlanner planner,
    IQueryCatalog catalog,
    IQueryCompiler compiler,
    IQueryExecutor executor,
    IApplicationLogger? applicationLogger = null,
    bool logQuestionText = false)
{
    private readonly IApplicationLogger _logger = applicationLogger ?? NullApplicationLogger.Instance;

    public async Task<QueryResponse> ProcessAsync(
        string question,
        ApplicationSession session,
        ConversationContext? conversation = null,
        CancellationToken cancellationToken = default)
    {
        string requestId = Guid.NewGuid().ToString("N");
        Dictionary<string, object?> requestProperties = new()
        {
            ["hasConversationContext"] = conversation?.HasContext ?? false,
            ["questionLength"] = question.Length,
        };
        if (logQuestionText)
        {
            requestProperties["question"] = question;
        }
        Log(ApplicationEventName.PlannerRequest, session, requestId, requestProperties);
        PlannerRequest plannerRequest = (conversation?.CreateRequest(question) ?? new PlannerRequest(question)) with
        {
            SessionId = session.SessionId.ToString("N"),
            RequestId = requestId,
        };
        PlannerOutcome outcome;
        try
        {
            outcome = planner is IContextualQueryPlanner contextual
                ? await contextual.PlanAsync(plannerRequest, cancellationToken).ConfigureAwait(false)
                : await planner.PlanAsync(question, cancellationToken).ConfigureAwait(false);
        }
        catch (QueryPlannerUnavailableException exception)
        {
            Log(ApplicationEventName.GuardrailFailure, session, requestId, new Dictionary<string, object?>
            {
                ["failureCategory"] = exception.Category,
            });
            return QueryResponse.Failure(session.AuthorizedDepartment, exception.Message) with
            {
                RequestId = requestId,
            };
        }
        if (outcome is PlannerOutcome.Clarification clarification)
        {
            Log(ApplicationEventName.Clarification, session, requestId);
            return QueryResponse.Clarification(session.AuthorizedDepartment, clarification.Message) with
            {
                RequestId = requestId,
                Planner = clarification.Planner,
            };
        }

        if (outcome is PlannerOutcome.Unsupported unsupported)
        {
            Log(ApplicationEventName.UnsupportedRequest, session, requestId);
            return QueryResponse.Refused(session.AuthorizedDepartment, unsupported.Message) with
            {
                RequestId = requestId,
                Planner = unsupported.Planner,
            };
        }

        if (outcome is not PlannerOutcome.Ready ready)
        {
            Log(ApplicationEventName.GuardrailFailure, session, requestId);
            return QueryResponse.Failure(session.AuthorizedDepartment, "The planner returned an invalid outcome.") with
            {
                RequestId = requestId,
            };
        }

        QueryResponse response;
        try
        {
            response = await ExecuteReadyAsync(ready, session, requestId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log(ApplicationEventName.GuardrailFailure, session, requestId, new Dictionary<string, object?>
            {
                ["failureCategory"] = "compileOrExecution",
                ["exceptionType"] = exception.GetType().Name,
            });
            return QueryResponse.Failure(
                session.AuthorizedDepartment,
                "The query could not be executed safely. No data was returned.") with
            {
                RequestId = requestId,
            };
        }
        if (response.Status == "success")
        {
            conversation?.RecordSuccess(question, ready.Plan);
        }
        return response;
    }

    private async Task<QueryResponse> ExecuteReadyAsync(
        PlannerOutcome.Ready ready,
        ApplicationSession session,
        string requestId,
        CancellationToken cancellationToken)
    {
        QueryDefinition? definition = null;
        if (ready.Plan.Semantics is null && !catalog.TryGet(ready.Plan.QueryId, out definition!))
        {
            Log(ApplicationEventName.GuardrailFailure, session, requestId);
            return QueryResponse.Failure(session.AuthorizedDepartment, "The requested query capability is not supported.") with
            {
                RequestId = requestId,
            };
        }

        if (definition is not null && (definition.Family != ready.Plan.Family || definition.Grain != ready.Plan.Grain))
        {
            Log(ApplicationEventName.GuardrailFailure, session, requestId);
            return QueryResponse.Failure(session.AuthorizedDepartment, "The semantic query plan failed validation.") with
            {
                RequestId = requestId,
            };
        }

        SemanticValidationResult validation = SemanticQueryValidator.Validate(ready.Plan);
        if (!validation.IsValid)
        {
            Log(ApplicationEventName.GuardrailFailure, session, requestId, new Dictionary<string, object?>
            {
                ["validationErrorCount"] = validation.Errors.Count,
            });
            return QueryResponse.Failure(
                session.AuthorizedDepartment,
                "The semantic query plan failed validation: " + string.Join("; ", validation.Errors.Select(error => error.Message))) with
            {
                RequestId = requestId,
            };
        }

        Log(ApplicationEventName.Compilation, session, requestId, new Dictionary<string, object?>
        {
            ["planFamily"] = ready.Plan.Family.ToString(),
            ["resultGrain"] = ready.Plan.Grain.ToString(),
            ["planner"] = ready.Planner,
            ["promptVersion"] = ready.PromptVersion,
            ["model"] = ready.Model,
        });
        CompiledQuery compiled = compiler.Compile(ready.Plan, session);
        ExecutionResult executed = await executor.ExecuteAsync(compiled, session, cancellationToken).ConfigureAwait(false);
        Log(ApplicationEventName.Execution, session, requestId, new Dictionary<string, object?>
        {
            ["compilerStrategy"] = compiled.Strategy,
            ["durationMilliseconds"] = (long)Math.Round(executed.Duration.TotalMilliseconds),
            ["rowCount"] = executed.Rows.Count,
        });
        Log(ApplicationEventName.ResultValidation, session, requestId, new Dictionary<string, object?>
        {
            ["rowCount"] = executed.Rows.Count,
        });
        string message = definition is not null
            ? ResultInterpreter.Summarize(definition, executed.Columns, executed.Rows, compiled.AppliedLimit)
            : ResultInterpreter.Summarize(ready.Plan, executed.Columns, executed.Rows, compiled.AppliedLimit);
        return new QueryResponse(
            "success",
            session.AuthorizedDepartment,
            compiled.Sql,
            compiled.Parameters,
            executed.Columns,
            executed.Rows,
            message,
            ready.Plan.QueryId,
            ready.Planner,
            compiled.Strategy,
            (long)Math.Round(executed.Duration.TotalMilliseconds),
            ready.PromptVersion,
            ready.PromptFingerprint,
            ready.Model,
            ready.ReasoningEffort,
            requestId,
            compiled.Descriptor is null ? null : ResultDescriptors.VisibleOnly(compiled.Descriptor),
            ready.Plan);
    }

    private void Log(
        ApplicationEventName eventName,
        ApplicationSession session,
        string requestId,
        IReadOnlyDictionary<string, object?>? properties = null) =>
        _logger.Log(new ApplicationLogEvent(
            DateTimeOffset.UtcNow,
            eventName,
            session.SessionId.ToString("N"),
            requestId,
            properties ?? new Dictionary<string, object?>()));
}

public static class ResultInterpreter
{
    public static string Summarize(
        QueryDefinition definition,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int? appliedLimit)
    {
        int rowCount = rows.Count;
        if (rowCount == 0)
        {
            return $"No matching {definition.Grain.ToString().ToLowerInvariant()} records were found in the authorized department.";
        }

        if (definition.Family == PlanFamily.ScalarAggregate && columns.Count == 1 && rows[0].Count == 1)
        {
            string label = Humanize(columns[0]).ToLowerInvariant();
            object? value = rows[0][0];
            if (value is null)
            {
                return $"The {label} is unavailable because no matching values were found.";
            }
            string formatted = IsMoneyColumn(columns[0])
                ? FormatMoney(value)
                : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null";
            return $"The {label} is {formatted}.";
        }

        if (definition.Grain == ResultGrain.Certification)
        {
            int employeeIdIndex = IndexOf(columns, "EmployeeId");
            if (employeeIdIndex >= 0)
            {
                int employees = rows.Select(row => row[employeeIdIndex]).Distinct().Count();
                return $"Returned {rowCount} certification record{(rowCount == 1 ? string.Empty : "s")} for {employees} employee{(employees == 1 ? string.Empty : "s")}.";
            }
        }

        if (!string.IsNullOrWhiteSpace(definition.Summary))
        {
            string result = definition.Summary.Replace("{count}", rowCount.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            return appliedLimit is not null && rowCount == appliedLimit
                ? result + $" Display is limited to {appliedLimit} rows."
                : result;
        }

        return $"Returned {rowCount} row{(rowCount == 1 ? string.Empty : "s")}.";
    }

    public static string Summarize(
        QueryPlan plan,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        int? appliedLimit)
    {
        int rowCount = rows.Count;
        if (rowCount == 0)
        {
            return $"No matching {plan.Grain.ToString().ToLowerInvariant()} records were found in the authorized department.";
        }

        string result = plan.Family switch
        {
            PlanFamily.ScalarAggregate => ScalarSummary(
                plan.Semantics?.Aggregate,
                rows[0].Count == 0 ? null : rows[0][0]),
            PlanFamily.GroupedAggregate => $"Returned {rowCount} aggregate group{(rowCount == 1 ? string.Empty : "s")}.",
            PlanFamily.TopRecord => RankedSummary(plan, columns, rows),
            _ => $"Returned {rowCount} {plan.Grain.ToString().ToLowerInvariant()} record{(rowCount == 1 ? string.Empty : "s")}.",
        };
        return appliedLimit is not null && rowCount == appliedLimit
            ? result + $" Display is limited to {appliedLimit} rows."
            : result;
    }

    private static string ScalarSummary(AggregateSpec? aggregate, object? value)
    {
        if (aggregate is null)
        {
            return "Calculated the requested aggregate for the authorized department.";
        }
        string label = aggregate.Measure.ToString();
        string formatted = FormatAggregateValue(aggregate.Measure, value);
        return $"The {aggregate.Function.ToString().ToLowerInvariant()} {label} value is {formatted}.";
    }

    private static string RankedSummary(
        QueryPlan plan,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        if (plan.Semantics?.IncludeTies == true && rows.Count > 1)
        {
            return $"Returned {rows.Count} tied top-ranked records.";
        }
        int nameIndex = IndexOf(columns, "Name");
        SortableField? sort = plan.Semantics?.Sort?.Field;
        int valueIndex = sort is null ? -1 : IndexOf(columns, SortColumnName(sort.Value));
        if (rows.Count == 1 && nameIndex >= 0 && valueIndex >= 0)
        {
            string name = Convert.ToString(rows[0][nameIndex], System.Globalization.CultureInfo.InvariantCulture) ?? "The record";
            return $"{name} is the top-ranked result at {FormatSortValue(sort!.Value, rows[0][valueIndex])}.";
        }
        return $"Returned {rows.Count} top-ranked record{(rows.Count == 1 ? string.Empty : "s")}.";
    }

    private static string SortColumnName(SortableField field) => field switch
    {
        SortableField.EmployeeName => "Name",
        SortableField.TotalCompensation => "TotalCashCompensation",
        SortableField.TotalRemainingBenefitsBalance => "TotalRemainingBalance",
        _ => field.ToString(),
    };

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            if (columns[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool IsMoneyColumn(string column) =>
        column.Contains("Salary", StringComparison.OrdinalIgnoreCase)
        || column.Contains("Bonus", StringComparison.OrdinalIgnoreCase)
        || column.Contains("Balance", StringComparison.OrdinalIgnoreCase)
        || column.Contains("Compensation", StringComparison.OrdinalIgnoreCase);

    private static string Humanize(string value)
    {
        List<char> result = [];
        for (int index = 0; index < value.Length; index++)
        {
            if (index > 0 && char.IsUpper(value[index]) && !char.IsWhiteSpace(value[index - 1]))
            {
                result.Add(' ');
            }
            result.Add(value[index]);
        }
        return new string(result.ToArray());
    }

    private static string FormatAggregateValue(AggregateMeasure measure, object? value) => measure switch
    {
        AggregateMeasure.Salary or AggregateMeasure.YearlyBonus or AggregateMeasure.TotalCompensation
            or AggregateMeasure.RemainingBalance or AggregateMeasure.TotalRemainingBenefitsBalance => FormatMoney(value),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    private static string FormatSortValue(SortableField field, object? value) => field switch
    {
        SortableField.SalaryAmount or SortableField.YearlyBonusAmount or SortableField.TotalCompensation
            or SortableField.RemainingBalance or SortableField.TotalRemainingBenefitsBalance => FormatMoney(value),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
    };

    private static string FormatMoney(object? value)
    {
        decimal amount = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        return amount.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    }
}
