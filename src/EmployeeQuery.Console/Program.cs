using System.Text.Json;
using System.Text.Json.Serialization;
using EmployeeQuery.Application;
using EmployeeQuery.ConsoleHost;
using EmployeeQuery.Infrastructure;

return await ProgramEntry.RunAsync(args).ConfigureAwait(false);

internal static class ProgramEntry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> RunAsync(string[] args)
    {
        IApplicationLogger applicationLogger = NullApplicationLogger.Instance;
        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            bool dotenvDisabled = string.Equals(
                Environment.GetEnvironmentVariable("EMPLOYEEQUERY_DISABLE_DOTENV"),
                "1",
                StringComparison.Ordinal);
            DotEnvLoadResult? dotenv = dotenvDisabled ? null : DotEnvFile.LoadOpenAiSettings();
            if (dotenv is not null)
            {
                Console.WriteLine($"[INFO] Loaded local OpenAI configuration from {Path.GetFileName(dotenv.FilePath)} (secret values hidden).");
            }

            bool structuredLogs = string.Equals(
                Environment.GetEnvironmentVariable("EMPLOYEEQUERY_STRUCTURED_LOGS"),
                "1",
                StringComparison.Ordinal);
            applicationLogger = structuredLogs
                ? new JsonLineApplicationLogger(Console.Error)
                : NullApplicationLogger.Instance;
            applicationLogger.Log(new ApplicationLogEvent(
                DateTimeOffset.UtcNow,
                ApplicationEventName.ApplicationStartup,
                null,
                null,
                new Dictionary<string, object?> { ["plain"] = args.Contains("--plain", StringComparer.OrdinalIgnoreCase) }));

            bool testMode = string.Equals(Environment.GetEnvironmentVariable("NL2SQL_TEST_MODE"), "1", StringComparison.Ordinal);
            bool hasOpenAiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
            if (!testMode && !hasOpenAiKey)
            {
                throw new InvalidOperationException(
                    "OPENAI_API_KEY is required for normal operation. Add it to .env or set it in the current shell, then restart the application.");
            }
            string openAiModel = hasOpenAiKey ? ResolveOpenAiModel() : "gpt-5.6-terra";

            bool plain = testMode || args.Contains("--plain", StringComparer.OrdinalIgnoreCase) || Console.IsOutputRedirected;
            AuthorizedDepartment department = SelectDepartment(testMode);
            ApplicationSession session = new(Guid.NewGuid(), department, DateTimeOffset.UtcNow);
            applicationLogger.Log(new ApplicationLogEvent(
                DateTimeOffset.UtcNow,
                ApplicationEventName.DepartmentSelected,
                session.SessionId.ToString("N"),
                null,
                new Dictionary<string, object?> { ["department"] = department.ToString() }));

            Console.WriteLine($"[INFO] Department selected: {department}");
            Console.WriteLine($"[INFO] Session {session.SessionId.ToString("N")[..8]}; scope remains fixed until restart.");

            string databasePath = ResolveDatabasePath(testMode);
            string catalogPath = Path.Combine(AppContext.BaseDirectory, "config", "query-catalog.csv");
            CsvQueryCatalog catalog = new(catalogPath);

            Console.WriteLine("[INFO] Initializing physically scoped read-only session database...");
            applicationLogger.Log(new ApplicationLogEvent(
                DateTimeOffset.UtcNow,
                ApplicationEventName.ScopedDatabaseInitialization,
                session.SessionId.ToString("N"),
                null,
                new Dictionary<string, object?>()));
            await using ScopedDatabaseSession database = await ScopedDatabaseSession
                .CreateAsync(databasePath, department, shutdown.Token)
                .ConfigureAwait(false);
            Console.WriteLine($"[INFO] Scoped database ready: {database.AuthorizedEmployeeIds.Count} authorized employees; source connection closed.");
            applicationLogger.Log(new ApplicationLogEvent(
                DateTimeOffset.UtcNow,
                ApplicationEventName.CopyVerification,
                session.SessionId.ToString("N"),
                null,
                new Dictionary<string, object?>
                {
                    ["authorizedEmployeeCount"] = database.AuthorizedEmployeeIds.Count,
                    ["sourceConnectionClosed"] = database.SourceConnectionClosed,
                }));

            using HttpClient? httpClient = CreateOpenAiClient();
            OpenAiQueryPlanner? openAi = CreateOpenAiPlanner(httpClient, openAiModel, applicationLogger);
            bool modelEvaluationMode = testMode && string.Equals(
                Environment.GetEnvironmentVariable("NL2SQL_MODEL_EVAL_MODE"),
                "1",
                StringComparison.Ordinal);
            if (modelEvaluationMode && openAi is null)
            {
                throw new InvalidOperationException("NL2SQL_MODEL_EVAL_MODE requires OPENAI_API_KEY.");
            }

            IQueryPlanner semanticPlanner = openAi is not null
                ? openAi
                : new SimulatedExhaustedOpenAiPlanner();
            Console.WriteLine(openAi is null
                ? "[INFO] Test protocol is simulating an exhausted transient OpenAI failure; only exact catalog recovery is available."
                : $"[INFO] OpenAI model-first semantic planner enabled with model {openAiModel}.");
            HybridQueryPlanner planner = new(catalog, semanticPlanner, modelEvaluationMode);
            QueryCompiler compiler = new(catalog);
            bool logQuestionText = string.Equals(
                Environment.GetEnvironmentVariable("EMPLOYEEQUERY_LOG_QUESTION"),
                "1",
                StringComparison.Ordinal);
            QueryService service = new(planner, catalog, compiler, database, applicationLogger, logQuestionText);
            ConversationContext conversation = new();

            if (testMode)
            {
                Console.WriteLine("NL2SQL_TEST_STARTUP " + JsonSerializer.Serialize(new { department = department.ToString() }, JsonOptions));
                await RunTestModeAsync(service, session, conversation, shutdown.Token).ConfigureAwait(false);
            }
            else
            {
                IConsoleView view = new SystemConsoleView();
                ConsoleApplication application = new(token => RunInteractiveAsync(service, session, conversation, view, plain, token));
                await using ApplicationHost host = new(application);
                await host.StartAsync(shutdown.Token).ConfigureAwait(false);
                await host.Application.RunAsync(shutdown.Token).ConfigureAwait(false);
                await host.StopAsync(shutdown.Token).ConfigureAwait(false);
            }

            Console.WriteLine("[INFO] Shutdown complete.");
            applicationLogger.Log(new ApplicationLogEvent(
                DateTimeOffset.UtcNow,
                ApplicationEventName.Shutdown,
                session.SessionId.ToString("N"),
                null,
                new Dictionary<string, object?>()));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[INFO] Cancelled.");
            return 0;
        }
        catch (Exception exception)
        {
            applicationLogger.Log(new ApplicationLogEvent(
                DateTimeOffset.UtcNow,
                ApplicationEventName.GuardrailFailure,
                null,
                null,
                new Dictionary<string, object?> { ["exceptionType"] = exception.GetType().Name }));
            Console.Error.WriteLine($"[ERROR] Startup or safety failure: {exception.Message}");
            return 2;
        }
    }

    private static AuthorizedDepartment SelectDepartment(bool testMode)
    {
        if (testMode)
        {
            string? forced = Environment.GetEnvironmentVariable("NL2SQL_TEST_DEPARTMENT");
            if (!AuthorizedDepartment.TryParse(forced, out AuthorizedDepartment department))
            {
                throw new InvalidOperationException("NL2SQL_TEST_DEPARTMENT must be Engineering, Marketing, or Sales in test mode.");
            }

            return department;
        }

        return new RandomDepartmentSelector().SelectDepartment();
    }

    private static string ResolveDatabasePath(bool testMode)
    {
        if (testMode)
        {
            string? path = Environment.GetEnvironmentVariable("NL2SQL_DB_PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("NL2SQL_DB_PATH is required in test mode.");
            }

            return Path.GetFullPath(path);
        }

        return Path.Combine(AppContext.BaseDirectory, "data", "employees.db");
    }

    private static HttpClient? CreateOpenAiClient()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
        {
            return null;
        }

        return new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    private static string ResolveOpenAiModel()
    {
        string? configured = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        if (configured is not null && string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "OPENAI_MODEL exists in the process environment but is blank. Remove it to use gpt-5.6-terra, or set it to a model ID.");
        }

        return configured?.Trim() ?? "gpt-5.6-terra";
    }

    private static OpenAiQueryPlanner? CreateOpenAiPlanner(
        HttpClient? client,
        string model,
        IApplicationLogger applicationLogger)
    {
        string? key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return client is null || string.IsNullOrWhiteSpace(key)
            ? null
            : new OpenAiQueryPlanner(client, key, model, applicationLogger);
    }

    private static async Task RunTestModeAsync(QueryService service, ApplicationSession session, ConversationContext conversation, CancellationToken cancellationToken)
    {
        while (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using JsonDocument input = JsonDocument.Parse(line);
            if (input.RootElement.TryGetProperty("command", out JsonElement command)
                && command.GetString() is string commandText
                && commandText.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string question = input.RootElement.TryGetProperty("question", out JsonElement questionElement)
                ? questionElement.GetString() ?? string.Empty
                : string.Empty;
            QueryResponse response = await service.ProcessAsync(question, session, conversation, cancellationToken).ConfigureAwait(false);
            Console.WriteLine("NL2SQL_TEST_RESULT " + JsonSerializer.Serialize(response, JsonOptions));
        }
    }

    private static async Task RunInteractiveAsync(
        QueryService service,
        ApplicationSession session,
        ConversationContext conversation,
        IConsoleView view,
        bool plain,
        CancellationToken cancellationToken)
    {
        _ = plain;
        bool explain = false;
        view.WriteLine();
        view.WriteLine("Employee Natural-Language Query Console");
        view.WriteLine($"Authorized department: {session.AuthorizedDepartment} (fixed until restart)");
        PrintHelp(view);

        while (!cancellationToken.IsCancellationRequested)
        {
            view.Write("query> ");
            string? input = await view.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (input is null || input.Trim() is "/exit" or "exit" or "quit")
            {
                return;
            }

            switch (input.Trim().ToLowerInvariant())
            {
                case "/help":
                    PrintHelp(view);
                    continue;
                case "/explain":
                    explain = !explain;
                    view.WriteLine($"Explain mode {(explain ? "enabled" : "disabled")}.");
                    continue;
                case "/clear":
                    conversation.Clear();
                    view.WriteLine("Conversation context cleared. Department scope is unchanged.");
                    continue;
            }

            QueryResponse response = await service.ProcessAsync(input, session, conversation, cancellationToken).ConfigureAwait(false);
            view.WriteLine(response.Message);
            if (response.Status == "success")
            {
                ConsoleTable.Write(view, response.Columns, response.Rows);
                if (explain)
                {
                    view.WriteLine();
                    view.WriteLine($"Plan: {response.QueryId} via {response.Planner}");
                    view.WriteLine("Validated semantics: " + JsonSerializer.Serialize(response.Plan, JsonOptions));
                    view.WriteLine("Result descriptor: " + JsonSerializer.Serialize(response.Descriptor, JsonOptions));
                    view.WriteLine($"Strategy: {response.Strategy}");
                    view.WriteLine($"Policy: Department={response.Department}; physical scope + SQL predicate + ID validation");
                    view.WriteLine("SQL: " + response.Sql);
                    view.WriteLine("Parameters: " + JsonSerializer.Serialize(response.Parameters, JsonOptions));
                    view.WriteLine($"Rows: {response.Rows.Count}; execution: {response.DurationMilliseconds} ms");
                    view.WriteLine($"Request: {response.RequestId}; model: {response.Model ?? "local"}; prompt: {response.PromptVersion ?? "n/a"} ({response.PromptFingerprint?[..Math.Min(12, response.PromptFingerprint.Length)] ?? "n/a"}); reasoning: {response.ReasoningEffort ?? "n/a"}");
                }
            }

            view.WriteLine();
        }
    }

    private static void PrintHelp(IConsoleView view)
    {
        view.WriteLine("Ask about employees, certifications, salaries, bonuses, start dates, or benefits.");
        view.WriteLine("Examples: Who are the software engineers? | Which employees have an AWS certification? | What is the average salary?");
        view.WriteLine("Commands: /help, /explain, /clear, /exit (exit and quit also work)");
    }
}

internal sealed class SimulatedExhaustedOpenAiPlanner : IQueryPlanner
{
    public Task<PlannerOutcome> PlanAsync(string question, CancellationToken cancellationToken) =>
        throw new QueryPlannerUnavailableException(
            "The deterministic test protocol simulated an OpenAI transport failure after retry exhaustion.",
            "transport");
}

internal static class ConsoleTable
{
    public static void Write(IConsoleView view, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        if (columns.Count == 0)
        {
            return;
        }

        string[][] displayRows = rows.Select((row, _) => row.Select((value, index) => Format(columns[index], value)).ToArray()).ToArray();
        int[] widths = columns.Select((column, index) =>
            Math.Min(36, Math.Max(column.Length, displayRows.Select(row => row[index].Length).DefaultIfEmpty(0).Max()))).ToArray();
        string divider = "+-" + string.Join("-+-", widths.Select(width => new string('-', width))) + "-+";
        view.WriteLine(divider);
        view.WriteLine("| " + string.Join(" | ", columns.Select((value, index) => Crop(value, widths[index]).PadRight(widths[index]))) + " |");
        view.WriteLine(divider);
        foreach (string[] row in displayRows)
        {
            view.WriteLine("| " + string.Join(" | ", row.Select((value, index) => Crop(value, widths[index]).PadRight(widths[index]))) + " |");
        }

        view.WriteLine(divider);
    }

    private static string Format(string column, object? value)
    {
        if (value is null)
        {
            return "—";
        }

        if (value is double or float or decimal
            && (column.Contains("Salary", StringComparison.OrdinalIgnoreCase)
                || column.Contains("Balance", StringComparison.OrdinalIgnoreCase)
                || column.Contains("Bonus", StringComparison.OrdinalIgnoreCase)
                || column.Contains("Compensation", StringComparison.OrdinalIgnoreCase)))
        {
            return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture).ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        }

        return TerminalText.Escape(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static string Crop(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";
}
