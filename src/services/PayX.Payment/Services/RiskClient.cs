using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Polly.Timeout;

namespace PayX.Payment.Services;

/// <summary>
/// Payment's side of the risk check (doc ch.2: payment service → fraud
/// detection + risk check → payment gateway). Gathers what Risk needs that
/// Payment doesn't hold itself, asks Risk, and hands back its decision.
///
/// The card's country comes from Vault, not from the client: a value the
/// browser sends is a value a fraudster chooses ("US", of course).
/// </summary>
public sealed class RiskClient(HttpClient http, VaultClient vault) : IRiskCheck
{
    // Risk sends its decision as text ("Challenge"), like every PayX enum.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<RiskCheckResult> CheckAsync(Domain.Payment payment, CancellationToken ct)
    {
        // Vault unreachable or card unknown → no country. Risk scores an
        // unknown country as suspicious (card_country_unknown), so a missing
        // signal makes the check stricter, never looser.
        var card = await vault.GetCardAsync(payment.CardToken, ct);

        var request = new
        {
            paymentId = payment.Id,
            customerId = payment.CustomerId,
            amountMinor = payment.Amount.AmountMinor,
            currency = payment.Amount.Currency,
            cardCountry = card?.Country,
        };

        try
        {
            // No idempotency key needed: assessing the same payment again is
            // harmless (Risk counts velocity by payment id, 5.2).
            using var response = await http.PostAsJsonAsync("api/risk/assessments", request, ct);
            if (!response.IsSuccessStatusCode)
                return new RiskCheckResult(null, 502, "risk_unavailable");

            var answer = (await response.Content.ReadFromJsonAsync<AssessmentAnswer>(Json, ct))!;
            return new RiskCheckResult(new RiskAssessment(answer.Decision, answer.Score,
                answer.Reasons.Select(r => r.Code).ToList()));
        }
        // The pipeline's timeout (5.6) gave up on Risk. No decision was
        // made, so nothing is lost: the payment stays Created and a retry
        // asks again. What to do instead of failing is 5.7.
        catch (TimeoutRejectedException)
        {
            return new RiskCheckResult(null, 504, "risk_timeout");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)   // HttpClient's own 100 s backstop
        {
            return new RiskCheckResult(null, 504, "risk_timeout");
        }
        catch (HttpRequestException)
        {
            return new RiskCheckResult(null, 502, "risk_unreachable");
        }
    }

    private sealed record AssessmentAnswer(RiskDecision Decision, int Score, List<ReasonAnswer> Reasons);
    private sealed record ReasonAnswer(string Code, int Points);
}
