namespace Halsa.Backend.Domain.Casos;

/// <summary>Por qué la validación asíncrona rechazó un caso radicado (SPEC 05).</summary>
public enum MotivoRechazo
{
    /// <summary>Ya existe un caso del mismo trabajador y tipo con un evento dentro de la ventana de deduplicación.</summary>
    Duplicado = 1,

    /// <summary>El core de la ARL no reporta afiliación del trabajador en la fecha del evento.</summary>
    NoAfiliado = 2
}
