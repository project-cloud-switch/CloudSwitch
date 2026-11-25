using CloudSwitch.Persistence;
using Domain = CloudSwitch.Persistence.Domain;
using SourceResource = CloudSwitch.Persistence.Domain.SourceResource;

namespace CloudSwitch.Api.Contracts;

internal static class ModelMapping
{
    public static Domain.DeploymentDefinition ToDomain(this DeploymentDefinitionDto value)
        => new(value.Environment, value.SourceProvider, value.SourceEndpoint, value.Application.ToDomain(),
            value.HealthCheck.ToDomain(), value.Traffic.ToDomain(), value.Recovery.ToDomain(), value.MonitoringEnabled, 
            sourceResource: value.SourceResource is { } ? new SourceResource(value.SourceResource!.Type, value.SourceResource.FolderId, value.SourceResource.ResourceId) : null);

    public static DeploymentDefinitionDto ToDto(this Domain.DeploymentDefinition value)
        => new()
        {
            Environment = value.Environment, SourceProvider = value.SourceProvider, SourceEndpoint = value.SourceEndpoint,
            Application = value.Application.ToContract(), HealthCheck = value.HealthCheck.ToContract(),
            Traffic = value.Traffic.ToContract(), Recovery = value.Recovery.ToContract(), MonitoringEnabled = value.MonitoringEnabled,
            SourceResource = value.SourceResource is { } r ? new SourceResourceDto { Type = r.Type, FolderId = r.FolderId, ResourceId = r.ResourceId } : null
        };
}
