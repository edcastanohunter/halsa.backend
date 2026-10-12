namespace Halsa.Backend.Domain.Clinico;

/// <summary>Código de una terminología clínica, por ejemplo <c>("CIE-10", "S62.6")</c>.</summary>
public sealed record CodigoClinico(string Sistema, string Codigo);
