using System.Text.Json;
using System.Text.Json.Nodes;
using CloudSwitch.Contracts.Events;
using MassTransit;
using Xunit;

namespace CloudSwitch.Contracts.Tests;

public class SerializationTests
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static T Read<T>(string name) =>
        JsonSerializer.Deserialize<T>(Fixture(name), JsonOptions)!;

    [Fact]
    public void Deployment_request_preserves_submission_snapshot()
    {
        var request = Read<DeploymentRequested>("deployment-requested.v1.json");

        Assert.Equal(Guid.Parse("33333333-3333-3333-3333-333333333333"), request.JobId);
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), request.CorrelationId);
        Assert.Equal(TimeSpan.Zero, request.OccurredAt.Offset);
        Assert.Equal(new DeploymentSpecification
        {
            Name = "demo", ContainerImage = "nginx:1.27", Environment = "development", Provider = "local"
        }, request.Specification);
        Assert.Equal(request, JsonSerializer.Deserialize<DeploymentRequested>(
            JsonSerializer.Serialize(request, JsonOptions), JsonOptions));
    }

    [Fact]
    public void Status_event_preserves_ordering_and_optional_failure_details()
    {
        var status = Read<JobStatusChanged>("job-status-changed.v1.json");

        Assert.Equal(JobStatus.Running, status.PreviousStatus);
        Assert.Equal(JobStatus.Succeeded, status.Status);
        Assert.Equal(1, status.Attempt);
        Assert.Equal(2, status.Sequence);
        Assert.Null(status.ErrorCode);
        Assert.Null(status.ErrorMessage);

        var failed = status with
        {
            Status = JobStatus.Failed, ErrorCode = "execution_timeout", ErrorMessage = "Execution timed out."
        };
        Assert.Equal(failed, JsonSerializer.Deserialize<JobStatusChanged>(
            JsonSerializer.Serialize(failed, JsonOptions), JsonOptions));
    }

    [Theory]
    [InlineData(JobStatus.Pending, "Pending")]
    [InlineData(JobStatus.Running, "Running")]
    [InlineData(JobStatus.Succeeded, "Succeeded")]
    [InlineData(JobStatus.Failed, "Failed")]
    public void Status_wire_names_are_stable(JobStatus status, string name)
    {
        Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(status, JsonOptions));
        Assert.Equal(status, JsonSerializer.Deserialize<JobStatus>($"\"{name}\"", JsonOptions));
    }

    [Fact]
    public void Unknown_status_name_is_rejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<JobStatus>("\"Cancelled\"", JsonOptions));
    }

    [Theory]
    [InlineData("deployment-requested.v1.json", typeof(DeploymentRequested))]
    [InlineData("job-status-changed.v1.json", typeof(JobStatusChanged))]
    [InlineData("deployment-health-changed.v1.json", typeof(DeploymentHealthChanged))]
    public void Every_required_field_is_required_on_the_wire(string fixture, Type type)
    {
        var original = JsonNode.Parse(Fixture(fixture))!.AsObject();
        foreach (var property in original.Select(x => x.Key).ToArray())
        {
            var missingField = original.DeepClone().AsObject();
            missingField.Remove(property);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(missingField.ToJsonString(), type, JsonOptions));
        }
    }

    [Fact]
    public void New_optional_properties_do_not_break_existing_consumers()
    {
        var original = Read<DeploymentRequested>("deployment-requested.v1.json");
        var additive = JsonNode.Parse(Fixture("deployment-requested.v1.json"))!.AsObject();
        additive["futureOptionalProperty"] = "ignored";
        additive["specification"]!["futureOptionalProperty"] = "ignored";

        Assert.Equal(original, JsonSerializer.Deserialize<DeploymentRequested>(additive.ToJsonString(), JsonOptions));
    }

    [Fact]
    public void Message_type_urns_are_stable()
    {
        Assert.Equal("urn:message:CloudSwitch.Contracts.Events:DeploymentRequested",
            MessageUrn.ForTypeString<DeploymentRequested>());
        Assert.Equal("urn:message:CloudSwitch.Contracts.Events:JobStatusChanged",
            MessageUrn.ForTypeString<JobStatusChanged>());
        Assert.Equal("urn:message:CloudSwitch.Contracts.Events:DeploymentHealthChanged",
            MessageUrn.ForTypeString<DeploymentHealthChanged>());
    }
}
