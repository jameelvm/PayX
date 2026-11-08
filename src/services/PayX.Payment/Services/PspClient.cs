using System.Net.Http.Json;
using Polly.Timeout;

namespace PayX.Payment.Services;

/// <param name="Status">The authorization's status at the PSP after the call ("captured"…), when it answered 200.</param>
/// <param name="StatusCode">The HTTP status, or 504 / 502 when no answer came back.</param>
/// <param name="Error">The PSP's refusal code (its ProblemDetails title), or ours for a timeout.</param>
public sealed record PspResult(string? Status, int StatusCode, string? Error);

/// <summary>What the PSP knows about one of our payments, by our reference (6.5).</summary>
/// <param name="Reachable">False when the PSP couldn't be asked: no conclusion may be drawn.</param>
/// <param name="Authorization">Null when the PSP answered and has no record.</param>
public sealed record PspLookup(bool Reachable, PspAuthorizationView? Authorization);

public sealed record PspAuthorizationView(string AuthorizationId, string Status, string? AuthorizationCode, string? DeclineCode);

/// <summary>
/// Payment's direct line to the PSP, for operations on an existing
/// authorization: capture now, void and refund later. These carry the PSP's
/// authorization id, never card data, so they don't go through Vault
/// (DESIGN.md decision 32). Only authorize needs the card, and only Vault has it.
/// </summary>
public sealed class PspClient(HttpClient http)
{
    public Task<PspResult> CaptureAsync(string pspAuthorizationId, long amountMinor, string idempotencyKey,
        CancellationToken ct) =>
        PostAsync($"api/psp/authorizations/{Uri.EscapeDataString(pspAuthorizationId)}/capture",
            new { amount = amountMinor }, idempotencyKey, ct);

    /// <summary>
    /// "What happened to payment X?" (6.5), by the reference sent with the
    /// authorization. A plain read: safe to repeat, no key needed.
    /// </summary>
    public async Task<PspLookup> FindByReferenceAsync(string reference, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                $"api/psp/authorizations?reference={Uri.EscapeDataString(reference)}", ct);
            if (!response.IsSuccessStatusCode)
                return new PspLookup(false, null);   // a 5xx is not "no record"
            var found = await response.Content.ReadFromJsonAsync<List<PspAuthorizationView>>(ct);
            // One idempotency key per payment means at most one authorization.
            return new PspLookup(true, found?.LastOrDefault());
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new PspLookup(false, null);
        }
    }

    private async Task<PspResult> PostAsync(string path, object body, string idempotencyKey, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", idempotencyKey);

        try
        {
            using var response = await http.SendAsync(message, ct);
            // Two shapes, read separately: an authorization's "status" is
            // text ("captured"), a ProblemDetails' "status" is a number (409).
            // One shared record for both broke on every refusal (found in 4.3's tests).
            return response.IsSuccessStatusCode
                ? new PspResult((await response.Content.ReadFromJsonAsync<Authorization>(ct))?.Status, 200, null)
                : new PspResult(null, (int)response.StatusCode,
                    (await response.Content.ReadFromJsonAsync<Problem>(ct))?.Title);
        }
        // No answer in time: the PSP may have captured. Not a failure, an
        // unknown, and safe to retry with the same key (it replays).
        catch (Exception ex) when (ex is TimeoutRejectedException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // The retry budget (6.3) ran out without an answer.
            return new PspResult(null, 504, "psp_timeout");
        }
        catch (HttpRequestException)
        {
            return new PspResult(null, 502, "psp_unreachable");
        }
    }

    private sealed record Authorization(string? Status);
    private sealed record Problem(string? Title);
}
