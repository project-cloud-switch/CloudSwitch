using System.Collections.Concurrent;
using CloudSwitch.Persistence.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class SafeCapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Lines);
    public void Dispose() { }

    private sealed class Capture(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => lines.Enqueue(SafeLogLine.Format(DateTimeOffset.UtcNow, logLevel, category, eventId, state, exception, formatter));
    }
}

public sealed class SafeDiagnosticsTests
{
    private const string Secret = "S3cr3t-Value-9f2c";

    private static Exception Leaky() => new InvalidOperationException(
        $"connect failed: https://admin:{Secret}@db.internal/x?token={Secret}",
        new AggregateException(
            new Exception($"inner {Secret}"),
            new PostgresException($"duplicate key Key (IdempotencyKey)=({Secret})", "ERROR", "ERROR",
                PostgresErrorCodes.UniqueViolation, detail: $"Key=({Secret})", constraintName: "UX_Jobs_ActiveDeployment")));

    [Fact]
    public void Describe_never_contains_message_text_of_outer_inner_or_postgres_exceptions()
    {
        var text = SafeDiagnostics.Describe(Leaky());
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("db.internal", text);
        Assert.DoesNotContain("admin", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("sqlstate=23505", text);
        Assert.Contains("constraint=UX_Jobs_ActiveDeployment", text);
    }

    [Fact]
    public void Formatted_log_lines_drop_exception_text_and_forged_line_breaks()
    {
        var line = SafeLogLine.Format(DateTimeOffset.UnixEpoch, LogLevel.Error, "Test", new EventId(7),
            $"failed\nFAKE LINE {Secret.Length}", Leaky(), (state, _) => state);
        Assert.DoesNotContain(Secret, line);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("error_category=", line);
    }

    [Theory]
    [InlineData(typeof(TimeoutException), SafeDiagnostics.Timeout)]
    [InlineData(typeof(OperationCanceledException), SafeDiagnostics.Canceled)]
    [InlineData(typeof(InvalidOperationException), SafeDiagnostics.Internal)]
    public void Categories_are_stable(Type type, string expected)
        => Assert.Equal(expected, SafeDiagnostics.Categorize((Exception)Activator.CreateInstance(type)!));

    [Fact]
    public void Wrapped_database_failures_are_categorized_by_their_root_cause()
    {
        var failure = new InvalidOperationException("wrapper", new NpgsqlException("connection refused"));
        Assert.Equal(SafeDiagnostics.DatabaseUnavailable, SafeDiagnostics.Categorize(failure));
    }

    [Fact]
    public void Deep_exception_chains_are_bounded()
    {
        var exception = new Exception("0");
        for (var i = 1; i < 200; i++)
            exception = new Exception(i.ToString(), exception);
        Assert.True(SafeDiagnostics.Describe(exception).Length <= 2000);
    }
}