using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Payment.Repositories;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// checkPaymentStatus against the real payx_payments database: who may see a payment.
public sealed class PaymentQueriesTests
{
    private static readonly Guid Customer = Guid.NewGuid(), Merchant = Guid.NewGuid();

    public PaymentQueriesTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task The_customer_and_the_merchant_both_see_the_payment()
    {
        var id = await SavedPaymentId();

        Assert.NotNull(await new PaymentQueries(NewContext()).FindVisibleAsync(id, Customer, default));
        Assert.NotNull(await new PaymentQueries(NewContext()).FindVisibleAsync(id, Merchant, default));
    }

    [Fact]
    public async Task Anyone_else_gets_nothing_exactly_as_for_a_missing_id()
    {
        var id = await SavedPaymentId();

        Assert.Null(await new PaymentQueries(NewContext()).FindVisibleAsync(id, Guid.NewGuid(), default));
        Assert.Null(await new PaymentQueries(NewContext()).FindVisibleAsync("pay_missing", Customer, default));
    }

    private static async Task<string> SavedPaymentId()
    {
        var payment = new Domain.Payment(Merchant, Customer, new Money(5_000, "USD"), "tok_test");
        await using var db = NewContext();
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev")
        .UseSnakeCaseNamingConvention()
        .Options);
}
