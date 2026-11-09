using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudSwitch.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CloudSwitchDbContext>
{
    public CloudSwitchDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=55432;Database=cloudswitch;Username=cloudswitch;Password=cloudswitch-dev";
        return new CloudSwitchDbContext(new DbContextOptionsBuilder<CloudSwitchDbContext>().UseNpgsql(connection).Options);
    }
}
