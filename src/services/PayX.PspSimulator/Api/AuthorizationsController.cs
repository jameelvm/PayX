using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using PayX.PspSimulator.Domain;

namespace PayX.PspSimulator.Api;

// Doc ch.2: authorizePayment(amount, card_number, expiration_date, CVV, merchant_id).
// Amount is an integer in minor units plus a currency, the way real PSPs
// (Stripe, Adyen) take it.
public sealed record AuthorizeRequest(
    [Range(1, long.MaxValue)] long Amount,
    [Required, RegularExpression("^[A-Z]{3}$")] string Currency,
    [Required] CardDetails Card,
    [Required] string MerchantId);

public sealed record CardDetails(
    [Required] string Number,
    [Range(1, 12)] int ExpMonth,
    [Range(2000, 2100)] int ExpYear,
    [Required, RegularExpression("^[0-9]{3,4}$")] string Cvc);

// Never the full card number back: last 4 digits are enough to show
// "Visa •••• 4242" and useless to a thief.
public sealed record AuthorizeResponse(
    string AuthorizationId, string Status, string? AuthorizationCode, string? DeclineCode,
    string Brand, string Last4, long Amount, string Currency);

[ApiController]
[Route("api/psp/authorizations")]
public sealed class AuthorizationsController : ControllerBase
{
    [HttpPost]
    public IActionResult Authorize(AuthorizeRequest request)
    {
        var number = request.Card.Number.Replace(" ", "");

        // 1. Card network: is it a real card number, and whose network is it?
        if (!CardNetwork.PassesLuhn(number))
            return Problem(statusCode: 400, title: "invalid_number");
        if (CardNetwork.BrandOf(number) is not { } brand)
            return Problem(statusCode: 400, title: "card_not_supported");

        // 2. Issuer bank: does this cardholder's account allow the hold?
        var declineCode = IssuerBank.Decide(number, request.Card.ExpMonth, request.Card.ExpYear,
            request.Amount, DateOnly.FromDateTime(DateTime.UtcNow));

        // A decline is a 200, not an error: the request was understood and
        // processed, and the answer was "no". The difference matters to the
        // caller in Phase 6: an error (5xx, timeout) may be worth retrying; a
        // decline never is, because the same card will be declined again.
        return Ok(new AuthorizeResponse(
            AuthorizationId: $"auth_{Guid.CreateVersion7():N}",
            Status: declineCode is null ? "authorized" : "declined",
            // The issuer's approval code: proof the bank said yes, quoted in
            // capture and disputes. Real codes are 6 alphanumeric characters.
            AuthorizationCode: declineCode is null ? RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 6) : null,
            DeclineCode: declineCode,
            Brand: brand.ToString(),
            Last4: number[^4..],
            Amount: request.Amount,
            Currency: request.Currency));
    }
}
