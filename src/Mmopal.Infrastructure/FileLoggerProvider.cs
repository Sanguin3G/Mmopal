using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mmopal.Infrastructure;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(_directory, name));

    public void Dispose() => _loggers.Clear();

    private sealed class FileLogger : ILogger
    {
        private readonly string _directory;
        private readonly string _category;
        private readonly object _gate = new();

        public FileLogger(string directory, string category)
        {
            _directory = directory;
            _category = category;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow,
                level = logLevel.ToString(),
                category = _category,
                eventId = eventId.Id,
                message = formatter(state, exception),
                exception = exception?.ToString()
            };
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            var path = Path.Combine(_directory, $"mmopal-{DateTime.UtcNow:yyyyMMdd}.jsonl");
            lock (_gate)
            {
                File.AppendAllText(path, line);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
