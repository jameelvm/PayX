using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using PayX.PspSimulator.Domain;
using PayX.PspSimulator.Services;

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
public sealed class AuthorizationsController(Authorizer authorizer) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Authorize(AuthorizeRequest request, CancellationToken ct)
    {
        var number = request.Card.Number.Replace(" ", "");

        // 1. Card network: is it a real card number, and whose network is it?
        if (!CardNetwork.PassesLuhn(number))
            return Problem(statusCode: 400, title: "invalid_number");
        if (CardNetwork.BrandOf(number) is not { } brand)
            return Problem(statusCode: 400, title: "card_not_supported");

        // 2. Issuer bank: does this cardholder's account allow the hold?
        //    (Approved → a hold is recorded against the card's credit.)
        var auth = await authorizer.AuthorizeAsync(number, brand.ToString(), request.Card.ExpMonth,
            request.Card.ExpYear, request.Amount, request.Currency, request.MerchantId, ct);

        // A decline is a 200, not an error: the request was understood and
        // processed, and the answer was "no". The difference matters to the
        // caller in Phase 6: an error (5xx, timeout) may be worth retrying; a
        // decline never is, because the same card will be declined again.
        // The authorization code is the issuer's proof it said yes, quoted
        // in capture and disputes.
        return Ok(new AuthorizeResponse(auth.Id, auth.Status.ToString().ToLowerInvariant(),
            auth.AuthorizationCode, auth.DeclineCode, auth.Brand, auth.Last4, auth.AmountMinor, auth.Currency));
    }
}
