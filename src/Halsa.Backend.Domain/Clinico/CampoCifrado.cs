namespace Halsa.Backend.Domain.Clinico;

/// <summary>
/// Un campo cifrado con AES-256-GCM. <see cref="KeyId"/> identifica la DEK que lo cifró,
/// para que una rotación futura pueda convivir con los campos ya escritos.
/// </summary>
public sealed record CampoCifrado(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyId);
