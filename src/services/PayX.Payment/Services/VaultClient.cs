using System.Net.Http.Json;
using PayX.Contracts;

namespace PayX.Payment.Services;

/// <summary>The PSP's answer, relayed by Vault (4.2b): only the fields Payment acts on.</summary>
public sealed record PspAuthorization(string AuthorizationId, string Status, string? AuthorizationCode, string? DeclineCode);

/// <param name="Authorization">Set when the PSP answered (approved or declined).</param>
/// <param name="StatusCode">Otherwise the HTTP status, or 504 when nothing came back in time.</param>
/// <summary>What Vault shows any service about a card: no number, no CVC.</summary>
public sealed record VaultCard(string Token, string Brand, string Last4, string? Country);

public sealed record VaultAuthorizeResult(PspAuthorization? Authorization, int StatusCode, string? Error);

/// <summary>
/// Payment's only way to the PSP: through Vault, by card token. Payment never
/// sees a card number or CVC, which keeps it out of PCI DSS scope.
/// </summary>
public sealed class VaultClient(HttpClient http)
{
    public async Task<VaultAuthorizeResult> AuthorizeAsync(string cardToken, Money amount, Guid merchantId,
        string idempotencyKey, CancellationToken ct, string? reference = null)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"api/vault/cards/{Uri.EscapeDataString(cardToken)}/authorizations")
        {
            // reference = the payment id: the PSP keeps it, so the resolver can
            // ask "what happened to this payment?" (6.5).
            Content = JsonContent.Create(new { amount = amount.AmountMinor, currency = amount.Currency, merchantId, reference }),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        try
        {
            using var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                // Vault's own refusal (404 card_not_found, 409 cvc_unavailable…)
                // or a relayed PSP error: the title is the machine-readable code.
                var problem = await response.Content.ReadFromJsonAsync<ProblemTitle>(ct);
                return new VaultAuthorizeResult(null, (int)response.StatusCode, problem?.Title);
            }
            return new VaultAuthorizeResult(await response.Content.ReadFromJsonAsync<PspAuthorization>(ct), 200, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new VaultAuthorizeResult(null, 504, "vault_timeout");
        }
        catch (HttpRequestException)
        {
            return new VaultAuthorizeResult(null, 502, "vault_unreachable");
        }
    }

    /// <summary>
    /// The card's safe details (5.5: its country, for Risk), or null when
    /// Vault doesn't answer with them. Never the number.
    /// </summary>
    public async Task<VaultCard?> GetCardAsync(string cardToken, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"api/vault/cards/{Uri.EscapeDataString(cardToken)}", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<VaultCard>(ct) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private sealed record ProblemTitle(string? Title);
}
