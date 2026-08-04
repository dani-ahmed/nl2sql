using System.Security.Cryptography;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

public sealed class RandomDepartmentSelector : IDepartmentSelector
{
    private static readonly Department[] Values = [Department.Sales, Department.Marketing, Department.Engineering];

    public AuthorizedDepartment SelectDepartment() => new(Values[RandomNumberGenerator.GetInt32(Values.Length)]);
}
