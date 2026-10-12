namespace Halsa.Backend.Domain.Casos;

/// <summary>Identificador único de un <see cref="CasoMedico"/>. Siempre UUID v7.</summary>
public readonly record struct CasoMedicoId(Guid Value)
{
    public static CasoMedicoId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Identificador único de una <see cref="Remision"/>. Siempre UUID v7.</summary>
public readonly record struct RemisionId(Guid Value)
{
    public static RemisionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
