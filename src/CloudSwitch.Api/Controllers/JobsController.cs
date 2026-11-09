using CloudSwitch.Api.Contracts;
using CloudSwitch.Api.Infrastructure;
using CloudSwitch.Api.Services;
using CloudSwitch.Contracts.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CloudSwitch.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/jobs")]
[Tags("Jobs")]
public sealed class JobsController(JobService service) : ControllerBase
{
    [HttpPost("/api/v1/deployments/{deploymentId:guid}/jobs", Name = "SubmitMigration")]
    [EnableRateLimiting(RateLimitingExtensions.JobSubmissionPolicy)]
    [ProducesResponseType<JobResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<JobResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(Guid deploymentId, JobRequest input, CancellationToken cancellationToken)
    {
        var keys = Request.Headers["Idempotency-Key"];
        var key = keys.ToString();
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl))
            throw new ApiRequestException(400, "idempotency_key_required", "Supply an Idempotency-Key header of 1–128 characters.");

        var correlationHeader = Request.Headers["X-Correlation-ID"].ToString();
        var correlationId = Guid.NewGuid();
        if (!string.IsNullOrEmpty(correlationHeader) && (!Guid.TryParse(correlationHeader, out correlationId) || correlationId == Guid.Empty))
            throw new ApiRequestException(400, "invalid_correlation_id", "X-Correlation-ID must be a nonempty UUID.");

        var result = await service.SubmitAsync(deploymentId, input, key, correlationId, cancellationToken);
        var location = $"/api/v1/jobs/{result.Job.Id}";
        Response.Headers.Location = location;
        Response.Headers["X-Correlation-ID"] = result.Job.Request.CorrelationId.ToString();
        return result.Created ? Accepted(location, JobResponse.From(result.Job)) : Ok(JobResponse.From(result.Job));
    }

    [HttpGet(Name = "ListJobs")]
    [ProducesResponseType<List<JobResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(CancellationToken cancellationToken,
        [FromQuery] Guid? deploymentId = null, [FromQuery] int offset = 0, [FromQuery] int limit = 50)
        => Ok(await service.ListAsync(deploymentId, offset, limit, cancellationToken));

    [HttpGet("{id:guid}", Name = "GetJob")]
    [ProducesResponseType<JobResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await service.GetAsync(id, cancellationToken));

    [HttpGet("{id:guid}/events", Name = "ListJobEvents")]
    [ProducesResponseType<List<JobStatusChanged>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Events(Guid id, CancellationToken cancellationToken,
        [FromQuery] long afterSequence = 0, [FromQuery] int limit = 50)
        => Ok(await service.ListEventsAsync(id, afterSequence, limit, cancellationToken));
}
