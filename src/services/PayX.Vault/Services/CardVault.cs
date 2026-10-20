using PayX.Vault.Domain;
using PayX.Vault.Repositories;

namespace PayX.Vault.Services;

/// <summary>
/// Tokenization: a card number goes in once and a token comes out; from then
/// on every other service handles only the token (CLAUDE.md: "raw PANs never
/// leave Vault"). That keeps PCI DSS scope to this one service.
/// </summary>
public sealed class CardVault(CardEncryptor encryptor, CardRepository cards, TimeProvider clock)
{
    public async Task<VaultedCard> TokenizeAsync(string number, int expMonth, int expYear, CancellationToken ct)
    {
        // The token exists before the ciphertext does, because the ciphertext
        // is bound to it (3.6: encryption context + GCM associated data).
        var token = VaultedCard.NewToken();
        var encrypted = await encryptor.EncryptAsync(number, token, ct);

        var card = new VaultedCard(token, encrypted, CardNumber.BrandOf(number), number[^4..],
            expMonth, expYear, clock.GetUtcNow());
        await cards.AddAsync(card, ct);
        return card;
    }

    /// <summary>Card details safe to show anywhere. No KMS call, no card number.</summary>
    public Task<VaultedCard?> FindAsync(string token, CancellationToken ct) => cards.FindAsync(token, ct);

    /// <summary>
    /// The card number, for the one place it is needed: Vault's own call to
    /// the PSP (Phase 4: Payment → Vault → PSP). Deliberately not exposed over
    /// HTTP: an endpoint that returns card numbers would put every caller
    /// back in PCI scope.
    /// </summary>
    public async Task<string?> DetokenizeAsync(string token, CancellationToken ct) =>
        await cards.FindAsync(token, ct) is { } card
            ? await encryptor.DecryptAsync(card.Encrypted, card.Token, ct)
            : null;
}
