using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayX.Contracts;
using PayX.Payment.Services;

namespace PayX.Payment.Api;

// Doc ch.2: authorizePayment(amount, card_number, expiration_date, CVV,
// merchant_id). The card details arrive as a Vault token (number, expiry and
// CVC were given to Vault at checkout, 3.7 / 4.2a); the customer is whoever
// the access token says, never a field in the body.
public sealed record AuthorizePaymentRequest([Required] Guid? MerchantId, [Required] AmountRequest? Amount, [Required] string CardToken);

// Plain fields on the way in, not Money: Money's constructor throws on an
// unknown currency, and an exception during JSON reading becomes a 500, not
// a 400 (found in 4.2c). Validated here, converted to Money in the action.
public sealed record AmountRequest(
    [Range(1, Money.MaxAmountMinor)] long AmountMinor,
    [Required, RegularExpression("^[A-Z]{3}$")] string Currency);

// Doc ch.2: capturePayment(authorization_code, amount). The payment id stands
// for the authorization; the amount is optional (default: all of it).
public sealed record CapturePaymentRequest([Range(1, Money.MaxAmountMinor)] long? AmountMinor);

public sealed record PaymentResponse(string Id, string Status, Guid MerchantId, Money Amount, string? AuthorizationCode,
    string? DeclineCode, long? CapturedAmountMinor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static PaymentResponse From(Domain.Payment p) =>
        new(p.Id, p.Status.ToString(), p.MerchantId, p.Amount, p.AuthorizationCode, p.DeclineCode, p.CapturedAmountMinor,
            p.CreatedAt, p.UpdatedAt);
}

[ApiController]
[Route("api/payments")]
public sealed class PaymentsController(PaymentAuthorization authorization, PaymentCapture capture, PaymentQueries queries)
    : ControllerBase
{
    // Doc ch.2: checkPaymentStatus(payment_id). The customer and the merchant
    // of a payment can both see it; for anyone else it doesn't exist (404).
    [HttpGet("{id}"), Authorize(Roles = "Customer,Merchant")]
    public async Task<IActionResult> Get(string id, CancellationToken ct) =>
        await queries.FindVisibleAsync(id, Guid.Parse(User.FindFirst("sub")!.Value), ct) is { } payment
            ? Ok(PaymentResponse.From(payment))
            : Problem(statusCode: 404, title: "payment_not_found");

    // The merchant takes the money, typically when the order ships. Only the
    // payment's own merchant: the check is in PaymentCapture (404 otherwise).
    [HttpPost("{id}/capture"), Authorize(Roles = "Merchant"), Idempotent]
    public async Task<IActionResult> Capture(string id, CapturePaymentRequest? request, CancellationToken ct)
    {
        var merchantId = Guid.Parse(User.FindFirst("sub")!.Value);

        var result = await capture.CaptureAsync(id, merchantId, request?.AmountMinor, ct);
        return result switch
        {
            { Failure: null } => Ok(PaymentResponse.From(result.Payment!)),
            { Payment: null } => Problem(statusCode: 404, title: result.Failure),
            _ => Problem(statusCode: result.FailureStatus, title: result.Failure,
                detail: $"payment {result.Payment.Id} is {result.Payment.Status}"),
        };
    }

    // The Gateway lets Customers and Merchants into /api/payments; inside,
    // Payment decides per endpoint (decision 21): only a customer pays.
    [HttpPost, Authorize(Roles = "Customer"), Idempotent]
    public async Task<IActionResult> Authorize(AuthorizePaymentRequest request, CancellationToken ct)
    {
        var customerId = Guid.Parse(User.FindFirst("sub")!.Value);

        Money amount;
        try
        {
            amount = new Money(request.Amount!.AmountMinor, request.Amount.Currency);
        }
        catch (ArgumentException ex)   // e.g. a well-formed but unsupported currency
        {
            ModelState.AddModelError("amount.currency", ex.Message);
            return ValidationProblem(ModelState);
        }

        var result = await authorization.AuthorizeAsync(customerId, request.MerchantId!.Value, amount,
            request.CardToken, ct);

        if (result.Failure is not null)
            // The payment exists (its id is in the detail) but has no outcome yet.
            return Problem(statusCode: result.FailureStatus is >= 500 ? result.FailureStatus : 422,
                title: result.Failure, detail: $"payment {result.Payment.Id} is {result.Payment.Status}");

        // 201 for every business outcome: Authorized, Declined and RiskBlocked
        // are all a payment that now exists, with an answer. The status field
        // says which; like the PSP, a decline is not an HTTP error. Location
        // points at checkPaymentStatus, and is relative on purpose: behind
        // the Gateway, an absolute URL built here named the internal instance
        // (http://localhost:7082/…) instead of the Gateway (found in 4.4).
        return Created($"/api/payments/{result.Payment.Id}", PaymentResponse.From(result.Payment));
    }
}
