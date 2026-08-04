namespace EmployeeQuery.Application;

public interface IConsoleView
{
    void Write(string value);

    void WriteLine(string value = "");

    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);
}

public static class TerminalText
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // The dependency-free console does not interpret markup. Removing control
        // characters also prevents ANSI/control-sequence injection if values come
        // from the model, user, or database.
        return new string(value.Where(character =>
            character is '\t' || !char.IsControl(character)).ToArray());
    }
}
