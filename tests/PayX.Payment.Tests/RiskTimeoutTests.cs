using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayX.Contracts;
using PayX.Payment.Services;

namespace PayX.Payment.Tests;

// The Risk client as Program.cs registers it (AddRiskClient: HttpClient +
// Polly pipeline), with Risk itself replaced by a stub that answers fast or
// slowly. A 200 ms timeout keeps the tests quick; production uses 2 s.
public sealed class RiskTimeoutTests
{
    private const string Allow = """{"paymentId":"x","decision":"Allow","score":0,"reasons":[]}""";

    private static readonly Domain.Payment Payment =
        new(Guid.NewGuid(), Guid.NewGuid(), new Money(5_000, "USD"), "tok_test");

    [Fact]
    public async Task A_slow_risk_service_is_given_up_on_at_the_timeout()
    {
        var risk = Client(answerAfter: TimeSpan.FromSeconds(5));

        var clock = Stopwatch.StartNew();
        var result = await risk.CheckAsync(Payment, default);

        Assert.Equal((504, "risk_timeout"), (result.FailureStatus, result.Failure));
        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(2));   // not 5 s
    }

    [Fact]
    public async Task A_fast_answer_passes_through_the_pipeline_untouched()
    {
        var risk = Client(answerAfter: TimeSpan.FromMilliseconds(20));

        var result = await risk.CheckAsync(Payment, default);

        Assert.Equal(RiskDecision.Allow, result.Assessment!.Decision);
    }

    private static IRiskCheck Client(TimeSpan answerAfter)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Risk:BaseUrl"] = "http://risk.test/",
            ["Risk:Timeout"] = "00:00:00.200",
        }).Build();

        var services = new ServiceCollection();
        services.AddHttpClient<VaultClient>(http => http.BaseAddress = new Uri("http://vault.test/"))
            .ConfigurePrimaryHttpMessageHandler(() => new Answer(TimeSpan.Zero, "{}"));
        services.AddRiskClient(config)
            .ConfigurePrimaryHttpMessageHandler(() => new Answer(answerAfter, Allow));

        return services.BuildServiceProvider().GetRequiredService<IRiskCheck>();
    }

    // Stands in for the network: answers after a delay, and stops waiting
    // when the caller cancels (as a real socket read does).
    private sealed class Answer(TimeSpan after, string body) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(after, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
