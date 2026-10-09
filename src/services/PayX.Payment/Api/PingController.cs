using Microsoft.AspNetCore.Mvc;

namespace PayX.Payment.Api;

/// <summary>
/// Temporary: proves the host, the shared defaults and controller routing are
/// wired together, and (from Module 1.6) that the Gateway reaches this
/// service. Reports which instance answered so load balancing is visible.
/// Replaced by the real payment endpoints in Phase 4.
/// </summary>
[ApiController]
[Route("api/payments/ping")]
public sealed class PingController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new
    {
        service = "payment",
        instance = $"{Request.Host.Port}",
        status = "ok",
    });
}
