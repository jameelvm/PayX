using PayX.Vault.Domain;
using PayX.Vault.Repositories;

namespace PayX.Vault.Services;

/// <summary>
/// Tokenization: a card number goes in once and a token comes out; from then
/// on every other service handles only the token (CLAUDE.md: "raw PANs never
/// leave Vault"). That keeps PCI DSS scope to this one service.
/// </summary>
public sealed class CardVault(CardEncryptor encryptor, CardRepository cards, CvcStore cvcs, TimeProvider clock)
{
    public async Task<VaultedCard> TokenizeAsync(string number, int expMonth, int expYear, string? cvc,
        CancellationToken ct)
    {
        // The token exists before the ciphertext does, because the ciphertext
        // is bound to it (3.6: encryption context + GCM associated data).
        var token = VaultedCard.NewToken();

        // CVC first: if storing the card then fails, the CVC is an orphan
        // that expires on its own. The other order would leave a stored card
        // nobody can authorize.
        if (cvc is not null)
            await cvcs.HoldAsync(token, cvc, ct);

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
        await cards.FindAsync(token, ct) is { } card ? await RevealNumberAsync(card, ct) : null;

    /// <summary>The same, for a card already loaded (saves a second DynamoDB read).</summary>
    public Task<string> RevealNumberAsync(VaultedCard card, CancellationToken ct) =>
        encryptor.DecryptAsync(card.Encrypted, card.Token, ct);

    /// <summary>
    /// The CVC given at tokenization, for the first authorization (4.2b).
    /// Null if it was never given, already used to completion, or expired.
    /// </summary>
    public Task<string?> GetCvcAsync(string token, CancellationToken ct) => cvcs.GetAsync(token, ct);

    /// <summary>The authorization it was for has completed: forget it (4.7).</summary>
    public Task ForgetCvcAsync(string token) => cvcs.ForgetAsync(token);
}
