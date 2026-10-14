using Halsa.Backend.Application.Abstractions.Archivos;

namespace Halsa.Backend.FunctionalTests.Abstractions;

/// <summary>The antivirus of the functional tests (SPEC 07): its verdict is whatever the test sets. Clean by default.</summary>
public sealed class FakeAntivirus : IAntivirusScanner
{
    public ResultadoAntivirus Resultado { get; set; } = ResultadoAntivirus.Limpio;

    public Task<ResultadoAntivirus> AnalizarAsync(Stream contenido, CancellationToken ct) => Task.FromResult(Resultado);
}
