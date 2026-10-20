using System.Security.Cryptography;
using System.Text;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using PayX.Vault.Domain;

namespace PayX.Vault.Services;

/// <summary>
/// Envelope encryption of card numbers (doc ch.2: "where are payment details
/// encrypted?"). Each card gets its own data key from KMS; the card is
/// encrypted here with that key, and only the KMS-encrypted copy of the key is
/// kept. The KMS key itself never leaves KMS.
/// </summary>
public sealed class CardEncryptor(IAmazonKeyManagementService kms, IConfiguration config)
{
    private const int NonceSize = 12, TagSize = 16;   // AES-GCM standard sizes

    private readonly string _kmsKeyId = config["Vault:KmsKeyId"]
        ?? throw new InvalidOperationException("Missing configuration: Vault:KmsKeyId");

    /// <param name="recordId">The id the encrypted card will be stored under
    /// (the token, from 3.7). Binds the ciphertext to that record: see <see cref="Context"/>.</param>
    public async Task<EncryptedCard> EncryptAsync(string cardNumber, string recordId, CancellationToken ct)
    {
        // 1. KMS creates a fresh 256-bit data key and returns it twice: in the
        //    clear (to use now) and encrypted under the KMS key (to store).
        var dataKey = await kms.GenerateDataKeyAsync(new GenerateDataKeyRequest
        {
            KeyId = _kmsKeyId,
            KeySpec = DataKeySpec.AES_256,
            EncryptionContext = Context(recordId),
        }, ct);

        var key = dataKey.Plaintext.ToArray();
        var plaintext = Encoding.ASCII.GetBytes(cardNumber);
        try
        {
            // 2. Encrypt locally. No card number is ever sent to KMS: KMS
            //    only handles keys, and at most 4 KB of data per call anyway.
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(recordId));

            return new EncryptedCard([.. nonce, .. ciphertext, .. tag], dataKey.CiphertextBlob.ToArray(), dataKey.KeyId);
        }
        finally
        {
            // 3. Forget the clear data key. Best effort: the string card
            //    number and the SDK's own buffer can't be wiped in .NET; the
            //    real protection is that they live for milliseconds, in memory only.
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task<string> DecryptAsync(EncryptedCard card, string recordId, CancellationToken ct)
    {
        // KMS unwraps the data key only if the encryption context matches the
        // one used to create it, and logs the call (CloudTrail on AWS): every
        // decryption of every card is auditable and can be denied by IAM.
        var dataKey = await kms.DecryptAsync(new DecryptRequest
        {
            CiphertextBlob = new MemoryStream(card.EncryptedDataKey),
            KeyId = card.KmsKeyArn,
            EncryptionContext = Context(recordId),
        }, ct);

        var key = dataKey.Plaintext.ToArray();
        try
        {
            var blob = card.Ciphertext.AsSpan();
            var plaintext = new byte[blob.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            // Throws AuthenticationTagMismatchException if a single bit of the
            // ciphertext, nonce, tag or record id changed: GCM detects
            // tampering instead of returning a wrong card number.
            aes.Decrypt(blob[..NonceSize], blob[NonceSize..^TagSize], blob[^TagSize..], plaintext,
                Encoding.UTF8.GetBytes(recordId));
            return Encoding.ASCII.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // Ties both layers to the record: KMS refuses to unwrap the data key for
    // any other record id (encryption context), and GCM refuses the card
    // ciphertext (associated data). Someone able to write to the table can't
    // swap one card's encrypted blob into another card's record. The context
    // is logged in clear by CloudTrail, so it must never hold secrets.
    private static Dictionary<string, string> Context(string recordId) => new() { ["payx:record"] = recordId };
}
