using System.Collections.ObjectModel;
using EmployeeQuery.Application;
using EmployeeQuery.Infrastructure;
using Microsoft.Data.Sqlite;

return await IntegrationTestRunner.RunAsync().ConfigureAwait(false);

internal static class IntegrationTestRunner
{
    private static readonly string[] PrimaryQueryIds = ["EMP-001", "CERT-001", "EMP-003", "CERT-002", "BEN-001"];
    private static int _passed;
    private static int _failed;

    public static async Task<int> RunAsync()
    {
        string databasePath = Path.Combine(AppContext.BaseDirectory, "data", "employees.db");
        string catalogPath = Path.Combine(AppContext.BaseDirectory, "config", "query-catalog.csv");
        CsvQueryCatalog catalog = new(catalogPath);

        foreach (Department departmentValue in Enum.GetValues<Department>())
        {
            AuthorizedDepartment department = new(departmentValue);
            ApplicationSession session = new(Guid.NewGuid(), department, DateTimeOffset.UtcNow);
            await using ScopedDatabaseSession database = await ScopedDatabaseSession.CreateAsync(databasePath, department);
            HybridQueryPlanner planner = new(catalog, new UnavailableQueryPlanner());
            QueryService service = new(planner, catalog, new QueryCompiler(catalog), database);

            Test($"{department}: source is closed and authorized IDs exist", () =>
            {
                True(database.SourceConnectionClosed);
                True(database.AuthorizedEmployeeIds.Count > 0);
            });

            string[] primaryQuestions = PrimaryQueryIds
                .Select(id => catalog.All.Single(definition => definition.Id == id).Question)
                .ToArray();
            foreach (string question in primaryQuestions)
            {
                await TestAsync($"{department}: {question}", async () =>
                {
                    QueryResponse response = await service.ProcessAsync(question, session);
                    Equal("success", response.Status);
                    Equal(department, response.Department);
                    True(response.Sql!.Contains("Department", StringComparison.OrdinalIgnoreCase));
                    True(response.Parameters.Values.Contains(department.ToString()));
                });
            }

            await TestAsync($"{department}: all catalog capabilities execute", async () =>
            {
                foreach (QueryDefinition definition in catalog.All)
                {
                    QueryResponse response = await service.ProcessAsync(definition.Question, session);
                    Equal("success", response.Status);
                    Equal(definition.Columns.Count, response.Columns.Count);
                    if (definition.Id == "EMP-003")
                    {
                        decimal expected = await ExpectedAverageAsync(databasePath, department, withAwsCertification: null);
                        True(response.Message.Contains(expected.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-US")), StringComparison.Ordinal));
                    }
                }
            });

            await TestAsync($"{department}: specific AWS title excludes other AWS certifications", async () =>
            {
                QueryPlan plan = new RecordListPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
                    [OutputField.CertificationId, OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationName, OutputField.DateAchieved],
                    [new FilterGroup([new TextFilterClause(
                        TextFilterField.CertificationName,
                        TextFilterOperator.Equals,
                        "AWS Developer Associate")])],
                    Limit: 100));
                QueryService modelFirstService = new(
                    new HybridQueryPlanner(catalog, new FixedQueryPlanner(plan)),
                    catalog,
                    new QueryCompiler(catalog),
                    database);
                QueryResponse response = await modelFirstService.ProcessAsync(
                    "which employees have aws developer associate cert",
                    session);
                Equal("success", response.Status);
                Equal("stub-model", response.Planner);
                int certificationName = response.Columns
                    .Select((name, index) => (name, index))
                    .Single(pair => pair.name == "CertificationName")
                    .index;
                True(response.Rows.All(row => string.Equals(
                    Convert.ToString(row[certificationName], System.Globalization.CultureInfo.InvariantCulture),
                    "AWS Developer Associate",
                    StringComparison.Ordinal)));
            });

            await TestAsync($"{department}: hidden employee identity is validated and removed before display", async () =>
            {
                QueryPlan plan = new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
                    [OutputField.EmployeeName], [], Limit: 10));
                QueryService modelFirstService = new(
                    new HybridQueryPlanner(catalog, new FixedQueryPlanner(plan)),
                    catalog,
                    new QueryCompiler(catalog),
                    database);
                QueryResponse response = await modelFirstService.ProcessAsync("List employee names", session);
                Equal("success", response.Status);
                True(response.Sql!.Contains("__AuthorizedEmployeeId", StringComparison.Ordinal));
                True(response.Columns.SequenceEqual(["Name"], StringComparer.Ordinal));
                True(response.Descriptor!.Columns.All(column => !column.Hidden));
                True(response.Rows.All(row => row.Count == 1));
            });

            await TestAsync($"{department}: model-mapped AWS salary average preserves certification filter", async () =>
            {
                QueryPlan plan = new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
                    [],
                    [new FilterGroup([new TextFilterClause(TextFilterField.CertificationName, TextFilterOperator.Contains, "AWS")])],
                    new AggregateSpec(AggregateFunction.Average, AggregateMeasure.Salary)));
                QueryService modelFirstService = new(
                    new HybridQueryPlanner(catalog, new FixedQueryPlanner(plan)),
                    catalog,
                    new QueryCompiler(catalog),
                    database);
                QueryResponse response = await modelFirstService.ProcessAsync(
                    "whats the avg salary for employees with aws certification",
                    session);

                Equal("success", response.Status);
                Equal("stub-model", response.Planner);
                True(response.Sql!.Contains("EXISTS", StringComparison.OrdinalIgnoreCase));
                decimal expected = await ExpectedAverageAsync(databasePath, department, withAwsCertification: true);
                Equal(expected, Convert.ToDecimal(response.Rows.Single().Single(), System.Globalization.CultureInfo.InvariantCulture));
            });

            await TestAsync($"{department}: model-mapped salary average preserves no-certification filter", async () =>
            {
                QueryPlan plan = new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
                    [],
                    [new FilterGroup([new BooleanFilterClause(BooleanFilterField.HasCertification, false)])],
                    new AggregateSpec(AggregateFunction.Average, AggregateMeasure.Salary)));
                QueryService modelFirstService = new(
                    new HybridQueryPlanner(catalog, new FixedQueryPlanner(plan)),
                    catalog,
                    new QueryCompiler(catalog),
                    database);
                QueryResponse response = await modelFirstService.ProcessAsync(
                    "avg salary for employees without certs",
                    session);

                Equal("success", response.Status);
                Equal("stub-model", response.Planner);
                True(response.Sql!.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase));
                decimal expected = await ExpectedAverageAsync(databasePath, department, withAwsCertification: false);
                Equal(expected, Convert.ToDecimal(response.Rows.Single().Single(), System.Globalization.CultureInfo.InvariantCulture));
            });

            await TestAsync($"{department}: planner infrastructure failure is an error with no SQL", async () =>
            {
                QueryService unavailableService = new(
                    new UnavailableQueryPlanner(),
                    catalog,
                    new QueryCompiler(catalog),
                    database);
                QueryResponse response = await unavailableService.ProcessAsync("a novel filtered request", session);
                Equal("error", response.Status);
                True(response.Sql is null);
                True(response.Rows.Count == 0);
            });

            await TestAsync($"{department}: execution failure is controlled and preserves context", async () =>
            {
                ConversationContext conversation = new();
                conversation.RecordSuccess("List all employees", new RecordListPlan("EMP-002", ResultGrain.Employee));
                QueryPlan accepted = conversation.PreviousValidatedPlan!;
                QueryService failingService = new(
                    new FixedQueryPlanner(new RecordListPlan("EMP-002", ResultGrain.Employee)),
                    catalog,
                    new QueryCompiler(catalog),
                    new ThrowingExecutor());
                QueryResponse response = await failingService.ProcessAsync("List all employees", session, conversation);
                Equal("error", response.Status);
                True(response.Sql is null);
                True(response.Rows.Count == 0);
                True(response.Message.Contains("No data was returned", StringComparison.Ordinal));
                Equal(accepted, conversation.PreviousValidatedPlan);
            });

            QueryCompiler semanticCompiler = new(catalog);
            QueryPlan[] semanticPlans =
            [
                new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
                    [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.Role],
                    [new FilterGroup([new TextFilterClause(TextFilterField.Role, TextFilterOperator.Contains, "software engineer")])],
                    Sort: new SortSpec(SortableField.EmployeeName, SortDirection.Ascending), Limit: 50)),
                new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
                    [OutputField.EmployeeId, OutputField.EmployeeName],
                    [new FilterGroup([new TextFilterClause(TextFilterField.CertificationName, TextFilterOperator.Contains, "AWS")])],
                    Limit: 50)),
                new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
                    [], [], new AggregateSpec(AggregateFunction.Average, AggregateMeasure.Salary))),
                new RecordListPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
                    [OutputField.CertificationId, OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationName, OutputField.DateAchieved],
                    [new FilterGroup([new DateFilterClause(DateFilterField.EmploymentStartDate, DateFilterOperator.After, new DateOnly(2023, 12, 31))])],
                    Limit: 100)),
                new TopRecordPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
                    [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.TotalRemainingBenefitsBalance],
                    [], Sort: new SortSpec(SortableField.TotalRemainingBenefitsBalance, SortDirection.Descending), Limit: 1, IncludeTies: true)),
                new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
                    [OutputField.EmployeeId, OutputField.EmployeeName],
                    [new FilterGroup([new BooleanFilterClause(BooleanFilterField.HasCertification, false)])],
                    Limit: 100)),
                new GroupedAggregatePlan("DYNAMIC", new SemanticQuerySpec(
                    [], [], new AggregateSpec(AggregateFunction.Count, AggregateMeasure.Employees), GroupableField.Role)),
            ];
            foreach (QueryPlan semanticPlan in semanticPlans)
            {
                await TestAsync($"{department}: dynamic {semanticPlan.Family}/{semanticPlan.Grain}", async () =>
                {
                    CompiledQuery compiled = semanticCompiler.Compile(semanticPlan, session);
                    ExecutionResult result = await database.ExecuteAsync(compiled, session, CancellationToken.None);
                    True(result.Columns.Count > 0);
                    True(compiled.Parameters.Values.Contains(department.ToString()));
                    True(compiled.Sql.Contains("e.Department = :department", StringComparison.Ordinal));
                });
            }

            foreach (SemanticOracleCase oracleCase in SemanticOracleCases())
            {
                await TestAsync($"{department}: semantic oracle {oracleCase.Name}", async () =>
                {
                    CompiledQuery compiled = semanticCompiler.Compile(oracleCase.Plan, session);
                    ExecutionResult actual = await database.ExecuteAsync(compiled, session, CancellationToken.None);
                    ExecutionResult expected = await ExecuteOracleAsync(
                        databasePath,
                        department,
                        oracleCase.Sql,
                        oracleCase.Parameters);
                    EqualRows(expected, actual);
                    True(compiled.Sql.Contains("e.Department = :department", StringComparison.Ordinal));
                    True(compiled.Descriptor is not null);
                });
            }

            await TestAsync($"{department}: conversation updates only after successful execution", async () =>
            {
                ConversationContext conversation = new();
                string employeeQuestion = catalog.All.Single(definition => definition.Id == "EMP-002").Question;
                QueryResponse first = await service.ProcessAsync(employeeQuestion, session, conversation);
                Equal("success", first.Status);
                True(conversation.HasContext);
                True(first.RequestId is { Length: 32 });
                True(first.Descriptor is not null);
                True(first.Plan is not null);
                QueryPlan? accepted = conversation.PreviousValidatedPlan;

                QueryResponse refused = await service.ProcessAsync("Update all salaries", session, conversation);
                Equal("refused", refused.Status);
                True(refused.RequestId is { Length: 32 });
                Equal(accepted, conversation.PreviousValidatedPlan);

                QueryResponse followUp = await service.ProcessAsync("sort them by name", session, conversation);
                Equal("error", followUp.Status);
                Equal(accepted, conversation.PreviousValidatedPlan);

                conversation.Clear();
                True(!conversation.HasContext);
                Equal(department, first.Department);
            });

            await TestAsync($"{department}: write statement fails before execution", async () =>
            {
                Dictionary<string, object?> parameters = new() { ["department"] = department.ToString() };
                CompiledQuery forged = new(
                    "DELETE FROM Employee WHERE Department=:department",
                    new ReadOnlyDictionary<string, object?>(parameters),
                    Array.Empty<string>(),
                    ResultGrain.Employee,
                    new QueryPolicyProof(department, true, true, "forged"),
                    "forged",
                    null);
                await ThrowsAsync<InvalidOperationException>(() => database.ExecuteAsync(forged, session, CancellationToken.None));
            });

            await TestAsync($"{department}: mismatched policy proof fails", async () =>
            {
                AuthorizedDepartment other = new(departmentValue == Department.Sales ? Department.Marketing : Department.Sales);
                Dictionary<string, object?> parameters = new() { ["department"] = department.ToString() };
                CompiledQuery forged = new(
                    "SELECT EmployeeId FROM Employee WHERE Department=:department",
                    new ReadOnlyDictionary<string, object?>(parameters),
                    ["EmployeeId"],
                    ResultGrain.Employee,
                    new QueryPolicyProof(other, true, true, "forged"),
                    "forged",
                    null);
                await ThrowsAsync<InvalidOperationException>(() => database.ExecuteAsync(forged, session, CancellationToken.None));
            });

            await TestAsync($"{department}: missing result descriptor fails closed", async () =>
            {
                Dictionary<string, object?> parameters = new() { ["department"] = department.ToString(), ["limit"] = 1 };
                CompiledQuery forged = new(
                    "SELECT EmployeeId FROM Employee WHERE Department=:department LIMIT :limit",
                    new ReadOnlyDictionary<string, object?>(parameters),
                    ["EmployeeId"],
                    ResultGrain.Employee,
                    new QueryPolicyProof(department, true, true, "forged"),
                    "forged",
                    1);
                await ThrowsAsync<InvalidOperationException>(() => database.ExecuteAsync(forged, session, CancellationToken.None));
            });

            await TestAsync($"{department}: unbounded record descriptor fails closed", async () =>
            {
                Dictionary<string, object?> parameters = new() { ["department"] = department.ToString() };
                ResultDescriptor descriptor = new(
                    ResultGrain.Employee,
                    [new ResultColumnDescriptor("EmployeeId", "Employee Id", ResultValueKind.WholeNumber)],
                    ResultSummaryStrategy.RecordList,
                    false,
                    false,
                    false);
                CompiledQuery forged = new(
                    "SELECT EmployeeId FROM Employee WHERE Department=:department",
                    new ReadOnlyDictionary<string, object?>(parameters),
                    ["EmployeeId"],
                    ResultGrain.Employee,
                    new QueryPolicyProof(department, true, true, "forged"),
                    "forged",
                    null,
                    descriptor);
                await ThrowsAsync<InvalidOperationException>(() => database.ExecuteAsync(forged, session, CancellationToken.None));
            });

            await TestAsync($"{department}: unauthorized hidden employee identity fails closed", async () =>
            {
                Dictionary<string, object?> parameters = new()
                {
                    ["department"] = department.ToString(),
                    ["limit"] = 1,
                };
                ResultDescriptor descriptor = new(
                    ResultGrain.Employee,
                    [new ResultColumnDescriptor("__AuthorizedEmployeeId", "Authorization identity", ResultValueKind.WholeNumber, true)],
                    ResultSummaryStrategy.RecordList,
                    true,
                    false,
                    false);
                CompiledQuery forged = new(
                    "SELECT 0 AS __AuthorizedEmployeeId FROM Employee WHERE Department=:department LIMIT :limit",
                    new ReadOnlyDictionary<string, object?>(parameters),
                    ["__AuthorizedEmployeeId"],
                    ResultGrain.Employee,
                    new QueryPolicyProof(department, true, true, "forged"),
                    "forged",
                    1,
                    descriptor);
                await ThrowsAsync<InvalidOperationException>(() => database.ExecuteAsync(forged, session, CancellationToken.None));
            });
        }

        Console.WriteLine($"Integration tests: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task<decimal> ExpectedAverageAsync(
        string databasePath,
        AuthorizedDepartment department,
        bool? withAwsCertification)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        string childPredicate = withAwsCertification switch
        {
            true => "EXISTS (SELECT 1 FROM Certification AS c WHERE c.EmployeeId=e.EmployeeId AND LOWER(c.CertificationName) LIKE '%aws%')",
            false => "NOT EXISTS (SELECT 1 FROM Certification AS c WHERE c.EmployeeId=e.EmployeeId)",
            null => "1=1",
        };
        command.CommandText = $"SELECT ROUND(AVG(e.SalaryAmount), 2) FROM Employee AS e WHERE e.Department=$department AND {childPredicate}";
        command.Parameters.AddWithValue("$department", department.ToString());
        object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<SemanticOracleCase> SemanticOracleCases() =>
    [
        new("role contains", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.Role],
            [new FilterGroup([new TextFilterClause(TextFilterField.Role, TextFilterOperator.Contains, "software engineer")])],
            Sort: new SortSpec(SortableField.EmployeeName, SortDirection.Ascending), Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,e.Role FROM Employee e WHERE e.Department=:department AND LOWER(e.Role) LIKE '%software engineer%' ORDER BY e.Name,e.EmployeeId LIMIT 100"),
        new("certification exists", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName],
            [new FilterGroup([new TextFilterClause(TextFilterField.CertificationName, TextFilterOperator.Contains, "AWS")])], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name FROM Employee e WHERE e.Department=:department AND EXISTS (SELECT 1 FROM Certification c WHERE c.EmployeeId=e.EmployeeId AND LOWER(c.CertificationName) LIKE '%aws%') ORDER BY e.EmployeeId LIMIT 100"),
        new("without certifications", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName],
            [new FilterGroup([new BooleanFilterClause(BooleanFilterField.HasCertification, false)])], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name FROM Employee e WHERE e.Department=:department AND NOT EXISTS (SELECT 1 FROM Certification c WHERE c.EmployeeId=e.EmployeeId) ORDER BY e.EmployeeId LIMIT 100"),
        new("benefit package exists", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName],
            [new FilterGroup([new TextFilterClause(TextFilterField.BenefitsPackage, TextFilterOperator.Equals, "Premium")])], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name FROM Employee e WHERE e.Department=:department AND EXISTS (SELECT 1 FROM Benefits b WHERE b.EmployeeId=e.EmployeeId AND LOWER(b.BenefitsPackage)=LOWER('Premium')) ORDER BY e.EmployeeId LIMIT 100"),
        new("numeric salary range", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.SalaryAmount],
            [new FilterGroup([new NumericFilterClause(NumericFilterField.SalaryAmount, NumericFilterOperator.Between, 90000m, 130000m)])],
            Sort: new SortSpec(SortableField.SalaryAmount, SortDirection.Descending), Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,ROUND(e.SalaryAmount,2) AS SalaryAmount FROM Employee e WHERE e.Department=:department AND e.SalaryAmount BETWEEN 90000 AND 130000 ORDER BY e.SalaryAmount DESC,e.EmployeeId LIMIT 100"),
        new("date after", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.EmploymentStartDate],
            [new FilterGroup([new DateFilterClause(DateFilterField.EmploymentStartDate, DateFilterOperator.After, new DateOnly(2023, 12, 31))])], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,e.EmploymentStartDate FROM Employee e WHERE e.Department=:department AND e.EmploymentStartDate>'2023-12-31' ORDER BY e.EmployeeId LIMIT 100"),
        new("and of or groups", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.Role],
            [
                new FilterGroup([
                    new TextFilterClause(TextFilterField.Role, TextFilterOperator.Contains, "engineer"),
                    new TextFilterClause(TextFilterField.Role, TextFilterOperator.Contains, "analyst")]),
                new FilterGroup([new NumericFilterClause(NumericFilterField.SalaryAmount, NumericFilterOperator.GreaterThan, 100000m)]),
            ], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,e.Role FROM Employee e WHERE e.Department=:department AND (LOWER(e.Role) LIKE '%engineer%' OR LOWER(e.Role) LIKE '%analyst%') AND e.SalaryAmount>100000 ORDER BY e.EmployeeId LIMIT 100"),
        new("exact certification title", new RecordListPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
            [OutputField.CertificationId, OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationName, OutputField.DateAchieved],
            [new FilterGroup([new TextFilterClause(TextFilterField.CertificationName, TextFilterOperator.Equals, "AWS Developer Associate")])], Limit: 100)),
            "SELECT c.CertificationId,e.EmployeeId,e.Name AS Name,c.CertificationName,c.DateAchieved FROM Certification c JOIN Employee e ON e.EmployeeId=c.EmployeeId WHERE e.Department=:department AND LOWER(c.CertificationName)=LOWER('AWS Developer Associate') ORDER BY e.EmployeeId,c.CertificationId LIMIT 100"),
        new("benefit balance filter", new RecordListPlan("DYNAMIC", ResultGrain.Benefit, new SemanticQuerySpec(
            [OutputField.BenefitId, OutputField.EmployeeId, OutputField.EmployeeName, OutputField.BenefitsPackage, OutputField.RemainingBalance],
            [new FilterGroup([new NumericFilterClause(NumericFilterField.RemainingBalance, NumericFilterOperator.GreaterThan, 1000m)])], Limit: 100)),
            "SELECT b.BenefitId,e.EmployeeId,e.Name AS Name,b.BenefitsPackage,ROUND(b.RemainingBalance,2) AS RemainingBalance FROM Benefits b JOIN Employee e ON e.EmployeeId=b.EmployeeId WHERE e.Department=:department AND b.RemainingBalance>1000 ORDER BY b.BenefitId LIMIT 100"),
        new("average salary with AWS", new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [new FilterGroup([new TextFilterClause(TextFilterField.CertificationName, TextFilterOperator.Contains, "AWS")])],
            new AggregateSpec(AggregateFunction.Average, AggregateMeasure.Salary))),
            "SELECT ROUND(AVG(e.SalaryAmount),2) AS AverageSalary FROM Employee e WHERE e.Department=:department AND EXISTS (SELECT 1 FROM Certification c WHERE c.EmployeeId=e.EmployeeId AND LOWER(c.CertificationName) LIKE '%aws%')"),
        new("average salary without certifications", new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [new FilterGroup([new BooleanFilterClause(BooleanFilterField.HasCertification, false)])],
            new AggregateSpec(AggregateFunction.Average, AggregateMeasure.Salary))),
            "SELECT ROUND(AVG(e.SalaryAmount),2) AS AverageSalary FROM Employee e WHERE e.Department=:department AND NOT EXISTS (SELECT 1 FROM Certification c WHERE c.EmployeeId=e.EmployeeId)"),
        new("average employee total benefits", new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [], new AggregateSpec(AggregateFunction.Average, AggregateMeasure.TotalRemainingBenefitsBalance))),
            "SELECT ROUND(AVG(COALESCE((SELECT SUM(b.RemainingBalance) FROM Benefits b WHERE b.EmployeeId=e.EmployeeId),0)),2) AS AverageTotalRemainingBenefitsBalance FROM Employee e WHERE e.Department=:department"),
        new("average certification count including zero", new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [], new AggregateSpec(AggregateFunction.Average, AggregateMeasure.CertificationCount))),
            "SELECT ROUND(AVG((SELECT COUNT(*) FROM Certification c WHERE c.EmployeeId=e.EmployeeId)),2) AS AverageCertificationCount FROM Employee e WHERE e.Department=:department"),
        new("employees grouped by role", new GroupedAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [], new AggregateSpec(AggregateFunction.Count, AggregateMeasure.Employees), GroupableField.Role)),
            "SELECT e.Role AS Role,COUNT(DISTINCT e.EmployeeId) AS EmployeeCount FROM Employee e WHERE e.Department=:department GROUP BY e.Role ORDER BY Role"),
        new("certifications grouped by title", new GroupedAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [], new AggregateSpec(AggregateFunction.Count, AggregateMeasure.Certifications), GroupableField.CertificationName)),
            "SELECT c.CertificationName AS CertificationName,COUNT(c.CertificationId) AS CertificationCount FROM Certification c JOIN Employee e ON e.EmployeeId=c.EmployeeId WHERE e.Department=:department GROUP BY c.CertificationName ORDER BY CertificationName"),
        new("benefit average grouped by package", new GroupedAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [], new AggregateSpec(AggregateFunction.Average, AggregateMeasure.RemainingBalance), GroupableField.BenefitsPackage,
            new SortSpec(SortableField.AggregateValue, SortDirection.Descending))),
            "SELECT b.BenefitsPackage AS BenefitsPackage,ROUND(AVG(b.RemainingBalance),2) AS AverageRemainingBalance FROM Benefits b JOIN Employee e ON e.EmployeeId=b.EmployeeId WHERE e.Department=:department GROUP BY b.BenefitsPackage ORDER BY AverageRemainingBalance DESC,BenefitsPackage"),
        new("highest employee total benefits ties", new TopRecordPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.TotalRemainingBenefitsBalance], [],
            Sort: new SortSpec(SortableField.TotalRemainingBenefitsBalance, SortDirection.Descending), Limit: 1, IncludeTies: true)),
            "WITH totals AS (SELECT e.EmployeeId,e.Name,ROUND(COALESCE(SUM(b.RemainingBalance),0),2) AS TotalRemainingBalance FROM Employee e LEFT JOIN Benefits b ON b.EmployeeId=e.EmployeeId WHERE e.Department=:department GROUP BY e.EmployeeId,e.Name) SELECT EmployeeId,Name,TotalRemainingBalance FROM totals WHERE TotalRemainingBalance=(SELECT MAX(TotalRemainingBalance) FROM totals) ORDER BY EmployeeId LIMIT 200"),
        new("latest certification ties", new TopRecordPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
            [OutputField.CertificationId, OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationName, OutputField.DateAchieved], [],
            Sort: new SortSpec(SortableField.DateAchieved, SortDirection.Descending), Limit: 1, IncludeTies: true)),
            "WITH scoped AS (SELECT c.CertificationId,e.EmployeeId,e.Name,c.CertificationName,c.DateAchieved FROM Certification c JOIN Employee e ON e.EmployeeId=c.EmployeeId WHERE e.Department=:department) SELECT CertificationId,EmployeeId,Name,CertificationName,DateAchieved FROM scoped WHERE DateAchieved=(SELECT MAX(DateAchieved) FROM scoped) ORDER BY CertificationId LIMIT 200"),
        new("cross-child filter without multiplication", new RecordListPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
            [OutputField.CertificationId, OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationName],
            [new FilterGroup([new TextFilterClause(TextFilterField.BenefitsPackage, TextFilterOperator.Equals, "Premium")])], Limit: 100)),
            "SELECT c.CertificationId,e.EmployeeId,e.Name AS Name,c.CertificationName FROM Certification c JOIN Employee e ON e.EmployeeId=c.EmployeeId WHERE e.Department=:department AND EXISTS (SELECT 1 FROM Benefits b WHERE b.EmployeeId=e.EmployeeId AND LOWER(b.BenefitsPackage)=LOWER('Premium')) ORDER BY c.CertificationId LIMIT 100"),
        new("average recorded bonus", new ScalarAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [new FilterGroup([new BooleanFilterClause(BooleanFilterField.HasRecordedYearlyBonus, true)])],
            new AggregateSpec(AggregateFunction.Average, AggregateMeasure.YearlyBonus))),
            "SELECT ROUND(AVG(e.YearlyBonusAmount),2) AS AverageRecordedBonus FROM Employee e WHERE e.Department=:department AND e.YearlyBonusAmount IS NOT NULL"),
        new("missing bonus", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName],
            [new FilterGroup([new BooleanFilterClause(BooleanFilterField.HasRecordedYearlyBonus, false)])], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name FROM Employee e WHERE e.Department=:department AND e.YearlyBonusAmount IS NULL ORDER BY e.EmployeeId LIMIT 100"),
        new("duplicate employee names", new GroupedAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [], new AggregateSpec(AggregateFunction.Count, AggregateMeasure.Employees), GroupableField.EmployeeName,
            Sort: new SortSpec(SortableField.EmployeeName, SortDirection.Ascending),
            Having: new AggregateFilterSpec(NumericFilterOperator.GreaterThan, 1m))),
            "SELECT e.Name AS Name,COUNT(DISTINCT e.EmployeeId) AS EmployeeCount FROM Employee e WHERE e.Department=:department GROUP BY e.Name HAVING COUNT(DISTINCT e.EmployeeId)>1 ORDER BY e.Name"),
        new("certification rows include employment start", new RecordListPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.EmploymentStartDate, OutputField.CertificationName, OutputField.DateAchieved],
            [new FilterGroup([new DateFilterClause(DateFilterField.EmploymentStartDate, DateFilterOperator.OnOrAfter, new DateOnly(2024, 1, 1))])],
            Sort: new SortSpec(SortableField.EmployeeId, SortDirection.Ascending), Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,e.EmploymentStartDate,c.CertificationName,c.DateAchieved FROM Employee e JOIN Certification c ON c.EmployeeId=e.EmployeeId WHERE e.Department=:department AND e.EmploymentStartDate>='2024-01-01' ORDER BY e.EmployeeId,c.CertificationId LIMIT 100"),
        new("certification outer join retains employees", new RecordListPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.EmploymentStartDate, OutputField.CertificationName, OutputField.DateAchieved],
            [new FilterGroup([new DateFilterClause(DateFilterField.EmploymentStartDate, DateFilterOperator.OnOrAfter, new DateOnly(2024, 1, 1))])],
            Sort: new SortSpec(SortableField.EmployeeId, SortDirection.Ascending), Limit: 100,
            IncludeEmployeesWithoutChildRecords: true)),
            "SELECT e.EmployeeId,e.Name AS Name,e.EmploymentStartDate,c.CertificationName,c.DateAchieved FROM Employee e LEFT JOIN Certification c ON c.EmployeeId=e.EmployeeId WHERE e.Department=:department AND e.EmploymentStartDate>='2024-01-01' ORDER BY e.EmployeeId,c.CertificationId LIMIT 100"),
        new("certification before employment start", new RecordListPlan("DYNAMIC", ResultGrain.Certification, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.EmploymentStartDate, OutputField.CertificationName, OutputField.DateAchieved],
            [new FilterGroup([new BooleanFilterClause(BooleanFilterField.CertificationAchievedBeforeEmploymentStart, true)])],
            Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,e.EmploymentStartDate,c.CertificationName,c.DateAchieved FROM Employee e JOIN Certification c ON c.EmployeeId=e.EmployeeId WHERE e.Department=:department AND date(c.DateAchieved)<date(e.EmploymentStartDate) ORDER BY e.EmployeeId,c.DateAchieved,c.CertificationId LIMIT 100"),
        new("employee certification count threshold", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationCount],
            [new FilterGroup([new NumericFilterClause(NumericFilterField.CertificationCount, NumericFilterOperator.GreaterThan, 1m)])],
            Sort: new SortSpec(SortableField.CertificationCount, SortDirection.Descending), Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,(SELECT COUNT(*) FROM Certification c WHERE c.EmployeeId=e.EmployeeId) AS CertificationCount FROM Employee e WHERE e.Department=:department AND (SELECT COUNT(*) FROM Certification c WHERE c.EmployeeId=e.EmployeeId)>1 ORDER BY CertificationCount DESC,e.EmployeeId LIMIT 100"),
        new("employee benefit count threshold", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.BenefitRecordCount],
            [new FilterGroup([new NumericFilterClause(NumericFilterField.BenefitRecordCount, NumericFilterOperator.GreaterThan, 1m)])], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,(SELECT COUNT(*) FROM Benefits b WHERE b.EmployeeId=e.EmployeeId) AS BenefitRecordCount FROM Employee e WHERE e.Department=:department AND (SELECT COUNT(*) FROM Benefits b WHERE b.EmployeeId=e.EmployeeId)>1 ORDER BY e.EmployeeId LIMIT 100"),
        new("multiple child summaries without multiplication", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName, OutputField.CertificationCount, OutputField.BenefitRecordCount, OutputField.TotalRemainingBenefitsBalance], [], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name,(SELECT COUNT(*) FROM Certification c WHERE c.EmployeeId=e.EmployeeId) AS CertificationCount,(SELECT COUNT(*) FROM Benefits b WHERE b.EmployeeId=e.EmployeeId) AS BenefitCount,ROUND(COALESCE((SELECT SUM(b.RemainingBalance) FROM Benefits b WHERE b.EmployeeId=e.EmployeeId),0),2) AS TotalRemainingBalance FROM Employee e WHERE e.Department=:department ORDER BY e.EmployeeId LIMIT 100"),
        new("benefit records grouped by package", new GroupedAggregatePlan("DYNAMIC", new SemanticQuerySpec(
            [], [], new AggregateSpec(AggregateFunction.Count, AggregateMeasure.BenefitRecords), GroupableField.BenefitsPackage,
            Sort: new SortSpec(SortableField.BenefitsPackage, SortDirection.Ascending))),
            "SELECT b.BenefitsPackage,COUNT(b.BenefitId) AS BenefitRecordCount FROM Benefits b JOIN Employee e ON e.EmployeeId=b.EmployeeId WHERE e.Department=:department GROUP BY b.BenefitsPackage ORDER BY b.BenefitsPackage"),
        new("text injection remains data", new RecordListPlan("DYNAMIC", ResultGrain.Employee, new SemanticQuerySpec(
            [OutputField.EmployeeId, OutputField.EmployeeName],
            [new FilterGroup([new TextFilterClause(TextFilterField.EmployeeName, TextFilterOperator.Contains, "%' OR 1=1 --")])], Limit: 100)),
            "SELECT e.EmployeeId,e.Name AS Name FROM Employee e WHERE e.Department=:department AND 0=1 ORDER BY e.EmployeeId LIMIT 100"),
    ];

    private static async Task<ExecutionResult> ExecuteOracleAsync(
        string databasePath,
        AuthorizedDepartment department,
        string sql,
        IReadOnlyDictionary<string, object?>? extraParameters = null)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly };
        await using SqliteConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(":department", department.ToString());
        foreach ((string name, object? value) in extraParameters ?? new Dictionary<string, object?>())
        {
            command.Parameters.AddWithValue(name.StartsWith(':') ? name : $":{name}", value ?? DBNull.Value);
        }
        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        List<string> columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        List<IReadOnlyList<object?>> rows = [];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            object?[] row = new object?[reader.FieldCount];
            for (int index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }
            rows.Add(row);
        }
        return new ExecutionResult(columns, rows, TimeSpan.Zero);
    }

    private static void EqualRows(ExecutionResult expected, ExecutionResult actual)
    {
        Equal(string.Join('|', expected.Columns), string.Join('|', actual.Columns));
        Equal(expected.Rows.Count, actual.Rows.Count);
        for (int rowIndex = 0; rowIndex < expected.Rows.Count; rowIndex++)
        {
            Equal(expected.Rows[rowIndex].Count, actual.Rows[rowIndex].Count);
            for (int columnIndex = 0; columnIndex < expected.Rows[rowIndex].Count; columnIndex++)
            {
                string expectedValue = Convert.ToString(expected.Rows[rowIndex][columnIndex], System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
                string actualValue = Convert.ToString(actual.Rows[rowIndex][columnIndex], System.Globalization.CultureInfo.InvariantCulture) ?? "<null>";
                Equal(expectedValue, actualValue);
            }
        }
    }

    private static void Test(string name, Action action)
    {
        try
        {
            action();
            _passed++;
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static async Task TestAsync(string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            _passed++;
        }
        catch (Exception exception)
        {
            _failed++;
            Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
        }
    }
}

internal sealed class FixedQueryPlanner(QueryPlan plan) : IContextualQueryPlanner
{
    public Task<PlannerOutcome> PlanAsync(string question, CancellationToken cancellationToken) =>
        Task.FromResult<PlannerOutcome>(new PlannerOutcome.Ready(plan, "stub-model"));

    public Task<PlannerOutcome> PlanAsync(PlannerRequest request, CancellationToken cancellationToken) =>
        PlanAsync(request.Question, cancellationToken);
}

internal sealed class UnavailableQueryPlanner : IQueryPlanner
{
    public Task<PlannerOutcome> PlanAsync(string question, CancellationToken cancellationToken) =>
        throw new QueryPlannerUnavailableException("Planner transport unavailable.", "transport");
}

internal sealed class ThrowingExecutor : IQueryExecutor
{
    public Task<ExecutionResult> ExecuteAsync(
        CompiledQuery query,
        ApplicationSession session,
        CancellationToken cancellationToken) =>
        throw new InvalidOperationException("simulated database failure");
}

internal sealed record SemanticOracleCase(
    string Name,
    QueryPlan Plan,
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters = null);
