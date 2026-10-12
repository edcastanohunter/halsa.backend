namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// Una DEK de un tenant para un propósito, envuelta con la KEK (<c>clinico.llaves_tenant</c>).
/// Se mapea solo para que la migración cree la tabla: <see cref="DekStore"/> la lee y escribe con SQL.
/// </summary>
internal sealed class LlaveTenant
{
    /// <summary><c>"{tenantId}:{proposito}:v{version}"</c>, por ejemplo <c>ARL_A:CONTENIDO:v1</c>.</summary>
    public required string KeyId { get; init; }

    public required string TenantId { get; init; }

    public required string Proposito { get; init; }

    public required int Version { get; init; }

    public required string KekId { get; init; }

    public required byte[] DekEnvuelta { get; init; }

    public required DateTimeOffset CreadaEn { get; init; }

    public required bool Activa { get; init; }
}
