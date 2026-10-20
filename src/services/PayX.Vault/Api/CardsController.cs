using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using PayX.Vault.Domain;
using PayX.Vault.Services;

namespace PayX.Vault.Api;

// No CVC field, and unknown fields are refused rather than ignored: a client
// sending "cvc" gets a 400 instead of believing it was kept. PCI DSS forbids
// storing the CVC after authorization, even encrypted; it travels with the
// authorization itself (Phase 4), never into storage.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TokenizeRequest(
    [Required] string Number,
    [Range(1, 12)] int ExpMonth,
    [Range(2000, 2100)] int ExpYear);

// What every other service gets instead of a card number.
public sealed record CardResponse(string Token, string Brand, string Last4, int ExpMonth, int ExpYear)
{
    public static CardResponse From(VaultedCard c) => new(c.Token, c.Brand, c.Last4, c.ExpMonth, c.ExpYear);
}

[ApiController]
[Route("api/vault/cards")]
public sealed class CardsController(CardVault vault) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Tokenize(TokenizeRequest request, CancellationToken ct)
    {
        var number = request.Number.Replace(" ", "");
        // The error names the problem, never echoes the number back.
        if (!CardNumber.IsValid(number))
            return Problem(statusCode: 400, title: "invalid_number");

        var card = await vault.TokenizeAsync(number, request.ExpMonth, request.ExpYear, ct);
        return CreatedAtAction(nameof(Get), new { token = card.Token }, CardResponse.From(card));
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct) =>
        await vault.FindAsync(token, ct) is { } card
            ? Ok(CardResponse.From(card))
            : Problem(statusCode: 404, title: "card_not_found");
}
