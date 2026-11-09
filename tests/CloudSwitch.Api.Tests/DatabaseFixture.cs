using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using CloudSwitch.Persistence;
using Npgsql;
using Xunit;

namespace CloudSwitch.Api.Tests;

public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLOUDSWITCH_TEST_POSTGRES")))
            Skip = "Set CLOUDSWITCH_TEST_POSTGRES to run against an isolated PostgreSQL test database.";
    }
}

[CollectionDefinition("PostgreSQL")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;

public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly string? admin = Environment.GetEnvironmentVariable("CLOUDSWITCH_TEST_POSTGRES");
    private readonly string databaseName = "cloudswitch_test_" + Guid.NewGuid().ToString("N");
    public string ConnectionString { get; private set; } = "";
    public DatabaseApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(admin))
            return;
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection))
            await create.ExecuteNonQueryAsync();
        var settings = new NpgsqlConnectionStringBuilder(admin) { Database = databaseName };
        ConnectionString = settings.ConnectionString;
        await using var db = CreateDb();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "permissions.sql")));
        Factory = new DatabaseApiFactory(ConnectionString);
    }

    public CloudSwitchDbContext CreateDb() => new(new DbContextOptionsBuilder<CloudSwitchDbContext>()
        .UseNpgsql(ConnectionString).Options);

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(admin))
            return;
        if (Factory is not null)
            await Factory.DisposeAsync();
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }
}

public sealed class DatabaseApiFactory(string connectionString) : ApiFactory
{
    protected override bool UseRealPersistence => true;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString,
                // An unavailable broker proves accepted jobs are durably stored without publishing first.
                ["RabbitMq:Port"] = "65530",
                ["Logging:LogLevel:Default"] = "Critical"
            }));
    }
}
