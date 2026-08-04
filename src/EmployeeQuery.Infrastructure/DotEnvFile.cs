using System.Collections.ObjectModel;

namespace EmployeeQuery.Infrastructure;

public sealed record DotEnvLoadResult(string FilePath, IReadOnlyList<string> LoadedKeys);

/// <summary>
/// Minimal dotenv support for local OpenAI development without adding a package.
/// Only the two documented OpenAI settings are accepted; test-mode and database
/// variables can never be injected through this file.
/// </summary>
public static class DotEnvFile
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "OPENAI_API_KEY",
        "OPENAI_MODEL",
    };

    public static DotEnvLoadResult? LoadOpenAiSettings(string? explicitPath = null)
    {
        string? selectedPath = FindFile(explicitPath);
        if (selectedPath is null)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> settings = ParseOpenAiSettings(File.ReadAllText(selectedPath));
        List<string> loaded = new();
        foreach ((string key, string value) in settings)
        {
            // A value explicitly supplied by the process, even an empty value,
            // takes precedence over local developer configuration.
            if (Environment.GetEnvironmentVariable(key) is not null)
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
            loaded.Add(key);
        }

        return new DotEnvLoadResult(selectedPath, loaded.AsReadOnly());
    }

    public static IReadOnlyDictionary<string, string> ParseOpenAiSettings(string content)
    {
        Dictionary<string, string> settings = new(StringComparer.Ordinal);
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line[7..].TrimStart();
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (!AllowedKeys.Contains(key))
            {
                continue;
            }

            if (settings.ContainsKey(key))
            {
                throw new FormatException($"Duplicate {key} entry on line {index + 1} of the dotenv file.");
            }

            string value = ParseValue(line[(separator + 1)..].Trim(), key, index + 1);
            settings.Add(key, value);
        }

        return new ReadOnlyDictionary<string, string>(settings);
    }

    private static string? FindFile(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string requiredPath = Path.GetFullPath(explicitPath);
            return File.Exists(requiredPath)
                ? requiredPath
                : throw new FileNotFoundException("The requested dotenv file does not exist.", requiredPath);
        }

        string[] candidates =
        [
            Path.Combine(Environment.CurrentDirectory, ".env"),
            Path.Combine(AppContext.BaseDirectory, ".env"),
        ];
        return candidates
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static string ParseValue(string value, string key, int lineNumber)
    {
        if (value.Length == 0 || value[0] is not ('\'' or '"'))
        {
            return value;
        }

        char quote = value[0];
        if (value.Length < 2 || value[^1] != quote)
        {
            throw new FormatException($"Unterminated quoted {key} value on line {lineNumber} of the dotenv file.");
        }

        string unquoted = value[1..^1];
        return quote == '"'
            ? unquoted.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : unquoted;
    }
}
