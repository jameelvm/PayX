using System.Security.Cryptography;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;
using PayX.Vault.Services;

namespace PayX.Vault.Tests;

// Integration tests: they call the KMS emulated by LocalStack (port 4568), the
// same API the service calls on AWS. Start it first: docker compose up -d localstack.
public sealed class CardEncryptorTests
{
    private const string Card = "4242424242424242";

    private readonly CardEncryptor _encryptor = new(
        new AmazonKeyManagementServiceClient(new BasicAWSCredentials("test", "test"),
            new AmazonKeyManagementServiceConfig { ServiceURL = "http://localhost:4568", AuthenticationRegion = "us-east-1" }),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:KmsKeyId"] = "alias/payx-card-vault",
        }).Build());

    [Fact]
    public async Task Round_trip_returns_the_card_number()
    {
        var encrypted = await _encryptor.EncryptAsync(Card, "tok_1", default);

        Assert.Equal(Card, await _encryptor.DecryptAsync(encrypted, "tok_1", default));
    }

    [Fact]
    public async Task Stored_form_contains_no_card_number_and_records_the_real_key()
    {
        var encrypted = await _encryptor.EncryptAsync(Card, "tok_1", default);

        Assert.DoesNotContain(Card, Encoding.ASCII.GetString(encrypted.Ciphertext));
        Assert.Equal(12 + Card.Length + 16, encrypted.Ciphertext.Length);   // nonce + card + tag
        Assert.StartsWith("arn:aws:kms:", encrypted.KmsKeyArn);            // the key itself, not the alias
    }

    [Fact]
    public async Task Same_card_encrypted_twice_gets_a_new_data_key_and_ciphertext()
    {
        var first = await _encryptor.EncryptAsync(Card, "tok_1", default);
        var second = await _encryptor.EncryptAsync(Card, "tok_1", default);

        Assert.NotEqual(first.EncryptedDataKey, second.EncryptedDataKey);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public async Task A_flipped_bit_in_the_ciphertext_is_detected()
    {
        var encrypted = await _encryptor.EncryptAsync(Card, "tok_1", default);
        encrypted.Ciphertext[15] ^= 0x01;

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => _encryptor.DecryptAsync(encrypted, "tok_1", default));
    }

    [Fact]
    public async Task A_card_moved_to_another_record_is_refused_by_KMS()
    {
        var encrypted = await _encryptor.EncryptAsync(Card, "tok_1", default);

        await Assert.ThrowsAsync<InvalidCiphertextException>(
            () => _encryptor.DecryptAsync(encrypted, "tok_2", default));
    }
}
