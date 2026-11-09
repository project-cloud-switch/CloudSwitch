using CloudSwitch.Persistence;
using Domain = CloudSwitch.Persistence.Domain;

namespace CloudSwitch.Api.Contracts;

internal static class ModelMapping
{
    public static Domain.DeploymentDefinition ToDomain(this DeploymentDefinitionDto value)
        => new(value.Environment, value.SourceProvider, value.SourceEndpoint, value.Application.ToDomain(),
            value.HealthCheck.ToDomain(), value.Traffic.ToDomain(), value.Recovery.ToDomain(), value.MonitoringEnabled);

    public static DeploymentDefinitionDto ToDto(this Domain.DeploymentDefinition value)
        => new()
        {
            Environment = value.Environment, SourceProvider = value.SourceProvider, SourceEndpoint = value.SourceEndpoint,
            Application = value.Application.ToContract(), HealthCheck = value.HealthCheck.ToContract(),
            Traffic = value.Traffic.ToContract(), Recovery = value.Recovery.ToContract(), MonitoringEnabled = value.MonitoringEnabled
        };
}
