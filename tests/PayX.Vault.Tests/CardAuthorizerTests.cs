using System.Net;
using System.Text.Json;
using PayX.Vault.Repositories;
using PayX.Vault.Services;

namespace PayX.Vault.Tests;

// Vault's real card storage (LocalStack + Redis), with the PSP replaced by a
// stub that records what it was sent: the test sees exactly what leaves Vault.
public sealed class CardAuthorizerTests
{
    private const string Card = "4242424242424242";
    private const string Approved = """{"authorizationId":"auth_1","status":"authorized","last4":"4242"}""";

    private readonly CardVault _vault = TestVault.Create();
    private readonly StubPsp _psp = new();

    private CardAuthorizer Authorizer(TimeSpan? timeout = null) => new(_vault,
        new PspClient(new HttpClient(_psp)
        {
            BaseAddress = new Uri("http://psp.test/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        }),
        new AuthorizationAnswers(TestVault.Redis));

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

        Assert.Equal((200, Approved, false), (outcome.Answer!.StatusCode, outcome.Answer.Body, outcome.Answer.Replayed));
    }

    [Fact]
    public async Task A_definitive_answer_forgets_the_cvc_so_a_new_authorization_is_refused()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);
        await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        var second = await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-2", default);

        Assert.Null(await _vault.GetCvcAsync(card.Token, default));
        Assert.Equal((409, "cvc_unavailable"), (second.ErrorStatus, second.Error));
        Assert.Equal(1, _psp.Calls);
    }

    [Fact]
    public async Task The_same_key_again_replays_the_first_answer_without_calling_the_psp()
    {
        // 4.7: the answer was lost on its way back to Payment, which retries.
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);
        await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        var retry = await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        Assert.Equal((200, Approved, true), (retry.Answer!.StatusCode, retry.Answer.Body, retry.Answer.Replayed));
        Assert.Equal(1, _psp.Calls);
    }

    [Fact]
    public async Task After_a_timeout_the_cvc_is_kept_and_the_retry_sends_the_identical_request()
    {
        // 4.7, the 4.2b gap: the first call gets no answer in time.
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);
        _psp.Delay = TimeSpan.FromSeconds(2);
        var first = await Authorizer(timeout: TimeSpan.FromMilliseconds(200))
            .AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);
        Assert.Equal((504, "psp_timeout"), (first.ErrorStatus, first.Error));
        var firstBody = _psp.LastBody;

        _psp.Delay = TimeSpan.Zero;
        var retry = await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        Assert.Equal(200, retry.Answer!.StatusCode);
        Assert.Equal((firstBody, "key-1"), (_psp.LastBody, _psp.LastIdempotencyKey));   // same request, same key: the PSP dedupes
    }

    [Fact]
    public async Task A_psp_error_keeps_the_cvc_for_the_retry()
    {
        var card = await _vault.TokenizeAsync(Card, 12, 2030, cvc: "123", default);
        _psp.Status = HttpStatusCode.ServiceUnavailable;

        var outcome = await Authorizer().AuthorizeAsync(card.Token, 5_000, "USD", "m_1", "key-1", default);

        Assert.Equal(503, outcome.Answer!.StatusCode);   // relayed, not remembered
        Assert.Equal("123", await _vault.GetCvcAsync(card.Token, default));
    }

    [Fact]
    public async Task An_unknown_token_is_404_and_reaches_nobody()
    {
        var outcome = await Authorizer().AuthorizeAsync("tok_nope", 5_000, "USD", "m_1", "key-1", default);

        Assert.Equal((404, "card_not_found"), (outcome.ErrorStatus, outcome.Error));
        Assert.Equal(0, _psp.Calls);
    }

    private sealed class StubPsp : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public TimeSpan Delay { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri!.ToString();
            LastBody = await request.Content!.ReadAsStringAsync(ct);
            LastIdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            await Task.Delay(Delay, ct);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK ? Approved : """{"title":"unavailable"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
