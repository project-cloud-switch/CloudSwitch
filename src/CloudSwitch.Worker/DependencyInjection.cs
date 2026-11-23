using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Repositories;
using CloudSwitch.Worker.Consumers;
using CloudSwitch.Worker.Doctor;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using CloudSwitch.Worker.Probing;
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
        var isSimulation = configuration.GetValue<bool>("Worker:Simulation");
        if (!isSimulation)
        {
            var vaultAddress = configuration.GetValue<string>("Vault:Address");
            if (string.IsNullOrWhiteSpace(vaultAddress))
            {
                throw new InvalidOperationException(
                    "Real execution adapters are not configured. Real Worker mode requires validated Vault:Address and Terraform configuration, or Worker:Simulation=true.");
            }
        }

        var connection = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        if (string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(connection).Host))
            throw new InvalidOperationException("The PostgreSQL connection requires a host.");

        var rabbit = configuration.GetRequiredSection("RabbitMq").Get<RabbitMqOptions>()
            ?? throw new InvalidOperationException("RabbitMq configuration is required.");

        services.AddOptions<RabbitMqOptions>().Bind(configuration.GetSection("RabbitMq")).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<WorkerOptions>().Bind(configuration.GetSection("Worker")).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<DoctorOptions>().Bind(configuration.GetSection("Doctor"));
        services.AddOptions<HttpHealthProberOptions>().Bind(configuration.GetSection("Probing"));

        services.AddDbContext<CloudSwitchDbContext>(options => options.UseNpgsql(connection));

        // Shared persistence services for Doctor and TrafficGuard reconciliation
        services.AddScoped<DeploymentRepository>();
        services.AddScoped<TargetRepository>();
        services.AddScoped<JobRepository>();
        services.AddScoped<UserRepository>();
        services.AddScoped<UnitOfWork>();
        services.AddOptions<ModuleCatalogOptions>().Bind(configuration.GetSection("Terraform"));
        services.AddSingleton<ApprovedModules>();
        services.AddScoped<JobSubmissionService>();
        services.AddScoped<ITrafficGuardReconciliationService, TrafficGuardReconciliationService>();

        // Probing
        services.AddSingleton<IHttpHealthProber, HttpHealthProber>();

        if (isSimulation)
        {
            services.AddSingleton<IVaultSecretResolver, DeterministicVaultResolver>();
            services.AddSingleton<IInfrastructureExecutor, DeterministicInfrastructureExecutor>();
            services.AddSingleton<ITargetHealthValidator, DeterministicHealthValidator>();
            services.AddSingleton<ITrafficRoutingService, DeterministicTrafficRoutingService>();
        }
        else
        {
            services.AddOptions<VaultOptions>().Bind(configuration.GetSection("Vault")).ValidateDataAnnotations().ValidateOnStart();
            services.AddSingleton<IVaultSecretResolver, ScopedVaultSecretResolver>();
            services.AddSingleton<ITerraformProcessRunner, TerraformProcessRunner>();
            services.AddSingleton<IInfrastructureExecutor, TerraformInfrastructureExecutor>();
            services.AddSingleton<ITargetHealthValidator, HttpTargetHealthValidator>();
            services.AddSingleton<ITrafficRoutingService, DeterministicTrafficRoutingService>();
        }

        services.AddSingleton<IJobLeaseStore, PostgresJobLeaseStore>();
        services.AddSingleton<IMigrationOrchestrator, MigrationOrchestrator>();
        services.AddHostedService<DoctorMonitoringService>();
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
