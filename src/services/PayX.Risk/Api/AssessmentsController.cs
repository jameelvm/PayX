using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PayX.Contracts;
using PayX.Risk.Domain;
using PayX.Risk.Repositories;

namespace PayX.Risk.Api;

// Plain fields on the way in, converted to Money in the action: Money's
// constructor throws on an unknown currency, and a throw during JSON reading
// is a 500, not a 400 (same as Payment, found in 4.2c).
public sealed record AssessmentRequest(
    [Required] string PaymentId,
    [Required] Guid? CustomerId,
    [Range(1, Money.MaxAmountMinor)] long AmountMinor,
    [Required, RegularExpression("^[A-Z]{3}$")] string Currency,
    [RegularExpression("^[A-Z]{2}$")] string? CardCountry);

public sealed record AssessmentResponse(string PaymentId, int Score, IReadOnlyList<RiskReason> Reasons);

/// <summary>
/// "How risky is this payment?" Called by Payment before it asks for any
/// money (5.5). Internal only: no Gateway route, a customer never scores
/// their own payment.
/// </summary>
[ApiController]
[Route("api/risk/assessments")]
public sealed class AssessmentsController(IOptionsMonitor<RiskRulesSettings> rules, SlidingWindowCounter velocity)
    : ControllerBase
{
    // 200, not 201: nothing is stored but the velocity entry; the answer is
    // a computation. IOptionsMonitor, not IOptions: a threshold edited in
    // appsettings.json applies to the next request, without a restart.
    [HttpPost]
    public async Task<IActionResult> Assess(AssessmentRequest request)
    {
        Money amount;
        try
        {
            amount = new Money(request.AmountMinor, request.Currency);
        }
        catch (ArgumentException ex)   // well-formed but unsupported currency
        {
            ModelState.AddModelError(nameof(request.Currency), ex.Message);
            return ValidationProblem(ModelState);
        }

        var settings = rules.CurrentValue;

        // Signals first (I/O), then the rules (pure). Safe to repeat for the
        // same payment: the counter keys on the payment id.
        var recent = await velocity.RecordAsync($"customer:{request.CustomerId}", request.PaymentId,
            settings.Velocity.Window);

        var score = RiskRules.Score(new RiskInput(request.PaymentId, amount, request.CardCountry, recent), settings);
        return Ok(new AssessmentResponse(request.PaymentId, score.Score, score.Reasons));
    }
}
