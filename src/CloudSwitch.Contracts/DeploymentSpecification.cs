namespace CloudSwitch.Contracts;

/// <summary>A snapshot of the deployment submitted for execution, without credentials.</summary>
public sealed record DeploymentSpecification
{
    public required string Name { get; init; }
    public required string ContainerImage { get; init; }
    public required string Environment { get; init; }
    public required string Provider { get; init; }
}
