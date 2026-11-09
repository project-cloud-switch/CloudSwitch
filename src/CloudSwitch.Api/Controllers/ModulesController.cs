using CloudSwitch.Api.Services;
using CloudSwitch.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CloudSwitch.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/modules")]
[Tags("Modules")]
public sealed class ModulesController(TargetService service) : ControllerBase
{
    [HttpGet(Name = "ListApprovedModules")]
    [ProducesResponseType<IReadOnlyList<ApprovedModule>>(StatusCodes.Status200OK)]
    public IActionResult List() => Ok(service.ListModules());
}
