using System.Text;
using EmployeeQuery.Application;

internal sealed class RecordingConsoleView(IEnumerable<string?> input) : IConsoleView
{
    private readonly Queue<string?> _input = new(input);
    private readonly StringBuilder _output = new();

    public string Output => _output.ToString();

    public void Write(string value) => _output.Append(TerminalText.Escape(value));

    public void WriteLine(string value = "") => _output.AppendLine(TerminalText.Escape(value));

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_input.Count == 0 ? null : _input.Dequeue());
    }
}
