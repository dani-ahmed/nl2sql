using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

public sealed class OpenAiPlannerUnavailableException(string message, string category)
    : QueryPlannerUnavailableException(message, category);

public sealed class OpenAiQueryPlanner(
    HttpClient client,
    string apiKey,
    string model = "gpt-5.6-terra",
    IApplicationLogger? applicationLogger = null) : IContextualQueryPlanner
{
    private const int MaximumSuccessResponseBytes = 1_048_576;
    private const int MaximumErrorResponseBytes = 65_536;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions ContextJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly PlannerPromptSnapshot _prompt = PlannerPromptBuilder.Build();
    private readonly IApplicationLogger _logger = applicationLogger ?? NullApplicationLogger.Instance;

    public async Task<PlannerOutcome> PlanAsync(string question, CancellationToken cancellationToken)
        => await PlanAsync(new PlannerRequest(question), cancellationToken).ConfigureAwait(false);

    public async Task<PlannerOutcome> PlanAsync(PlannerRequest request, CancellationToken cancellationToken)
    {
        string question = BuildContextualQuestion(request);
        PlanAttempt first = await RequestPlanAsync(question, null, request.SessionId, request.RequestId, cancellationToken).ConfigureAwait(false);
        if (QuestionAmbiguityPolicy.TryGetClarification(request.Question, first.Outcome, out string? ambiguityMessage))
        {
            return new PlannerOutcome.Clarification(ambiguityMessage!, "openai-structured");
        }

        object[] validationFeedback;
        if (first.Outcome is PlannerOutcome.Ready ready)
        {
            SemanticValidationResult validation = SemanticQueryValidator.Validate(ready.Plan);
            if (validation.IsValid)
            {
                return ready;
            }
            if (!validation.RepairEligible)
            {
                return new PlannerOutcome.Clarification("That request combines data in an undefined way. Could you ask for one employee, certification, or benefits measure at a time?", "openai-structured");
            }
            validationFeedback = validation.Errors
                .Select(error => (object)new { error.Code, error.Path, error.Message })
                .ToArray();
        }
        else if (first.RepairEligible)
        {
            validationFeedback = [new { Code = "mapping.invalid", Path = "plan", Message = first.RepairError ?? "The semantic response could not be mapped." }];
        }
        else
        {
            return first.Outcome;
        }

        string repair = JsonSerializer.Serialize(new
        {
            invalidSemanticResponse = first.RawOutput,
            validationErrors = validationFeedback,
            instruction = "Return one complete corrected semantic plan. Do not return a patch, SQL, data, or reasoning.",
        });
        Log(ApplicationEventName.SemanticRepair, request.SessionId, request.RequestId, new Dictionary<string, object?>
        {
            ["promptVersion"] = _prompt.Version,
            ["model"] = model,
            ["errorCount"] = validationFeedback.Length,
        });
        PlanAttempt repaired = await RequestPlanAsync(question, repair, request.SessionId, request.RequestId, cancellationToken).ConfigureAwait(false);
        if (repaired.Outcome is PlannerOutcome.Ready repairedReady && SemanticQueryValidator.Validate(repairedReady.Plan).IsValid)
        {
            return repairedReady with { Planner = "openai-structured-repair" };
        }

        return new PlannerOutcome.Clarification("I could not produce a safe valid plan. Could you rephrase the request more specifically?", "openai-structured-repair");
    }

    private static string BuildContextualQuestion(PlannerRequest request)
    {
        if (request.PreviousValidatedPlan is null || string.IsNullOrWhiteSpace(request.PreviousSuccessfulQuestion))
        {
            return request.Question;
        }
        string prior = JsonSerializer.Serialize(new
        {
            request.PreviousValidatedPlan.Family,
            request.PreviousValidatedPlan.Grain,
            request.PreviousValidatedPlan.Semantics,
        }, ContextJsonOptions);
        return $"Current question: {request.Question}\nPrevious accepted question: {request.PreviousSuccessfulQuestion}\nPrevious validated plan: {prior}\nThe previous question and plan are context for resolving follow-up references. If the current question is standalone, do not inherit prior filters, fields, aggregates, or sorting. Return one complete replacement plan, not a patch.";
    }

    private async Task<PlanAttempt> RequestPlanAsync(
        string question,
        string? repair,
        string? sessionId,
        string? requestId,
        CancellationToken cancellationToken)
    {
        HttpStatusCode? lastStatusCode = null;
        string? lastProviderCode = null;
        bool transportFailure = false;
        bool timeoutFailure = false;
        const int maximumAttempts = 2;
        for (int attempt = 0; attempt < maximumAttempts; attempt++)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(question, repair);
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await ParseResponseAsync(response, cancellationToken).ConfigureAwait(false);
                }

                lastStatusCode = response.StatusCode;
                lastProviderCode = await ReadProviderErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized)
                {
                    return Unavailable("OpenAI authentication failed. Check that OPENAI_API_KEY is a valid API key for the intended project.", "authentication");
                }
                if (response.StatusCode is HttpStatusCode.Forbidden)
                {
                    return Unavailable("OpenAI denied access. Check the API key's project permissions and model access.", "permission");
                }
                if (response.StatusCode is HttpStatusCode.NotFound)
                {
                    return Unavailable($"The configured OpenAI model '{model}' is unavailable to this API project. Check OPENAI_MODEL and project model access.", "modelAccess");
                }
                if (response.StatusCode is HttpStatusCode.TooManyRequests
                    && string.Equals(lastProviderCode, "insufficient_quota", StringComparison.OrdinalIgnoreCase))
                {
                    return Unavailable("The OpenAI API project has no available quota. Check project billing and usage limits.", "quota");
                }
                if (response.StatusCode is not HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500)
                {
                    return Unavailable("OpenAI rejected the planner request. Check OPENAI_MODEL and the Responses API configuration.", "configuration");
                }
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                transportFailure = true;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                transportFailure = true;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timeoutFailure = true;
            }

            if (attempt < maximumAttempts - 1)
            {
                Log(ApplicationEventName.PlannerRetry, sessionId, requestId, new Dictionary<string, object?>
                {
                    ["attempt"] = attempt + 2,
                    ["model"] = model,
                    ["failureCategory"] = FailureCategory(lastStatusCode, transportFailure, timeoutFailure),
                    ["statusCode"] = lastStatusCode is null ? null : (int)lastStatusCode.Value,
                });
                int jitter = Random.Shared.Next(0, 51);
                await Task.Delay(TimeSpan.FromMilliseconds((250 * Math.Pow(2, attempt)) + jitter), cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastStatusCode is HttpStatusCode.TooManyRequests)
        {
            return Unavailable("OpenAI rate limiting persisted after two attempts. Wait briefly, then check project usage limits if it continues.", "rateLimit");
        }
        if (lastStatusCode is not null && (int)lastStatusCode >= 500)
        {
            return Unavailable("OpenAI returned server errors on both attempts. Please retry shortly.", "server");
        }
        if (timeoutFailure)
        {
            return Unavailable("The OpenAI request timed out on both attempts. Check connectivity and retry.", "timeout");
        }
        if (transportFailure)
        {
            return Unavailable("The app could not reach the OpenAI API after two attempts. Check network, proxy, and firewall settings.", "transport");
        }

        return Unavailable("The language planner is temporarily unavailable.", "provider");
    }

    private static PlanAttempt Unavailable(string message, string category) =>
        throw new OpenAiPlannerUnavailableException(message, category);

    private static string FailureCategory(HttpStatusCode? statusCode, bool transportFailure, bool timeoutFailure) =>
        statusCode is HttpStatusCode.TooManyRequests ? "rateLimit"
        : statusCode is not null && (int)statusCode >= 500 ? "server"
        : timeoutFailure ? "timeout"
        : transportFailure ? "transport"
        : "provider";

    private static async Task<string?> ReadProviderErrorCodeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            string body = await ReadBoundedStringAsync(response.Content, MaximumErrorResponseBytes, cancellationToken).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("code", out JsonElement code)
                ? code.GetString()
                : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return null;
        }
    }

    private HttpRequestMessage CreateRequest(string question, string? repair)
    {
        List<object> input =
        [
            new { role = "developer", content = _prompt.Content },
            new { role = "user", content = question },
        ];
        if (repair is not null)
        {
            input.Add(new { role = "developer", content = "Deterministic validation rejected the first response. Repair it once using this machine-readable feedback: " + repair });
        }

        object body = new
        {
            model,
            store = false,
            reasoning = new { effort = _prompt.ReasoningEffort },
            input,
            text = new { format = BuildFormat() },
        };
        HttpRequestMessage request = new(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<PlanAttempt> ParseResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body;
        JsonDocument document;
        try
        {
            body = await ReadBoundedStringAsync(response.Content, MaximumSuccessResponseBytes, cancellationToken).ConfigureAwait(false);
            document = JsonDocument.Parse(body);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return new PlanAttempt(new PlannerOutcome.Unsupported("The language planner returned an invalid response envelope.", "openai-structured"), null);
        }
        using JsonDocument parsedDocument = document;
        string? output = FindOutputText(parsedDocument.RootElement);
        if (output is null)
        {
            return new PlanAttempt(new PlannerOutcome.Unsupported("The language planner returned no usable plan.", "openai-structured"), null);
        }

        PlannerEnvelopeDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PlannerEnvelopeDto>(output, JsonOptions);
        }
        catch (JsonException)
        {
            return new PlanAttempt(new PlannerOutcome.Unsupported("The language planner returned malformed structured output.", "openai-structured"), output);
        }
        if (dto is null)
        {
            return new PlanAttempt(new PlannerOutcome.Unsupported("The language planner returned an invalid plan.", "openai-structured"), output);
        }

        PlannerOutcome outcome;
        if (string.Equals(dto.Outcome, "clarification", StringComparison.OrdinalIgnoreCase))
        {
            outcome = new PlannerOutcome.Clarification(EnsureQuestion(dto.Message), "openai-structured");
        }
        else if (string.Equals(dto.Outcome, "refused", StringComparison.OrdinalIgnoreCase))
        {
            outcome = new PlannerOutcome.Unsupported(string.IsNullOrWhiteSpace(dto.Message) ? "That request is unsupported." : dto.Message, "openai-structured");
        }
        else if (string.Equals(dto.Outcome, "ready", StringComparison.OrdinalIgnoreCase))
        {
            if (TryMap(dto.Plan, out QueryPlan? plan, out string mappingError))
            {
                outcome = new PlannerOutcome.Ready(plan!, "openai-structured", _prompt.Version, _prompt.Fingerprint, model, _prompt.ReasoningEffort);
            }
            else
            {
                return new PlanAttempt(
                    new PlannerOutcome.Unsupported("The language planner returned an unmappable plan: " + mappingError, "openai-structured"),
                    output,
                    true,
                    mappingError);
            }
        }
        else
        {
            outcome = new PlannerOutcome.Unsupported("The language planner returned an unknown outcome.", "openai-structured");
        }

        return new PlanAttempt(outcome, output);
    }

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declaredLength && declaredLength > maximumBytes)
        {
            throw new InvalidDataException("The provider response exceeded the configured size limit.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The provider response exceeded the configured size limit.");
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool TryMap(SemanticPlanDto? dto, out QueryPlan? plan, out string error)
    {
        plan = null;
        error = string.Empty;
        if (dto is null
            || !TryEnum(dto.Family, out PlanFamily family)
            || !TryEnum(dto.Grain, out ResultGrain grain))
        {
            error = "missing or unknown family/grain";
            return false;
        }

        List<OutputField> outputs = [];
        foreach (string value in dto.OutputFields ?? [])
        {
            if (!TryEnum(value, out OutputField field))
            {
                error = "unknown output field";
                return false;
            }
            outputs.Add(field);
        }

        Dictionary<int, List<FilterClause>> groups = [];
        foreach (FilterDto filter in dto.Filters ?? [])
        {
            if (filter.Group is < 0 or >= SemanticQueryValidator.MaximumGroups
                || !TryMapFilter(filter, out FilterClause? clause))
            {
                error = "invalid filter";
                return false;
            }
            if (!groups.TryGetValue(filter.Group, out List<FilterClause>? clauses))
            {
                clauses = [];
                groups[filter.Group] = clauses;
            }
            clauses.Add(clause!);
        }

        AggregateSpec? aggregate = null;
        if (!IsNone(dto.AggregateFunction) || !IsNone(dto.AggregateMeasure))
        {
            if (!TryEnum(dto.AggregateFunction, out AggregateFunction function)
                || !TryEnum(dto.AggregateMeasure, out AggregateMeasure measure))
            {
                error = "invalid aggregate";
                return false;
            }
            aggregate = new AggregateSpec(function, measure);
        }
        GroupableField? groupBy = IsNone(dto.GroupBy) ? null : ParseEnum<GroupableField>(dto.GroupBy, ref error);
        SortSpec? sort = null;
        if (!IsNone(dto.SortField))
        {
            SortableField? field = ParseEnum<SortableField>(dto.SortField, ref error);
            SortDirection? direction = ParseEnum<SortDirection>(dto.SortDirection, ref error);
            if (field is null || direction is null)
            {
                return false;
            }
            sort = new SortSpec(field.Value, direction.Value);
        }
        SortSpec? thenSort = null;
        if (!IsNone(dto.ThenSortField))
        {
            SortableField? field = ParseEnum<SortableField>(dto.ThenSortField, ref error);
            SortDirection? direction = ParseEnum<SortDirection>(dto.ThenSortDirection, ref error);
            if (field is null || direction is null)
            {
                return false;
            }
            thenSort = new SortSpec(field.Value, direction.Value);
        }
        if (error.Length > 0)
        {
            return false;
        }

        AggregateFilterSpec? having = null;
        if (!IsNone(dto.HavingOperator))
        {
            if (!TryEnum(dto.HavingOperator, out NumericFilterOperator havingOperator)
                || !decimal.TryParse(dto.HavingValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal havingValue))
            {
                error = "invalid aggregate result filter";
                return false;
            }
            decimal? havingUpper = decimal.TryParse(dto.HavingUpperValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsedUpper)
                ? parsedUpper
                : null;
            having = new AggregateFilterSpec(havingOperator, havingValue, havingUpper);
        }

        SemanticQuerySpec query = new(
            outputs.AsReadOnly(),
            groups.OrderBy(pair => pair.Key).Select(pair => new FilterGroup(pair.Value.AsReadOnly())).ToArray(),
            aggregate,
            groupBy,
            sort,
            dto.Limit == 0 ? null : dto.Limit,
            dto.IncludeTies,
            dto.IncludeEmployeesWithoutChildRecords,
            having,
            thenSort);
        plan = family switch
        {
            PlanFamily.RecordList => new RecordListPlan("DYNAMIC", grain, query),
            PlanFamily.ScalarAggregate => new ScalarAggregatePlan("DYNAMIC", query),
            PlanFamily.GroupedAggregate => new GroupedAggregatePlan("DYNAMIC", query),
            PlanFamily.TopRecord => new TopRecordPlan("DYNAMIC", grain, query),
            _ => null,
        };
        if (plan is null)
        {
            error = "family and grain are inconsistent";
            return false;
        }
        return true;
    }

    private static bool TryMapFilter(FilterDto dto, out FilterClause? clause)
    {
        clause = null;
        if (dto.Kind.Equals("text", StringComparison.OrdinalIgnoreCase)
            && TryEnum(dto.Field, out TextFilterField textField)
            && TryEnum(dto.Operator, out TextFilterOperator textOperator))
        {
            clause = new TextFilterClause(textField, textOperator, dto.Value);
            return true;
        }
        if (dto.Kind.Equals("numeric", StringComparison.OrdinalIgnoreCase)
            && TryEnum(dto.Field, out NumericFilterField numericField)
            && TryEnum(dto.Operator, out NumericFilterOperator numericOperator)
            && decimal.TryParse(dto.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal number))
        {
            decimal? upper = decimal.TryParse(dto.UpperValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal upperNumber) ? upperNumber : null;
            clause = new NumericFilterClause(numericField, numericOperator, number, upper);
            return true;
        }
        if (dto.Kind.Equals("date", StringComparison.OrdinalIgnoreCase)
            && TryEnum(dto.Field, out DateFilterField dateField)
            && TryEnum(dto.Operator, out DateFilterOperator dateOperator)
            && DateOnly.TryParseExact(dto.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            DateOnly? upper = DateOnly.TryParseExact(dto.UpperValue, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly upperDate) ? upperDate : null;
            clause = new DateFilterClause(dateField, dateOperator, date, upper);
            return true;
        }
        if (dto.Kind.Equals("boolean", StringComparison.OrdinalIgnoreCase)
            && TryEnum(dto.Field, out BooleanFilterField booleanField)
            && bool.TryParse(dto.Value, out bool boolean))
        {
            clause = new BooleanFilterClause(booleanField, boolean);
            return true;
        }
        return false;
    }

    private static object BuildFormat()
    {
        string[] outputFields = Enum.GetNames<OutputField>();
        string[] fields = [.. Enum.GetNames<TextFilterField>(), .. Enum.GetNames<NumericFilterField>(), .. Enum.GetNames<DateFilterField>(), .. Enum.GetNames<BooleanFilterField>()];
        string[] operators = [.. Enum.GetNames<TextFilterOperator>(), .. Enum.GetNames<NumericFilterOperator>(), .. Enum.GetNames<DateFilterOperator>()];
        return new
        {
            type = "json_schema",
            name = "employee_semantic_query_plan",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["outcome"] = new { type = "string", @enum = new[] { "ready", "clarification", "refused" } },
                    ["message"] = new { type = "string" },
                    ["plan"] = new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            ["family"] = new { type = "string", @enum = Enum.GetNames<PlanFamily>() },
                            ["grain"] = new { type = "string", @enum = Enum.GetNames<ResultGrain>() },
                            ["outputFields"] = new { type = "array", items = new { type = "string", @enum = outputFields }, maxItems = 16 },
                            ["filters"] = new
                            {
                                type = "array",
                                maxItems = SemanticQueryValidator.MaximumClauses,
                                items = new
                                {
                                    type = "object",
                                    properties = new Dictionary<string, object>
                                    {
                                        ["group"] = new { type = "integer", minimum = 0, maximum = 7 },
                                        ["kind"] = new { type = "string", @enum = new[] { "text", "numeric", "date", "boolean" } },
                                        ["field"] = new { type = "string", @enum = fields.Distinct().Order().ToArray() },
                                        ["operator"] = new { type = "string", @enum = operators.Distinct().Order().ToArray() },
                                        ["value"] = new { type = "string" },
                                        ["upperValue"] = new { type = "string" },
                                    },
                                    required = new[] { "group", "kind", "field", "operator", "value", "upperValue" },
                                    additionalProperties = false,
                                },
                            },
                            ["aggregateFunction"] = new { type = "string", @enum = WithNone<AggregateFunction>() },
                            ["aggregateMeasure"] = new { type = "string", @enum = WithNone<AggregateMeasure>() },
                            ["groupBy"] = new { type = "string", @enum = WithNone<GroupableField>() },
                            ["sortField"] = new { type = "string", @enum = WithNone<SortableField>() },
                            ["sortDirection"] = new { type = "string", @enum = Enum.GetNames<SortDirection>() },
                            ["thenSortField"] = new { type = "string", @enum = WithNone<SortableField>() },
                            ["thenSortDirection"] = new { type = "string", @enum = Enum.GetNames<SortDirection>() },
                            ["limit"] = new { type = "integer", minimum = 0, maximum = SemanticQueryValidator.MaximumRows },
                            ["includeTies"] = new { type = "boolean" },
                            ["includeEmployeesWithoutChildRecords"] = new { type = "boolean" },
                            ["havingOperator"] = new { type = "string", @enum = WithNone<NumericFilterOperator>() },
                            ["havingValue"] = new { type = "string" },
                            ["havingUpperValue"] = new { type = "string" },
                        },
                        required = new[] { "family", "grain", "outputFields", "filters", "aggregateFunction", "aggregateMeasure", "groupBy", "sortField", "sortDirection", "thenSortField", "thenSortDirection", "limit", "includeTies", "includeEmployeesWithoutChildRecords", "havingOperator", "havingValue", "havingUpperValue" },
                        additionalProperties = false,
                    },
                },
                required = new[] { "outcome", "message", "plan" },
                additionalProperties = false,
            },
        };
    }

    private static string? FindOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out JsonElement type) && type.GetString() == "output_text"
                    && part.TryGetProperty("text", out JsonElement text))
                {
                    return text.GetString();
                }
            }
        }
        return null;
    }

    private static string EnsureQuestion(string? message)
    {
        string value = string.IsNullOrWhiteSpace(message) ? "Could you clarify the intended query?" : message.Trim();
        return value.EndsWith('?') ? value : value + "?";
    }

    private static bool TryEnum<T>(string? value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, true, out parsed) && Enum.IsDefined(parsed);

    private static T? ParseEnum<T>(string? value, ref string error) where T : struct, Enum
    {
        if (TryEnum(value, out T parsed))
        {
            return parsed;
        }
        error = "unknown enum value";
        return null;
    }

    private static bool IsNone(string? value) => string.IsNullOrWhiteSpace(value) || value.Equals("none", StringComparison.OrdinalIgnoreCase);

    private static string[] WithNone<T>() where T : struct, Enum =>
        Enumerable.Repeat("None", 1).Concat(Enum.GetNames<T>()).ToArray();

    private void Log(
        ApplicationEventName eventName,
        string? sessionId,
        string? requestId,
        IReadOnlyDictionary<string, object?> properties) =>
        _logger.Log(new ApplicationLogEvent(DateTimeOffset.UtcNow, eventName, sessionId, requestId, properties));

    private sealed record PlanAttempt(PlannerOutcome Outcome, string? RawOutput, bool RepairEligible = false, string? RepairError = null);
    private sealed record PlannerEnvelopeDto(string Outcome, string Message, SemanticPlanDto? Plan);
    private sealed record SemanticPlanDto(
        string Family,
        string Grain,
        string[]? OutputFields,
        FilterDto[]? Filters,
        string AggregateFunction,
        string AggregateMeasure,
        string GroupBy,
        string SortField,
        string SortDirection,
        string ThenSortField,
        string ThenSortDirection,
        int Limit,
        bool IncludeTies,
        bool IncludeEmployeesWithoutChildRecords,
        string HavingOperator,
        string HavingValue,
        string HavingUpperValue);
    private sealed record FilterDto(int Group, string Kind, string Field, string Operator, string Value, string UpperValue);
}
