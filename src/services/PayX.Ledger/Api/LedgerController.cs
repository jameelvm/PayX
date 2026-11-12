using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PayX.Ledger.Services;

namespace PayX.Ledger.Api;

[ApiController]
[Route("api/ledger")]
public sealed class LedgerController(LedgerQueries queries) : ControllerBase
{
    /// <summary>
    /// Doc ch.1: getTransactionHistory. The caller's own entries, newest
    /// first: a customer sees what they paid (holds, captures, releases), a
    /// merchant what they were paid. Who the caller is comes from the token,
    /// never from a parameter (as in Payment, 4.4).
    /// </summary>
    /// <param name="from">Entries that happened at or after this time.</param>
    /// <param name="to">Entries that happened before this time.</param>
    /// <param name="before">Paging cursor: the <c>nextBefore</c> of the previous page.</param>
    /// <param name="limit">Page size, 1 to 100.</param>
    [HttpGet("transactions"), Authorize(Roles = "Customer,Merchant")]
    public async Task<IActionResult> Transactions(DateTimeOffset? from, DateTimeOffset? to, long? before,
        CancellationToken ct, int limit = 20)
    {
        if (from is not null && to is not null && from >= to)
            return Problem(statusCode: 400, title: "invalid_range", detail: "'from' must be before 'to'");

        return Ok(await queries.HistoryAsync(CallerId, CallerView, from, to, before, limit, ct));
    }

    /// <summary>
    /// Doc ch.1: balance management, "available vs pending". A merchant gets
    /// authorized / pending / available per currency; a customer what is on
    /// hold on their card. From the stored balances, so one row per account.
    /// </summary>
    [HttpGet("balance"), Authorize(Roles = "Customer,Merchant")]
    public async Task<IActionResult> Balance(CancellationToken ct) =>
        CallerView == Viewer.Merchant
            ? Ok(new { balances = await queries.MerchantBalancesAsync(CallerId, ct) })
            : Ok(new { balances = await queries.CustomerBalancesAsync(CallerId, ct) });

    private Guid CallerId => Guid.Parse(User.FindFirst("sub")!.Value);

    // A user has one role (Identity, 2.2): a merchant's history is what they
    // were paid, anyone else's what they paid.
    private Viewer CallerView => User.IsInRole("Merchant") ? Viewer.Merchant : Viewer.Customer;
}
