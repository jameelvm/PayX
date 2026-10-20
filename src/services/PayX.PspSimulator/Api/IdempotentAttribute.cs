using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PayX.PspSimulator.Domain;
using PayX.PspSimulator.Repositories;

namespace PayX.PspSimulator.Api;

/// <summary>
/// Marks an endpoint as requiring an <c>Idempotency-Key</c> header, the way
/// Stripe and Adyen take one (Stripe makes it optional; here it is required,
/// so a caller can't forget it on a money-moving call).
/// </summary>
public sealed class IdempotentAttribute() : TypeFilterAttribute(typeof(IdempotencyFilter));

public sealed class IdempotencyFilter(PspDbContext db, IConfiguration config, IOptions<JsonOptions> json)
    : IAsyncActionFilter
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

        var arguments = context.ActionArguments.Where(a => a.Value is not CancellationToken).ToDictionary();
        var requestHash = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(config["Psp:FingerprintKey"]!),
            Encoding.UTF8.GetBytes($"{http.Request.Method} {http.Request.Path} {JsonSerializer.Serialize(arguments)}")));

        // One transaction around everything: the key, the work (a hold, a
        // capture…) and the stored answer commit together or not at all. If
        // the process dies half way, nothing is kept, and the retry simply
        // does the work for the first time. Separate commits would allow
        // "work done, key not saved", and the retry would do it twice.
        await using var tx = await db.Database.BeginTransactionAsync(http.RequestAborted);

        // Claim the key first. If another request with the same key is still
        // running, Postgres makes this INSERT wait on the unique index until
        // that one commits, then fails it: the duplicate never runs the work.
        var record = new IdempotencyRecord(key, requestHash);
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync();
            db.ChangeTracker.Clear();
            var original = await db.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.Key == key);

            context.Result = original.RequestHash != requestHash
                // Same key, different request: a client bug. Replaying the
                // old answer would be wrong, and so would processing it.
                ? Problem(422, "idempotency_key_reused_with_different_request")
                : Replay(original, http);
            return;
        }

        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;   // tx disposed without commit → everything rolled back; a retry runs afresh

        // Store whatever the endpoint answered: success, decline, 404, 409.
        // A retry must see the same answer, including the same refusal.
        if (executed.Result is ObjectResult result)
        {
            record.Complete(result.StatusCode ?? 200, JsonSerializer.Serialize(result.Value, json.Value.JsonSerializerOptions));
            await db.SaveChangesAsync(http.RequestAborted);
            await tx.CommitAsync(http.RequestAborted);
        }
    }

    private static ContentResult Replay(IdempotencyRecord original, HttpContext http)
    {
        // The header Stripe uses, so a caller (and you, reading curl output)
        // can tell "this already happened" from "this just happened".
        http.Response.Headers["Idempotent-Replayed"] = "true";
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
