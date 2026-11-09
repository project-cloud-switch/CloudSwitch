using CloudSwitch.Persistence;
using CloudSwitch.Worker.Consumers;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Recovery;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CloudSwitch.Worker;

public static class DependencyInjection
{
    public static IServiceCollection AddCloudSwitchWorker(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("Worker:Simulation"))
            throw new InvalidOperationException("Real execution adapters are not configured. Local simulation requires Development and Worker:Simulation=true.");
        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        if (string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(connection).Host))
            throw new InvalidOperationException("The PostgreSQL connection requires a host.");
        var rabbit = configuration.GetRequiredSection("RabbitMq").Get<RabbitMqOptions>()
            ?? throw new InvalidOperationException("RabbitMq configuration is required.");
        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection("RabbitMq")).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<WorkerOptions>().Bind(configuration.GetSection("Worker")).ValidateDataAnnotations().ValidateOnStart();
        services.AddDbContext<CloudSwitchDbContext>(options => options.UseNpgsql(connection));
        services.AddSingleton<IInfrastructureExecutor, DeterministicInfrastructureExecutor>();
        services.AddSingleton<ITargetHealthValidator, DeterministicHealthValidator>();
        services.AddSingleton<ITrafficRoutingService, DeterministicTrafficRoutingService>();
        services.AddSingleton<IJobLeaseStore, PostgresJobLeaseStore>();
        services.AddSingleton<IMigrationOrchestrator, MigrationOrchestrator>();
        services.AddHostedService<JobRecoveryService>();
        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(30);
            options.StopTimeout = TimeSpan.FromSeconds(10);
        });
        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<DeploymentRequestedConsumer>();
            bus.AddEntityFrameworkOutbox<CloudSwitchDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });
            bus.UsingRabbitMq((context, transport) =>
            {
                transport.Host(rabbit.Host, (ushort)rabbit.Port, rabbit.VirtualHost, host =>
                {
                    host.Username(rabbit.Username);
                    host.Password(rabbit.Password);
                });
                transport.ReceiveEndpoint("cloudswitch-worker-deployment-requested", endpoint =>
                {
                    endpoint.ConcurrentMessageLimit = 4;
                    endpoint.UseMessageRetry(retry => retry.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
                    endpoint.ConfigureConsumer<DeploymentRequestedConsumer>(context);
                });
            });
        });
        return services;
    }
}
