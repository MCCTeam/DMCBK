using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DMCBK.Samples.HeadlessClient;

// ----------------------------------------------------------------------------------------------------------- A dependency-free ILoggerFactory that writes to System.Console. DMCBK.Core takes an ILoggerFactory (it never touches Console itself), so an embedding host supplies one.
// A real worker would instead plug in Microsoft.Extensions.Logging.Console, Serilog, or OpenTelemetry here; this tiny implementation keeps the sample's dependency closure to DMCBK.Core + the BCL, which is precisely what the embeddability proof is about. -----------------------------------------------------------------------------------------------------------

/// <summary>A minimal console <see cref="ILoggerFactory"/> filtering below <paramref name="minLevel"/>.</summary>
internal sealed class ConsoleLoggerFactory(LogLevel minLevel) : ILoggerFactory
{
    private readonly ConcurrentDictionary<string, ConsoleLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new ConsoleLogger(name, minLevel));

    public void AddProvider(ILoggerProvider provider)
    {
        // Single fixed provider (the console); external providers are out of scope for the sample.
    }

    public void Dispose()
    {
    }

    private sealed class ConsoleLogger(string category, LogLevel minLevel) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            Console.WriteLine($"{Abbreviate(logLevel)} [{category}] {message}");
            if (exception is not null)
                Console.WriteLine(exception);
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "----",
        };
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
