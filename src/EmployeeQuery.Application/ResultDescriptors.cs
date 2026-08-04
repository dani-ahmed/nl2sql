namespace EmployeeQuery.Application;

public static class ResultDescriptors
{
    public static ResultDescriptor Create(
        QueryPlan plan,
        IReadOnlyList<string> columns,
        bool canBeTruncated)
    {
        ResultColumnDescriptor[] descriptors = columns
            .Select(column => new ResultColumnDescriptor(
                column,
                Humanize(column),
                InferKind(column),
                column.Equals("__AuthorizedEmployeeId", StringComparison.Ordinal)))
            .ToArray();
        ResultSummaryStrategy strategy = plan.Family switch
        {
            PlanFamily.ScalarAggregate => ResultSummaryStrategy.ScalarAggregate,
            PlanFamily.GroupedAggregate => ResultSummaryStrategy.GroupedAggregate,
            PlanFamily.TopRecord => ResultSummaryStrategy.RankedRecords,
            _ => ResultSummaryStrategy.RecordList,
        };
        return new ResultDescriptor(
            plan.Grain,
            descriptors,
            strategy,
            canBeTruncated,
            plan.Family == PlanFamily.TopRecord,
            plan.Family == PlanFamily.GroupedAggregate);
    }

    public static ResultDescriptor VisibleOnly(ResultDescriptor descriptor) => descriptor with
    {
        Columns = descriptor.Columns.Where(column => !column.Hidden).ToArray(),
    };

    private static ResultValueKind InferKind(string name)
    {
        if (name.Contains("Salary", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Bonus", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Balance", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Compensation", StringComparison.OrdinalIgnoreCase))
        {
            return ResultValueKind.Money;
        }
        if (name.Contains("Date", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Start", StringComparison.OrdinalIgnoreCase))
        {
            return ResultValueKind.Date;
        }
        if (name.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Count", StringComparison.OrdinalIgnoreCase))
        {
            return ResultValueKind.WholeNumber;
        }
        if (name.Equals("Value", StringComparison.OrdinalIgnoreCase))
        {
            return ResultValueKind.Number;
        }
        return ResultValueKind.Text;
    }

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
}
