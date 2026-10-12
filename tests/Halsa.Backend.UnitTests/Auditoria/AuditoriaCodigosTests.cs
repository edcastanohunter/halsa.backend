using Halsa.Backend.Application.Abstractions.Auditoria;

namespace Halsa.Backend.UnitTests.Auditoria;

public sealed class AuditoriaCodigosTests
{
    [Fact]
    public void Codigo_ShouldUseUpperSnakeCase()
    {
        AuditoriaCodigos.Codigo(AccionAuditoria.Leer).ShouldBe("LEER");
        AuditoriaCodigos.Codigo(ResultadoAuditoria.NoEncontrado).ShouldBe("NO_ENCONTRADO");
        AuditoriaCodigos.Codigo(RecursoAuditoria.RegistroClinico).ShouldBe("REGISTRO_CLINICO");
    }

    [Theory]
    [InlineData("NO_ENCONTRADO")]
    [InlineData("no_encontrado")]
    [InlineData("NoEncontrado")]
    public void Normalizar_ShouldAcceptTheCodeOrTheEnumName(string valor)
    {
        AuditoriaCodigos.Normalizar<ResultadoAuditoria>(valor).ShouldBe("NO_ENCONTRADO");
    }

    [Theory]
    [InlineData("BORRAR")]
    [InlineData("")]
    [InlineData(null)]
    public void Normalizar_ShouldReturnNull_ForUnknownValues(string? valor)
    {
        AuditoriaCodigos.Normalizar<AccionAuditoria>(valor).ShouldBeNull();
    }
}
