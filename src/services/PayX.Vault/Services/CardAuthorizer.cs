using PayX.Vault.Repositories;

namespace PayX.Vault.Services;

/// <param name="Answer">The PSP's answer, when the PSP was reached (or its stored answer, on a retry).</param>
/// <param name="ErrorStatus">Otherwise, Vault's own refusal or failure.</param>
public sealed record AuthorizeOutcome(PspAnswer? Answer, int ErrorStatus = 0, string? Error = null);

/// <summary>
/// "Authorize this amount on card tok_…": the call Payment makes instead of
/// ever touching a card number (DESIGN: Payment → Vault → PSP). Vault fills
/// in the card number and CVC, sends the request to the PSP and hands the
/// answer back. This is how commercial vaults (VGS, Basis Theory) work: an
/// outbound proxy that adds the card data on the way out.
///
/// Retry-safe end to end (4.7): the caller's key goes to the PSP unchanged,
/// the CVC is kept until the PSP gives a definitive answer, and that answer
/// is remembered per key, so a retry gets either the PSP's replay or Vault's.
/// </summary>
public sealed class CardAuthorizer(CardVault vault, PspClient psp, AuthorizationAnswers answers)
{
    public async Task<AuthorizeOutcome> AuthorizeAsync(string token, long amount, string currency, string merchantId,
        string idempotencyKey, CancellationToken ct)
    {
        if (await vault.FindAsync(token, ct) is not { } card)
            return new AuthorizeOutcome(null, 404, "card_not_found");

        // Asked before? The PSP already answered for this key: that answer is
        // the outcome, even though the CVC that produced it is gone.
        if (await answers.FindAsync(token, idempotencyKey) is { } earlier)
            return new AuthorizeOutcome(earlier with { Replayed = true });

        // Never given, already used to completion, or expired.
        if (await vault.GetCvcAsync(token, ct) is not { } cvc)
            return new AuthorizeOutcome(null, 409, "cvc_unavailable");

        var number = await vault.RevealNumberAsync(card, ct);
        var request = new
        {
            amount,
            currency,
            card = new { number, expMonth = card.ExpMonth, expYear = card.ExpYear, cvc },
            merchantId,
        };

        PspAnswer answer;
        try
        {
            answer = await psp.AuthorizeAsync(request, idempotencyKey, ct);
        }
        // No answer: the PSP may still have authorized. The CVC is kept, so a
        // retry with this key sends the identical request and the PSP replays
        // or performs it once. Reported as a timeout, never as "failed" (6.4).
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AuthorizeOutcome(null, 504, "psp_timeout");
        }
        catch (HttpRequestException)
        {
            return new AuthorizeOutcome(null, 502, "psp_unreachable");
        }

        // Definitive (approved, declined, refused): remember it, then let the
        // CVC go. In that order: a crash in between leaves a CVC that expires
        // on its own, never an answer that can't be replayed.
        if (answer.StatusCode < 500)
        {
            await answers.SaveAsync(token, idempotencyKey, answer);
            await vault.ForgetCvcAsync(token);
        }
        return new AuthorizeOutcome(answer);
    }
}
