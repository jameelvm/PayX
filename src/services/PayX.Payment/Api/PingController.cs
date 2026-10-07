using Microsoft.AspNetCore.Mvc;

namespace PayX.Payment.Api;

/// <summary>
/// Temporary: proves the host, the shared defaults and controller routing are
/// wired together. Replaced by the real payment endpoints in Phase 4.
/// </summary>
[ApiController]
[Route("api/ping")]
public sealed class PingController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { service = "payment", status = "ok" });
}
