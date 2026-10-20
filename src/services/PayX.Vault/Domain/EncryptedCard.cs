namespace PayX.Vault.Domain;

/// <summary>
/// A card number after envelope encryption: everything here is safe to store,
/// because none of it is readable without a call to KMS.
/// </summary>
/// <param name="Ciphertext">The card number encrypted with the data key
/// (AES-256-GCM): 12-byte nonce, then the ciphertext, then the 16-byte tag.</param>
/// <param name="EncryptedDataKey">The data key, encrypted by the KMS key. Only
/// KMS can turn it back into the key.</param>
/// <param name="KmsKeyArn">Which KMS key wrapped the data key, so decryption
/// asks that exact key even after the alias is pointed at a new one.</param>
public sealed record EncryptedCard(byte[] Ciphertext, byte[] EncryptedDataKey, string KmsKeyArn);
