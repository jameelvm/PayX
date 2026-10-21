using Microsoft.EntityFrameworkCore;
using PayX.Contracts;
using PayX.Payment.Domain;
using PayX.Payment.Repositories;

namespace PayX.Payment.Tests;

// Integration tests against payx_payments in the local Postgres (port 5434),
// connecting as the service's own role. Start it first: docker compose up -d postgres.
public sealed class PaymentPersistenceTests
{
    private static PaymentsDbContext NewContext() => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql("Host=localhost;Port=5434;Database=payx_payments;Username=payx_payments;Password=payx_payments_dev")
        .UseSnakeCaseNamingConvention()
        .Options);

    public PaymentPersistenceTests()
    {
        using var db = NewContext();
        db.Database.Migrate();
    }

    [Fact]
    public async Task A_payment_round_trips_with_its_money_and_status()
    {
        var payment = await SavedAuthorizedPayment();

        await using var db = NewContext();
        var loaded = await db.Payments.SingleAsync(p => p.Id == payment.Id);

        Assert.Equal(new Money(5_000, "USD"), loaded.Amount);
        Assert.Equal((PaymentStatus.Authorized, "auth_test"), (loaded.Status, loaded.PspAuthorizationId));
    }

    [Fact]
    public async Task Capture_and_void_racing_on_one_payment_cannot_both_save()
    {
        var payment = await SavedAuthorizedPayment();

        // Two workers load the same Authorized payment; both moves are legal
        // from what each one read.
        await using var first = NewContext();
        await using var second = NewContext();
        var forCapture = await first.Payments.SingleAsync(p => p.Id == payment.Id);
        var forVoid = await second.Payments.SingleAsync(p => p.Id == payment.Id);

        forCapture.Capture(forCapture.Amount);
        await first.SaveChangesAsync();

        forVoid.Void();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var check = NewContext();
        Assert.Equal(PaymentStatus.Captured, (await check.Payments.SingleAsync(p => p.Id == payment.Id)).Status);
    }

    private static async Task<Domain.Payment> SavedAuthorizedPayment()
    {
        var payment = new Domain.Payment(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");
        payment.ApproveRisk();
        payment.Authorize("auth_test", "A1B2C3");

        await using var db = NewContext();
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }
}
