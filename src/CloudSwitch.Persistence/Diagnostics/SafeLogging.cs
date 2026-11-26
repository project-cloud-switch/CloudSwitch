using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace CloudSwitch.Persistence.Diagnostics;

public static class SafeLogLine
{
    public static string Format<TState>(DateTimeOffset time, LogLevel level, string category, EventId eventId,
        TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        // The formatter is invoked without the exception, so its text can never be mixed into the message.
        var line = $"{time:O} {level} {category}[{eventId.Id}] {SafeDiagnostics.Clean(formatter(state, null))}";
        if (Activity.Current is { } activity)
            line += $" trace={activity.TraceId}";
        if (exception is not null)
            line += $" error_category={SafeDiagnostics.Categorize(exception)} error={SafeDiagnostics.Describe(exception)}";
        return line;
    }
}

public sealed class SafeConsoleFormatter() : ConsoleFormatter(Name)
{
    public new const string Name = "safe";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        if (logEntry.Formatter is null)
            return;
        textWriter.WriteLine(SafeLogLine.Format(DateTimeOffset.UtcNow, logEntry.LogLevel, logEntry.Category,
            logEntry.EventId, logEntry.State, logEntry.Exception, logEntry.Formatter));
    }
}

public static class SafeLoggingExtensions
{
    public static ILoggingBuilder AddSafeConsole(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsoleFormatter<SafeConsoleFormatter, ConsoleFormatterOptions>();
        logging.AddConsole(options => options.FormatterName = SafeConsoleFormatter.Name);
        return logging;
    }
}