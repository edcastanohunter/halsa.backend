namespace Halsa.Backend.Application.Abstractions.Auditoria;

/// <summary>Qué se intentó hacer. Se guarda en <c>accion</c> como <c>LEER</c>, <c>CREAR</c>, …</summary>
public enum AccionAuditoria
{
    Leer,
    Crear,
    Actualizar,
    Eliminar,
    Corregir,
    Exportar
}

/// <summary>Cómo terminó el intento. Se guarda en <c>resultado</c> como <c>EXITO</c>, <c>NO_ENCONTRADO</c>, …</summary>
public enum ResultadoAuditoria
{
    Exito,
    Denegado,
    NoEncontrado,
    Error
}

/// <summary>Sobre qué recurso. <c>Incapacidad</c> y <c>Soporte</c>: SPEC 07. <c>Plazo</c>: SPEC 09.</summary>
public enum RecursoAuditoria
{
    Caso,
    Remision,
    Asignacion,
    RegistroClinico,
    Auditoria,
    Incapacidad,
    Soporte,
    Plazo
}

/// <summary>El recurso tocado. <paramref name="CasoMedicoId"/> permite al oficial de privacidad filtrar por caso.</summary>
public sealed record ObjetivoAuditoria(RecursoAuditoria Tipo, string? RecursoId, Guid? CasoMedicoId);
