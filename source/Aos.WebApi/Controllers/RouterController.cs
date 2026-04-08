using Aos.WebApi.Models;
using Aos.WebApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Aos.WebApi.Controllers;

[ApiController]
[Route("router")]
public sealed class RouterController : ControllerBase
{
    private readonly IRouterService _routerService;

    public RouterController(IRouterService routerService)
    {
        _routerService = routerService;
    }

    [HttpPost("decide")]
    public IActionResult Decide([FromBody] RouterSelectionRequest request)
    {
        var result = _routerService.SelectModel(request);
        return Ok(result);
    }
}
