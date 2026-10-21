using System.Net.Http.Json;

namespace PayX.Vault.Services;

/// <summary>The PSP's answer, exactly as it came back: Vault relays it, it doesn't reinterpret it.</summary>
public sealed record PspAnswer(int StatusCode, string Body, string? ContentType, bool Replayed);

/// <summary>
/// Vault's only outbound call: the PSP's authorize endpoint (the simulator
/// locally; Stripe/Adyen in production). The request body is the one place a
/// full card number and CVC leave Vault, over TLS in production, straight to
/// the provider.
/// </summary>
public sealed class PspClient(HttpClient http)
{
    public async Task<PspAnswer> AuthorizeAsync(object request, string idempotencyKey, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/psp/authorizations")
        {
            Content = JsonContent.Create(request),
        };
        // Forwarded as given. Deriving a key per hop is Module 4.7.
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(message, ct);
        return new PspAnswer(
            (int)response.StatusCode,
            await response.Content.ReadAsStringAsync(ct),
            response.Content.Headers.ContentType?.ToString(),
            response.Headers.Contains("Idempotent-Replayed"));
    }
}
