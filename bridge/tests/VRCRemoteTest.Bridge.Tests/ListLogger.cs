using Microsoft.Extensions.Logging;

namespace VRCRemoteTest.Bridge.Tests;

/// <summary>Records log entries so tests can assert on levels and on whether an exception (stack trace) was attached.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
