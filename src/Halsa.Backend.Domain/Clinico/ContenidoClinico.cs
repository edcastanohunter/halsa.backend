namespace Halsa.Backend.Domain.Clinico;

/// <summary>Texto clínico en claro. Nunca se persiste así: se serializa a JSON y se cifra.</summary>
public sealed record ContenidoClinico(
    string? Motivo,
    string? Anamnesis,
    string? ExamenFisico,
    string? Analisis,
    string? Plan,
    IReadOnlyList<CodigoClinico> Diagnosticos,
    string? Observaciones);
