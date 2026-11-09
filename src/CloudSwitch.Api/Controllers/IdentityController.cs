using CloudSwitch.Api.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudSwitch.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/me")]
[Tags("Identity")]
public sealed class IdentityController(ICurrentAccount account) : ControllerBase
{
    [HttpGet(Name = "GetCurrentIdentity")]
    [ProducesResponseType<Account>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
        => Ok(await account.GetAsync(cancellationToken));
}
