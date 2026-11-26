using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloudSwitch.Persistence.Diagnostics;

public static class SafeDiagnostics
{
    public const string Validation = "validation";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string DatabaseUnavailable = "database_unavailable";
    public const string BrokerUnavailable = "broker_unavailable";
    public const string Timeout = "timeout";
    public const string Canceled = "canceled";
    public const string Internal = "internal_error";

    public static string Categorize(Exception exception)
    {
        foreach (var current in Chain(exception))
        {
            var category = current switch
            {
                OperationRejectedException { Kind: RejectionKind.NotFound } => NotFound,
                OperationRejectedException { Kind: RejectionKind.Invalid } => Validation,
                OperationRejectedException { Kind: RejectionKind.Conflict } => Conflict,
                DbUpdateConcurrencyException => Conflict,
                PostgresException { IsTransient: false } => null,
                PostgresException or NpgsqlException => DatabaseUnavailable,
                TimeoutException => Timeout,
                OperationCanceledException => Canceled,
                _ when IsBroker(current) => BrokerUnavailable,
                _ => null
            };
            if (category is not null)
                return category;
        }
        return Internal;
    }

    public static string Describe(Exception exception, int maxDepth = 6, int stackLines = 8)
    {
        var builder = new StringBuilder();
        var depth = 0;
        foreach (var current in Chain(exception).Take(maxDepth))
        {
            if (depth++ > 0)
                builder.Append(" <- ");
            builder.Append(current.GetType().FullName);
            switch (current)
            {
                case PostgresException postgres:
                    builder.Append("[sqlstate=").Append(Token(postgres.SqlState));
                    if (postgres.ConstraintName is { } constraint)
                        builder.Append(",constraint=").Append(Token(constraint));
                    builder.Append(']');
                    break;
                case HttpRequestException { StatusCode: { } status }:
                    builder.Append("[status=").Append((int)status).Append(']');
                    break;
                case SocketException socket:
                    builder.Append("[socket=").Append(socket.SocketErrorCode).Append(']');
                    break;
            }
        }
        // Stack frames contain method and parameter type names only, never argument values.
        var frames = (exception.StackTrace ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(stackLines).Select(Token);
        var stack = string.Join(" | ", frames);
        if (stack.Length > 0)
            builder.Append(" stack: ").Append(stack);
        return Truncate(builder.ToString(), 2000);
    }

    public static string Clean(string? value)
        => Truncate(new string((value ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()), 2000);

    private static string Token(string? value) => Clean(value);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static bool IsBroker(Exception exception)
    {
        var name = exception.GetType().FullName ?? "";
        return name.StartsWith("RabbitMQ.Client.", StringComparison.Ordinal)
            || name.StartsWith("MassTransit.", StringComparison.Ordinal);
    }

    private static IEnumerable<Exception> Chain(Exception root)
    {
        var pending = new Queue<Exception>();
        pending.Enqueue(root);
        var visited = 0;
        while (pending.Count > 0 && visited++ < 32)
        {
            var current = pending.Dequeue();
            yield return current;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Enqueue(inner);
            else if (current.InnerException is { } next)
                pending.Enqueue(next);
        }
    }
}