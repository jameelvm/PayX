using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using PayX.Payment.Services;

namespace PayX.Payment.Api;

/// <summary>
/// Requires an <c>Idempotency-Key</c> on a money-moving endpoint (CLAUDE.md
/// conventions) and makes retries with it safe: the first request runs, every
/// later one gets the first one's answer.
/// </summary>
public sealed class IdempotentAttribute() : TypeFilterAttribute(typeof(IdempotencyFilter));

public sealed class IdempotencyFilter(IdempotencyService idempotency, IOptions<JsonOptions> json) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length is 0 or > 255)
        {
            context.Result = Problem(400, "idempotency_key_required");
            return;
        }

        // Keys belong to the caller: the token's sub (authentication ran first).
        var scope = http.User.FindFirst("sub")!.Value;
        var arguments = context.ActionArguments.Where(a => a.Value is not CancellationToken).ToDictionary();
        var requestHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{http.Request.Method} {http.Request.Path} {JsonSerializer.Serialize(arguments)}")));

        var claim = await idempotency.ClaimAsync(scope, key, requestHash, http.RequestAborted);
        switch (claim.Outcome)
        {
            case ClaimOutcome.Mismatch:
                context.Result = Problem(422, "idempotency_key_reused_with_different_request");
                return;
            case ClaimOutcome.InProgress:
                // The first request is still running (or died: 4.6). Not
                // processed again; the caller retries in a moment.
                http.Response.Headers.RetryAfter = "1";
                context.Result = Problem(409, "request_in_progress");
                return;
            case ClaimOutcome.Replay:
                context.Result = Replay(claim.Record!, http);
                return;
        }

        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;   // key stays in progress (see 4.6)

        // Store definitive answers only: success, decline, 4xx refusals.
        // A 5xx means "no outcome yet" (Vault or the PSP failed or timed out),
        // and replaying it forever would stop the caller ever finding out
        // what happened: the 3.5 lesson. Those keys stay in progress (4.6).
        if (executed.Result is ObjectResult { StatusCode: < 500 } result)
        {
            var location = (result as CreatedResult)?.Location;
            await idempotency.CompleteAsync(claim.Record!, result.StatusCode ?? 200,
                JsonSerializer.Serialize(result.Value, json.Value.JsonSerializerOptions), location, http.RequestAborted);
        }
    }

    private static ContentResult Replay(Domain.IdempotencyRecord original, HttpContext http)
    {
        http.Response.Headers["Idempotent-Replayed"] = "true";
        if (original.ResponseLocation is { } location)
            http.Response.Headers.Location = location;
        return new ContentResult
        {
            StatusCode = original.ResponseStatus,
            Content = original.ResponseBody,
            ContentType = original.ResponseStatus >= 400 ? "application/problem+json" : "application/json",
        };
    }

    private static ObjectResult Problem(int status, string title) =>
        new(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
}
