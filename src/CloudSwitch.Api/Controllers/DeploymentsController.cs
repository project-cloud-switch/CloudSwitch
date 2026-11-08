using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudSwitch.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/deployments")]
[Tags("Deployments")]
public sealed class DeploymentsController(DeploymentService service) : ControllerBase
{
    [HttpPost(Name = "CreateDeployment")]
    [ProducesResponseType<DeploymentResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(DeploymentRequest request, CancellationToken cancellationToken)
    {
        var deployment = await service.CreateAsync(request, cancellationToken);
        ResourceHeaders.ETag(Response, deployment.Revision);
        return Created($"/api/v1/deployments/{deployment.Id}", DeploymentResponse.From(deployment));
    }

    [HttpGet(Name = "ListDeployments")]
    [ProducesResponseType<List<DeploymentResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(CancellationToken cancellationToken, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Ok((await service.ListAsync(offset, limit, cancellationToken)).Select(DeploymentResponse.From));

    [HttpGet("{id:guid}", Name = "GetDeployment")]
    [ProducesResponseType<DeploymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var deployment = await service.GetAsync(id, cancellationToken);
        ResourceHeaders.ETag(Response, deployment.Revision);
        return Ok(DeploymentResponse.From(deployment));
    }

    [HttpPut("{id:guid}", Name = "UpdateDeployment")]
    [ProducesResponseType<DeploymentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Update(Guid id, DeploymentRequest request, CancellationToken cancellationToken)
    {
        var deployment = await service.UpdateAsync(id, request, ResourceHeaders.ExpectedRevision(Request), cancellationToken);
        ResourceHeaders.ETag(Response, deployment.Revision);
        return Ok(DeploymentResponse.From(deployment));
    }

    [HttpDelete("{id:guid}", Name = "DeleteDeployment")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(id, ResourceHeaders.ExpectedRevision(Request), cancellationToken);
        return NoContent();
    }

    [HttpGet("{id:guid}/health", Name = "GetDeploymentHealth")]
    [ProducesResponseType<DeploymentHealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Health(Guid id, CancellationToken cancellationToken)
        => Ok(await service.GetHealthAsync(id, cancellationToken));
}
