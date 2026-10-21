using System.Net.Http.Json;

namespace PayX.Payment.Services;

/// <param name="Status">The authorization's status at the PSP after the call ("captured"…), when it answered 200.</param>
/// <param name="StatusCode">The HTTP status, or 504 / 502 when no answer came back.</param>
/// <param name="Error">The PSP's refusal code (its ProblemDetails title), or ours for a timeout.</param>
public sealed record PspResult(string? Status, int StatusCode, string? Error);

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
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
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
