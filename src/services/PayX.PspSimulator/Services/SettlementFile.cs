using System.Globalization;
using System.Text;
using PayX.PspSimulator.Domain;

namespace PayX.PspSimulator.Services;

/// <summary>
/// The settlement file itself: one CSV line per charge paid out (8.1).
///
/// Example, for a $100.00 charge with a 2.9% + 30¢ fee:
/// <code>
/// batch_id,business_date,psp_reference,merchant_reference,merchant_id,type,gross_minor,fee_minor,net_minor,currency,captured_at
/// stl_…,2026-11-11,auth_…,9f1c…,m_42,capture,10000,320,9680,USD,2026-11-11T10:15:02.123Z
/// </code>
/// - <b>psp_reference</b>: the PSP's own id (the authorization id). PayX's ledger
///   has it too, so reconciliation (8.3) matches on it.
/// - <b>merchant_reference</b>: PayX's payment id, if PayX sent one.
/// - <b>gross</b> = what the customer paid, <b>fee</b> = what the PSP keeps,
///   <b>net</b> = what the merchant actually receives (gross − fee).
///
/// Amounts are whole minor units (cents), never decimals, like everywhere
/// else in PayX. Every value is an id, a number or a code, so none can
/// contain a comma or quote: no CSV escaping is needed.
/// </summary>
public static class SettlementFile
{
    public const string Header =
        "batch_id,business_date,psp_reference,merchant_reference,merchant_id,type,gross_minor,fee_minor,net_minor,currency,captured_at";

    /// <summary>
    /// The PSP's fee for one charge: a percentage (in basis points, 1 bp =
    /// 0.01%, so 290 = 2.9%) plus a fixed amount, rounded half up to a whole
    /// cent. Integer maths only: no rounding drift from floating point.
    /// On a tiny charge the fee can be more than the charge (10¢ − 30¢), so
    /// net goes negative. Real PSPs do the same; it is not clamped.
    /// </summary>
    public static long Fee(long grossMinor, int basisPoints, long fixedMinor) =>
        (grossMinor * basisPoints + 5_000) / 10_000 + fixedMinor;

    public static string Build(SettlementBatch batch, IEnumerable<Authorization> charges, int basisPoints, long fixedMinor)
    {
        var csv = new StringBuilder().Append(Header).Append('\n');
        foreach (var charge in charges)
        {
            var gross = charge.CapturedAmountMinor!.Value;
            var fee = Fee(gross, basisPoints, fixedMinor);
            csv.Append(CultureInfo.InvariantCulture, $"{batch.Id},{batch.BusinessDate:yyyy-MM-dd},{charge.Id},")
               .Append(CultureInfo.InvariantCulture, $"{charge.Reference},{charge.MerchantId},capture,")
               .Append(CultureInfo.InvariantCulture, $"{gross},{fee},{gross - fee},{charge.Currency},")
               .Append(charge.CapturedAt!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
               .Append('\n');
        }
        return csv.ToString();
    }
}
