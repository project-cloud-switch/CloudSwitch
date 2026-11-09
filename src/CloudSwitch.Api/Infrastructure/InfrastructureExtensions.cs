using System.ComponentModel.DataAnnotations;
using MassTransit;
using CloudSwitch.Persistence;
using CloudSwitch.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CloudSwitch.Api.Infrastructure;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddApiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection("ConnectionStrings"))
            .ValidateDataAnnotations()
            .Validate(options => options.HasValidConnectionString(), "ConnectionStrings:Postgres must be a valid PostgreSQL connection string with a host.")
            .ValidateOnStart();
        services.AddOptions<RabbitMqOptions>()
            .Bind(configuration.GetSection(RabbitMqOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(provider => NpgsqlDataSource.Create(
            provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Postgres));
        services.AddDbContext<CloudSwitchDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<DeploymentRepository>();
        services.AddScoped<TargetRepository>();
        services.AddScoped<JobRepository>();
        services.AddScoped<UserRepository>();
        services.AddScoped<UnitOfWork>();
        services.AddSingleton<DatabaseReadiness>();
        services.AddOptions<ModuleCatalogOptions>().Bind(configuration.GetSection("Terraform"))
            .Validate(options => HasValidAnnotations(options) && options.Modules.All(HasValidAnnotations)
                && options.Modules.Select(module => (module.Id, module.Provider)).Distinct().Count() == options.Modules.Count,
                "Terraform modules require unique IDs/providers, a source, a pinned revision and an input allowlist.")
            .ValidateOnStart();
        services.AddSingleton<ApprovedModules>();
        services.AddScoped<JobSubmissionService>();

        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        services.AddMassTransit(bus =>
        {
            bus.AddEntityFrameworkOutbox<CloudSwitchDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.UseBusOutbox();
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
            });
            bus.ConfigureHealthCheckOptions(options =>
            {
                options.Tags.Add("ready");
            });
            bus.UsingRabbitMq((context, rabbit) =>
            {
                var options = context.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
                rabbit.Host(options.Host, (ushort)options.Port, options.VirtualHost, host =>
                {
                    host.Username(options.Username);
                    host.Password(options.Password);
                });
            });
        });
        services.Configure<MassTransitHostOptions>(options =>
        {
            options.StopTimeout = TimeSpan.FromSeconds(10);
        });
        return services;
    }

    private static bool HasValidAnnotations(object? value)
        => value is not null && Validator.TryValidateObject(value, new ValidationContext(value),
            validationResults: null, validateAllProperties: true);
}
