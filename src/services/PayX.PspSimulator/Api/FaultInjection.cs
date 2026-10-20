using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PayX.PspSimulator.Api;

/// <summary>
/// Lets a caller make one request misbehave on purpose, with an
/// <c>X-Psp-Fault</c> header, so PayX's handling of the doc's "transient
/// failures" (ch.2: retries, timeouts, idempotency) can be demonstrated on
/// demand instead of waiting for a real network to fail. Values, comma
/// separated: <c>latency=2000</c>, <c>error=503</c>, <c>timeout-after-success</c>.
/// Only honoured when <c>Psp:FaultInjection:Enabled</c> is true (Development).
/// </summary>
public sealed class FaultInjectableAttribute : TypeFilterAttribute
{
    // Lower order = outer filter. Running before [Idempotent] is what makes
    // an injected 5xx a clean "nothing happened": no key claimed, no work.
    public FaultInjectableAttribute() : base(typeof(FaultInjectionFilter)) => Order = -1;
}

public sealed record Faults(int LatencyMs, int? ErrorStatus, bool TimeoutAfterSuccess)
{
    public static Faults? Parse(string header)
    {
        var faults = new Faults(0, null, false);
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            faults = part.Split('=', 2) switch
            {
                ["latency", var ms] when int.TryParse(ms, out var v) && v is > 0 and <= 60_000 => faults with { LatencyMs = v },
                ["error", var code] when int.TryParse(code, out var v) && v is >= 500 and <= 599 => faults with { ErrorStatus = v },
                ["timeout-after-success"] => faults with { TimeoutAfterSuccess = true },
                _ => null,
            };
            if (faults is null)
                return null;
        }
        return faults;
    }
}

public sealed class FaultInjectionFilter(IConfiguration config, ILogger<FaultInjectionFilter> logger)
    : IAsyncActionFilter, IAsyncResultFilter
{
    // A type filter that isn't reusable is created once per request, so this
    // carries the parsed header from the action stage to the result stage.
    private Faults? _faults;

    // Stage 1, before [Idempotent]: delay, or fail without doing anything.
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var header = context.HttpContext.Request.Headers["X-Psp-Fault"].ToString();
        if (header.Length == 0 || !config.GetValue<bool>("Psp:FaultInjection:Enabled"))
        {
            await next();
            return;
        }

        _faults = Faults.Parse(header);
        if (_faults is null)
        {
            context.Result = Problem(400, "invalid_fault");
            return;
        }
        logger.LogWarning("Injecting fault {Faults} on {Method} {Path}", header,
            context.HttpContext.Request.Method, context.HttpContext.Request.Path);

        if (_faults.LatencyMs > 0)
            await Task.Delay(_faults.LatencyMs, context.HttpContext.RequestAborted);

        // The provider fell over before touching the request (overloaded,
        // deploying, a load balancer with no healthy node). Safe to retry,
        // with the same key or a new one: nothing was done.
        if (_faults.ErrorStatus is { } status)
        {
            context.Result = Problem(status, "injected_fault");
            return;
        }

        await next();
    }

    // Stage 2, after [Idempotent] has committed: the work is done and stored,
    // but the answer never arrives. This is the doc's hard case: the caller
    // sees a timeout and can't tell whether the customer was charged. Applies
    // whatever the answer was (approval, decline, 409): what's lost is the reply.
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (_faults is not { TimeoutAfterSuccess: true })
        {
            await next();
            return;
        }

        // Hang until the caller gives up (its timeout fires), capped at 60 s,
        // then drop the connection without writing a byte.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), context.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException)
        {
        }
        context.HttpContext.Abort();
    }

    private static ObjectResult Problem(int status, string title) =>
        new(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
}
