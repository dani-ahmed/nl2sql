using EmployeeQuery.Application;

namespace EmployeeQuery.ConsoleHost;

internal sealed class SystemConsoleView : IConsoleView
{
    public void Write(string value) => Console.Write(TerminalText.Escape(value));

    public void WriteLine(string value = "") => Console.WriteLine(TerminalText.Escape(value));

    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
}

internal sealed class ConsoleApplication(Func<CancellationToken, Task> run)
{
    public Task RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
}

internal sealed class ApplicationHost(ConsoleApplication application) : IAsyncDisposable
{
    private bool _started;

    public ConsoleApplication Application => _started
        ? application
        : throw new InvalidOperationException("The application host has not started.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _started = false;
        return ValueTask.CompletedTask;
    }
}
