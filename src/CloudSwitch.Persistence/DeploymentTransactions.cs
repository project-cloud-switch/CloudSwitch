using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CloudSwitch.Persistence;

public static class DeploymentTransactions
{
    // API and Doctor use the same transaction-scoped lock without claiming write ownership of each other's tables.
    public static Task<IDbContextTransaction> BeginAsync(CloudSwitchDbContext db, Guid deploymentId, CancellationToken ct)
        => BeginAsync(db, deploymentId, null, ct);

    public static async Task<IDbContextTransaction> BeginAsync(
        CloudSwitchDbContext db,
        Guid deploymentId,
        string? normalizedRecord,
        CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (db.Database.IsNpgsql())
            {
                if (string.IsNullOrWhiteSpace(normalizedRecord))
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({deploymentId.ToString()}, 0))", ct);
                }
                else
                {
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(k) FROM (SELECT DISTINCT unnest(ARRAY[hashtextextended({deploymentId.ToString()}, 0), hashtextextended({normalizedRecord}, 0)]) AS k ORDER BY k) t", ct);
                }
            }
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
