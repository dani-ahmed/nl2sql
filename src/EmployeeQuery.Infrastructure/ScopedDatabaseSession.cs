using System.Diagnostics;
using System.Text.RegularExpressions;
using EmployeeQuery.Application;
using Microsoft.Data.Sqlite;

namespace EmployeeQuery.Infrastructure;

public sealed class ScopedDatabaseSession : IScopedDatabaseSession, IQueryExecutor
{
    private static readonly Regex Forbidden = new(
        @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|ATTACH|DETACH|PRAGMA|VACUUM|REINDEX)\b|;\s*\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private ScopedDatabaseSession(
        SqliteConnection connection,
        AuthorizedDepartment department,
        IReadOnlySet<long> employeeIds)
    {
        _connection = connection;
        Department = department;
        AuthorizedEmployeeIds = employeeIds;
        SourceConnectionClosed = true;
    }

    public AuthorizedDepartment Department { get; }

    public IReadOnlySet<long> AuthorizedEmployeeIds { get; }

    public bool SourceConnectionClosed { get; }

    public static async Task<ScopedDatabaseSession> CreateAsync(
        string sourcePath,
        AuthorizedDepartment department,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The employee database was not found.", sourcePath);
        }

        SqliteConnection destination = new("Data Source=:memory:");
        await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteNonQueryAsync(destination, "PRAGMA foreign_keys = ON", cancellationToken).ConfigureAwait(false);
            await CreateSchemaAsync(destination, cancellationToken).ConfigureAwait(false);

            SqliteConnectionStringBuilder sourceBuilder = new()
            {
                DataSource = Path.GetFullPath(sourcePath),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
            };
            HashSet<long> employeeIds = [];
            int employeeCount;
            int benefitsCount;
            int certificationCount;
            await using (SqliteConnection source = new(sourceBuilder.ToString()))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using SqliteTransaction sourceTransaction = (SqliteTransaction)await source.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using SqliteTransaction transaction = (SqliteTransaction)await destination.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                employeeCount = await CopyEmployeesAsync(source, sourceTransaction, destination, transaction, department, employeeIds, cancellationToken).ConfigureAwait(false);
                benefitsCount = await CopyBenefitsAsync(source, sourceTransaction, destination, transaction, department, cancellationToken).ConfigureAwait(false);
                certificationCount = await CopyCertificationsAsync(source, sourceTransaction, destination, transaction, department, cancellationToken).ConfigureAwait(false);
                await VerifyAsync(destination, transaction, department, employeeCount, benefitsCount, certificationCount, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await sourceTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await ExecuteNonQueryAsync(destination, "PRAGMA query_only = ON", cancellationToken).ConfigureAwait(false);
            return new ScopedDatabaseSession(destination, department, employeeIds);
        }
        catch
        {
            await destination.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ExecutionResult> ExecuteAsync(
        CompiledQuery query,
        ApplicationSession session,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(session);
        ValidatePolicy(query, session);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            await using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = query.Sql;
            command.CommandTimeout = 10;
            foreach ((string name, object? value) in query.Parameters)
            {
                command.Parameters.AddWithValue(name.StartsWith(':') ? name : $":{name}", value ?? DBNull.Value);
            }

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            List<string> columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            if (!columns.SequenceEqual(query.Columns, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The executed result shape differs from the compiler descriptor.");
            }

            ResultDescriptor descriptor = query.Descriptor!;
            List<IReadOnlyList<object?>> physicalRows = [];
            int employeeIdOrdinal = columns.FindIndex(name => Normalize(name) is "employeeid" or "authorizedemployeeid");
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                object?[] row = new object?[reader.FieldCount];
                for (int index = 0; index < row.Length; index++)
                {
                    row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                if (employeeIdOrdinal >= 0
                    && row[employeeIdOrdinal] is not null
                    && !AuthorizedEmployeeIds.Contains(Convert.ToInt64(row[employeeIdOrdinal], System.Globalization.CultureInfo.InvariantCulture)))
                {
                    throw new InvalidOperationException("A result row failed the authorized employee identity check.");
                }

                physicalRows.Add(row);
                if (physicalRows.Count > 200)
                {
                    throw new InvalidOperationException("The result exceeded the hard row limit.");
                }
            }

            stopwatch.Stop();
            int[] visibleOrdinals = descriptor.Columns
                .Select((column, index) => (column, index))
                .Where(item => !item.column.Hidden)
                .Select(item => item.index)
                .ToArray();
            string[] visibleColumns = visibleOrdinals.Select(index => columns[index]).ToArray();
            IReadOnlyList<IReadOnlyList<object?>> visibleRows = physicalRows
                .Select(row => (IReadOnlyList<object?>)visibleOrdinals.Select(index => row[index]).ToArray())
                .ToArray();
            return new ExecutionResult(visibleColumns, visibleRows, stopwatch.Elapsed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void ValidatePolicy(CompiledQuery query, ApplicationSession session)
    {
        bool boundedResult = query.AppliedLimit is null
            || query.AppliedLimit is > 0 and <= SemanticQueryValidator.MaximumRows;
        bool descriptorMatches = query.Descriptor is { } descriptor
            && descriptor.Grain == query.Grain
            && descriptor.Columns.Select(column => column.Name)
                .SequenceEqual(query.Columns, StringComparer.OrdinalIgnoreCase);
        bool boundedRecordResult = query.Descriptor?.SummaryStrategy is not
                (ResultSummaryStrategy.RecordList or ResultSummaryStrategy.RankedRecords)
            || query.Descriptor.CanBeTruncated
                && query.AppliedLimit is > 0 and <= SemanticQueryValidator.MaximumRows
                && Regex.IsMatch(query.Sql, @"\bLIMIT\s+(?::limit|\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!SourceConnectionClosed
            || query.PolicyProof is not { ReadOnly: true, DepartmentPredicateApplied: true }
            || query.PolicyProof.Department != Department
            || query.PolicyProof.Department != session.AuthorizedDepartment
            || !query.Parameters.TryGetValue("department", out object? value)
            || !string.Equals(value?.ToString(), Department.ToString(), StringComparison.Ordinal)
            || !boundedResult
            || !descriptorMatches
            || !boundedRecordResult
            || Forbidden.IsMatch(query.Sql)
            || !(query.Sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || query.Sql.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The compiled query failed the pre-execution safety gate.");
        }
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string schema = """
            CREATE TABLE Employee (
                EmployeeId INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                Department TEXT NOT NULL CHECK (Department IN ('Sales','Marketing','Engineering')),
                Role TEXT NOT NULL,
                EmploymentStartDate TEXT NOT NULL,
                SalaryAmount REAL NOT NULL,
                YearlyBonusAmount REAL NULL
            );
            CREATE TABLE Certification (
                CertificationId INTEGER PRIMARY KEY,
                EmployeeId INTEGER NOT NULL REFERENCES Employee(EmployeeId),
                CertificationName TEXT NOT NULL,
                DateAchieved TEXT NOT NULL
            );
            CREATE TABLE Benefits (
                BenefitId INTEGER PRIMARY KEY,
                EmployeeId INTEGER NOT NULL REFERENCES Employee(EmployeeId),
                BenefitsPackage TEXT NOT NULL,
                RemainingBalance REAL NOT NULL
            );
            CREATE INDEX IX_Certification_EmployeeId ON Certification(EmployeeId);
            CREATE INDEX IX_Benefits_EmployeeId ON Benefits(EmployeeId);
            """;
        await ExecuteNonQueryAsync(connection, schema, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CopyEmployeesAsync(
        SqliteConnection source,
        SqliteTransaction sourceTransaction,
        SqliteConnection destination,
        SqliteTransaction transaction,
        AuthorizedDepartment department,
        HashSet<long> employeeIds,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand select = source.CreateCommand();
        select.Transaction = sourceTransaction;
        select.CommandText = "SELECT EmployeeId, Name, Department, Role, EmploymentStartDate, SalaryAmount, YearlyBonusAmount FROM Employee WHERE Department = :department ORDER BY EmployeeId";
        select.Parameters.AddWithValue(":department", department.ToString());
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await using SqliteCommand insert = destination.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO Employee VALUES (:id,:name,:department,:role,:start,:salary,:bonus)";
            Add(insert, ":id", reader.GetInt64(0));
            Add(insert, ":name", reader.GetString(1));
            Add(insert, ":department", reader.GetString(2));
            Add(insert, ":role", reader.GetString(3));
            Add(insert, ":start", reader.GetString(4));
            Add(insert, ":salary", reader.GetDouble(5));
            Add(insert, ":bonus", reader.IsDBNull(6) ? null : reader.GetDouble(6));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            employeeIds.Add(reader.GetInt64(0));
            count++;
        }

        return count;
    }

    private static async Task<int> CopyBenefitsAsync(
        SqliteConnection source,
        SqliteTransaction sourceTransaction,
        SqliteConnection destination,
        SqliteTransaction transaction,
        AuthorizedDepartment department,
        CancellationToken cancellationToken)
    {
        const string selectSql = "SELECT b.BenefitId,b.EmployeeId,b.BenefitsPackage,b.RemainingBalance FROM Benefits b JOIN Employee e ON e.EmployeeId=b.EmployeeId WHERE e.Department=:department ORDER BY b.BenefitId";
        return await CopyChildrenAsync(source, sourceTransaction, destination, transaction, department, selectSql, "INSERT INTO Benefits VALUES (:c0,:c1,:c2,:c3)", 4, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CopyCertificationsAsync(
        SqliteConnection source,
        SqliteTransaction sourceTransaction,
        SqliteConnection destination,
        SqliteTransaction transaction,
        AuthorizedDepartment department,
        CancellationToken cancellationToken)
    {
        const string selectSql = "SELECT c.CertificationId,c.EmployeeId,c.CertificationName,c.DateAchieved FROM Certification c JOIN Employee e ON e.EmployeeId=c.EmployeeId WHERE e.Department=:department ORDER BY c.CertificationId";
        return await CopyChildrenAsync(source, sourceTransaction, destination, transaction, department, selectSql, "INSERT INTO Certification VALUES (:c0,:c1,:c2,:c3)", 4, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CopyChildrenAsync(
        SqliteConnection source,
        SqliteTransaction sourceTransaction,
        SqliteConnection destination,
        SqliteTransaction transaction,
        AuthorizedDepartment department,
        string selectSql,
        string insertSql,
        int fieldCount,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand select = source.CreateCommand();
        select.Transaction = sourceTransaction;
        select.CommandText = selectSql;
        select.Parameters.AddWithValue(":department", department.ToString());
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        int count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await using SqliteCommand insert = destination.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = insertSql;
            for (int index = 0; index < fieldCount; index++)
            {
                Add(insert, $":c{index}", reader.IsDBNull(index) ? null : reader.GetValue(index));
            }

            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static async Task VerifyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AuthorizedDepartment department,
        int employees,
        int benefits,
        int certifications,
        CancellationToken cancellationToken)
    {
        int employeeCount = await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM Employee WHERE Department=:department", department, cancellationToken).ConfigureAwait(false);
        int otherCount = await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM Employee WHERE Department<>:department", department, cancellationToken).ConfigureAwait(false);
        int benefitCount = await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM Benefits", department, cancellationToken).ConfigureAwait(false);
        int certificationCount = await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM Certification", department, cancellationToken).ConfigureAwait(false);
        int orphans = await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM pragma_foreign_key_check", department, cancellationToken).ConfigureAwait(false);
        if (employeeCount != employees || benefitCount != benefits || certificationCount != certifications || otherCount != 0 || orphans != 0)
        {
            throw new InvalidOperationException("Department-scoped database verification failed.");
        }
    }

    private static async Task<int> ScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        AuthorizedDepartment department,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (sql.Contains(":department", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue(":department", department.ToString());
        }

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
