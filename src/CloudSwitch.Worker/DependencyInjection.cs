using CloudSwitch.Worker.Consumers;
using CloudSwitch.Worker.Infrastructure;
using CloudSwitch.Worker.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace CloudSwitch.Worker;

public static class DependencyInjection
{
    public static IServiceCollection AddCloudSwitchWorker(
        this IServiceCollection services,
        bool useDeterministicAdapters = true)
    {
        if (useDeterministicAdapters)
        {
            services.AddSingleton<IInfrastructureExecutor, DeterministicInfrastructureExecutor>();
            services.AddSingleton<ITargetHealthValidator, DeterministicHealthValidator>();
            services.AddSingleton<ITrafficRoutingService, DeterministicTrafficRoutingService>();
        }

        services.AddScoped<IMigrationOrchestrator, MigrationOrchestrator>();
        services.AddScoped<DeploymentRequestedConsumer>();

        return services;
    }
}
