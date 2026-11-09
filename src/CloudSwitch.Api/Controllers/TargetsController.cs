using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudSwitch.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/deployments/{deploymentId:guid}/targets")]
[Tags("Targets")]
public sealed class TargetsController(TargetService service) : ControllerBase
{
    [HttpPost(Name = "CreateTarget")]
    [ProducesResponseType<TargetResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid deploymentId, TargetRequest request, CancellationToken cancellationToken)
    {
        var target = await service.CreateAsync(deploymentId, request, cancellationToken);
        ResourceHeaders.ETag(Response, target.Revision);
        return Created($"/api/v1/deployments/{deploymentId}/targets/{target.Id}", TargetResponse.From(target));
    }

    [HttpGet(Name = "ListTargets")]
    [ProducesResponseType<List<TargetResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid deploymentId, CancellationToken cancellationToken,
        [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Ok((await service.ListAsync(deploymentId, offset, limit, cancellationToken)).Select(TargetResponse.From));

    [HttpGet("{id:guid}", Name = "GetTarget")]
    [ProducesResponseType<TargetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid deploymentId, Guid id, CancellationToken cancellationToken)
    {
        var target = await service.GetAsync(deploymentId, id, cancellationToken);
        ResourceHeaders.ETag(Response, target.Revision);
        return Ok(TargetResponse.From(target));
    }

    [HttpPut("{id:guid}", Name = "UpdateTarget")]
    [ProducesResponseType<TargetResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HttpValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Update(Guid deploymentId, Guid id, TargetRequest request, CancellationToken cancellationToken)
    {
        var target = await service.UpdateAsync(deploymentId, id, request, ResourceHeaders.ExpectedRevision(Request), cancellationToken);
        ResourceHeaders.ETag(Response, target.Revision);
        return Ok(TargetResponse.From(target));
    }

    [HttpDelete("{id:guid}", Name = "DeleteTarget")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status428PreconditionRequired)]
    public async Task<IActionResult> Delete(Guid deploymentId, Guid id, CancellationToken cancellationToken)
    {
        await service.DeleteAsync(deploymentId, id, ResourceHeaders.ExpectedRevision(Request), cancellationToken);
        return NoContent();
    }
}
