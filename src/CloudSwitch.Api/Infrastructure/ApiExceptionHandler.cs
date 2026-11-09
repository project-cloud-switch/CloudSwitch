using CloudSwitch.Persistence;
using CloudSwitch.Api.Services;
using CloudSwitch.Persistence.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CloudSwitch.Api.Infrastructure;

internal sealed class ApiRequestException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken _)
    {
        if (exception is RequestValidationException validation)
        {
            await Results.ValidationProblem(validation.Errors, title: validation.Message,
                extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier })
                .ExecuteAsync(context);
            return true;
        }

        var (status, code, detail) = exception switch
        {
            OperationRejectedException rejected => (
                rejected.Kind switch { RejectionKind.NotFound => 404, RejectionKind.Invalid => 400, _ => 409 }, rejected.Code, rejected.Message),
            ApiRequestException request => (request.Status, request.Code, request.Message),
            DomainException domain => (400, domain.Code, domain.Message),
            DbUpdateConcurrencyException => (409, "configuration_changed", "The configuration changed. Reload it before retrying."),
            DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } }
                => (409, "conflicting_write", "This write conflicts with an existing resource or active job."),
            _ when IsTransientDatabaseError(exception)
                => (503, "database_unavailable", "The database is temporarily unavailable. Try again later."),
            _ => (500, "internal_error", "An unexpected error occurred.")
        };
        if (status >= 500)
            logger.LogError(exception, "API request failed with {ErrorCode}.", code);
        await Results.Problem(statusCode: status, title: code, detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier })
            .ExecuteAsync(context);
        return true;
    }

    private static bool IsTransientDatabaseError(Exception exception)
        => exception is NpgsqlException { IsTransient: true }
            || exception is DbUpdateException or InvalidOperationException
                && exception.InnerException is { } inner && IsTransientDatabaseError(inner);
}
