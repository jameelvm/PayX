namespace PayX.Ledger.Domain;

/// <summary>
/// What an account holds. Doc ch.2, "Wallet and ledger": the wallet is the
/// balance someone sees; the ledger is the record of every movement behind it.
/// Here both live in one service (DESIGN decision 10).
///
/// <para>Two of these are <b>memo accounts</b>: an authorization moves no
/// money (the issuer only reserves it on the card), but merchants and
/// customers want to see the hold, and a void must be able to undo it. So
/// holds are booked too, in accounts of their own, and never mixed with
/// accounts that hold real money.</para>
/// </summary>
public enum AccountType
{
    /// <summary>System account (one per currency): what the PSP owes us for captured payments. Debit-normal.</summary>
    PspClearing,
    /// <summary>Per merchant: captured, not yet settled by the PSP, so not yet payable. Credit-normal.</summary>
    MerchantPending,
    /// <summary>Per merchant: settled, payable to the merchant (moved here by settlement, Phase 8). Credit-normal.</summary>
    MerchantAvailable,
    /// <summary>Memo, per customer: amounts on hold on the customer's card. Debit-normal.</summary>
    CustomerHolds,
    /// <summary>Memo, per merchant: authorized, not yet captured. Credit-normal.</summary>
    MerchantAuthorized,
}

public static class AccountTypes
{
    /// <summary>Owner of the system accounts (no person or merchant owns PSP clearing).</summary>
    public static readonly Guid System = Guid.Empty;

    /// <summary>
    /// Postings are signed: positive = debit, negative = credit. A
    /// credit-normal account (one we owe, like a merchant's balance) therefore
    /// sums to a negative number when it holds money. Shown to people with
    /// the sign flipped, so a merchant owed $50 sees +50.
    /// </summary>
    public static bool IsCreditNormal(this AccountType type) =>
        type is AccountType.MerchantPending or AccountType.MerchantAvailable or AccountType.MerchantAuthorized;
}

/// <summary>Identifies an account without its id: what a posting rule asks for.</summary>
public readonly record struct AccountKey(AccountType Type, Guid OwnerId, string Currency);

/// <summary>
/// One account in one currency: (type, owner, currency) is unique, so a
/// merchant paid in USD and EUR has two pending accounts. Mixing currencies
/// in one balance would make the number meaningless.
/// </summary>
public sealed class Account
{
    public long Id { get; private set; }
    public AccountType Type { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Currency { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; }

    private Account() { }   // EF Core

    public AccountKey Key => new(Type, OwnerId, Currency);
}
