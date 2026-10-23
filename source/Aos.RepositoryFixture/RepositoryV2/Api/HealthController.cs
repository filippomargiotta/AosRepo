namespace RepositoryFixture.Api;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public string Get() => "ok";
}
