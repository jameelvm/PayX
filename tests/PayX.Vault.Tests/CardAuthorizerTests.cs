using System.Net;
using System.Text.Json;
using PayX.Vault.Services;

namespace PayX.Vault.Tests;

// Vault's real card storage (LocalStack + Redis), with the PSP replaced by a
// stub that records what it was sent: the test sees exactly what leaves Vault.
public sealed class CardAuthorizerTests
{
    private const string Card = "4242424242424242";

    private readonly CardVault _vault = TestVault.Create();
    private readonly StubPsp _psp = new();

    private CardAuthorizer Authorizer(TimeSpan? timeout = null) => new(_vault,
        new PspClient(new HttpClient(_psp)
        {
            BaseAddress = new Uri("http://psp.test/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        }));

    [Fact]
    public async Task Sends_the_card_number_cvc_and_key_to_the_psp_and_relays_its_answer()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);

        var outcome = await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        var sent = JsonDocument.Parse(_psp.LastBody!).RootElement;
        Assert.Equal("http://psp.test/api/psp/authorizations", _psp.LastUri);
        Assert.Equal("key-1", _psp.LastIdempotencyKey);
        Assert.Equal((5_000, "USD", "m_1"),
            (sent.GetProperty("amount").GetInt64(), sent.GetProperty("currency").GetString(), sent.GetProperty("merchantId").GetString()));
        var sentCard = sent.GetProperty("card");
        Assert.Equal((Card, 12, 2030, "123"), (sentCard.GetProperty("number").GetString(),
            sentCard.GetProperty("expMonth").GetInt32(), sentCard.GetProperty("expYear").GetInt32(), sentCard.GetProperty("cvc").GetString()));

        Assert.Equal((200, StubPsp.Approved), (outcome.Answer!.StatusCode, outcome.Answer.Body));
    }

    [Fact]
    public async Task The_cvc_is_used_once_so_a_second_authorization_is_refused_without_calling_the_psp()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);
        await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        var second = await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-2", default);

        Assert.Equal((409, "cvc_unavailable"), (second.ErrorStatus, second.Error));
        Assert.Equal(1, _psp.Calls);
    }

    [Fact]
    public async Task An_unknown_token_is_404_and_reaches_nobody()
    {
        var outcome = await Authorizer().AuthorizeAsync("tok_nope", 5_000, "USD", "m_1", "key-1", default);

        Assert.Equal((404, "card_not_found"), (outcome.ErrorStatus, outcome.Error));
        Assert.Equal(0, _psp.Calls);
    }

    [Fact]
    public async Task A_psp_that_does_not_answer_in_time_is_a_timeout_not_a_failure()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);
        _psp.Delay = TimeSpan.FromSeconds(2);

        var outcome = await Authorizer(timeout: TimeSpan.FromMilliseconds(200))
            .AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        Assert.Equal((504, "psp_timeout"), (outcome.ErrorStatus, outcome.Error));
    }

    private sealed class StubPsp : HttpMessageHandler
    {
        public const string Approved = """{"authorizationId":"auth_1","status":"authorized","last4":"4242"}""";

        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            LastIdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            await Task.Delay(Delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Approved, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
