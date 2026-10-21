namespace PayX.Vault.Services;

/// <param name="Answer">The PSP's answer, when the PSP was reached.</param>
/// <param name="ErrorStatus">Otherwise, Vault's own refusal or failure.</param>
public sealed record AuthorizeOutcome(PspAnswer? Answer, int ErrorStatus = 0, string? Error = null);

/// <summary>
/// "Authorize this amount on card tok_…": the call Payment makes instead of
/// ever touching a card number (DESIGN: Payment → Vault → PSP). Vault fills
/// in the card number and CVC, sends the request to the PSP and hands the
/// answer back. This is how commercial vaults (VGS, Basis Theory) work: an
/// outbound proxy that adds the card data on the way out.
/// </summary>
public sealed class CardAuthorizer(CardVault vault, PspClient psp)
{
    public async Task<AuthorizeOutcome> AuthorizeAsync(string token, long amount, string currency, string merchantId,
        string idempotencyKey, CancellationToken ct)
    {
        // Check the card exists before taking the CVC: a typo in the token
        // must not use up a real card's CVC.
        if (await vault.FindAsync(token, ct) is not { } card)
            return new AuthorizeOutcome(null, 404, "card_not_found");

        // Taken once (4.2a). Known gap: if the PSP call below times out, the
        // CVC is already gone, so a retry with the same key can't rebuild the
        // same request. Closing that is Module 4.7.
        if (await vault.TakeCvcAsync(token, ct) is not { } cvc)
            return new AuthorizeOutcome(null, 409, "cvc_unavailable");

        var number = await vault.RevealNumberAsync(card, ct);
        var request = new
        {
            amount,
            currency,
            card = new { number, expMonth = card.ExpMonth, expYear = card.ExpYear, cvc },
            merchantId,
        };

        try
        {
            return new AuthorizeOutcome(await psp.AuthorizeAsync(request, idempotencyKey, ct));
        }
        // The PSP didn't answer in time. It may still have authorized: that
        // is the caller's "unknown" case (Module 6.4), so say timeout, never "failed".
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AuthorizeOutcome(null, 504, "psp_timeout");
        }
        catch (HttpRequestException)
        {
            return new AuthorizeOutcome(null, 502, "psp_unreachable");
        }
    }
}
