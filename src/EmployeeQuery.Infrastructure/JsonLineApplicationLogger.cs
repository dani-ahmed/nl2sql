using System.Text.Json;
using System.Text.Json.Serialization;
using EmployeeQuery.Application;

namespace EmployeeQuery.Infrastructure;

public sealed class JsonLineApplicationLogger(TextWriter writer) : IApplicationLogger
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly object _gate = new();

    public void Log(ApplicationLogEvent entry)
    {
        // Event properties are deliberately metadata-only. Callers must never add
        // API keys, authorization headers, prompts, SQL, parameters, or result rows.
        string json = JsonSerializer.Serialize(entry, Options);
        lock (_gate)
        {
            writer.WriteLine(json);
            writer.Flush();
        }
    }
}
