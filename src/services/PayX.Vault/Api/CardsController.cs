using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PayX.Vault.Domain;
using PayX.Vault.Services;

namespace PayX.Vault.Api;

// The CVC is accepted here but never stored with the card: it is held
// encrypted in Redis until the first authorization uses it, or its TTL ends
// (4.2a, CvcStore). Unknown fields are still refused rather than ignored, so
// a misspelt field fails loudly instead of being silently dropped.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TokenizeRequest(
    [Required] string Number,
    [Range(1, 12)] int ExpMonth,
    [Range(2000, 2100)] int ExpYear,
    [RegularExpression("^[0-9]{3,4}$")] string? Cvc);

// The PSP's authorize request without the card: Vault adds it.
public sealed record AuthorizeRequest(
    [Range(1, long.MaxValue)] long Amount,
    [Required, RegularExpression("^[A-Z]{3}$")] string Currency,
    [Required] string MerchantId);

// What every other service gets instead of a card number.
public sealed record CardResponse(string Token, string Brand, string Last4, int ExpMonth, int ExpYear)
{
    public static CardResponse From(VaultedCard c) => new(c.Token, c.Brand, c.Last4, c.ExpMonth, c.ExpYear);
}

[ApiController]
[Route("api/vault/cards")]
public sealed class CardsController(CardVault vault, CardAuthorizer authorizer) : ControllerBase
{
    // Money moves, so an Idempotency-Key is required (CLAUDE.md conventions);
    // a missing header is a 400 from model validation.
    [HttpPost("{token}/authorizations")]
    public async Task<IActionResult> Authorize(string token, AuthorizeRequest request,
        [FromHeader(Name = "Idempotency-Key"), Required, StringLength(255)] string idempotencyKey, CancellationToken ct)
    {
        var outcome = await authorizer.AuthorizeAsync(token, request.Amount, request.Currency, request.MerchantId,
            idempotencyKey, ct);
        if (outcome.Answer is not { } answer)
            return Problem(statusCode: outcome.ErrorStatus, title: outcome.Error);

        // Relayed as-is, status included: a decline stays a 200 with
        // status "declined", a PSP 503 stays a 503. The body carries last 4
        // digits only, never the number.
        if (answer.Replayed)
            Response.Headers["Idempotent-Replayed"] = "true";
        return new ContentResult { StatusCode = answer.StatusCode, Content = answer.Body, ContentType = answer.ContentType };
    }

    [HttpPost]
    public async Task<IActionResult> Tokenize(TokenizeRequest request, CancellationToken ct)
    {
        var number = request.Number.Replace(" ", "");
        // The error names the problem, never echoes the number back.
        if (!CardNumber.IsValid(number))
            return Problem(statusCode: 400, title: "invalid_number");

        var card = await vault.TokenizeAsync(number, request.ExpMonth, request.ExpYear, request.Cvc, ct);
        return CreatedAtAction(nameof(Get), new { token = card.Token }, CardResponse.From(card));
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct) =>
        await vault.FindAsync(token, ct) is { } card
            ? Ok(CardResponse.From(card))
            : Problem(statusCode: 404, title: "card_not_found");
}
