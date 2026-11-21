using Microsoft.EntityFrameworkCore.Storage;

namespace CloudSwitch.Persistence;

public interface IPersistenceTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}

public sealed class UnitOfWork(CloudSwitchDbContext db)
{
    public CloudSwitchDbContext Db => db;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        => db.SaveChangesAsync(cancellationToken);

    public Task<IPersistenceTransaction> BeginDeploymentTransactionAsync(
        Guid deploymentId, CancellationToken cancellationToken)
        => BeginDeploymentTransactionAsync(deploymentId, null, cancellationToken);

    public async Task<IPersistenceTransaction> BeginDeploymentTransactionAsync(
        Guid deploymentId, string? normalizedRecord, CancellationToken cancellationToken)
        => new PersistenceTransaction(await DeploymentTransactions.BeginAsync(db, deploymentId, normalizedRecord, cancellationToken));

    private sealed class PersistenceTransaction(IDbContextTransaction transaction) : IPersistenceTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
            => transaction.CommitAsync(cancellationToken);

        public ValueTask DisposeAsync()
            => transaction.DisposeAsync();
    }
}
