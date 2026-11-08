using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CloudSwitch.Persistence;

public static class DeploymentTransactions
{
    // API and Doctor use the same transaction-scoped lock without claiming write ownership of each other's tables.
    public static async Task<IDbContextTransaction> BeginAsync(CloudSwitchDbContext db, Guid deploymentId, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({deploymentId.ToString()}, 0))", ct);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
