using Microsoft.Extensions.Diagnostics.HealthChecks;
using CloudSwitch.Persistence;

namespace CloudSwitch.Api.Infrastructure;

internal sealed class PostgresHealthCheck(DatabaseReadiness database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return await database.HasSchemaAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database schema is missing; apply migrations before serving traffic.");
    }
}
