namespace Halsa.Backend.Domain.Casos;

/// <summary>Estado del ciclo de vida de un <see cref="CasoMedico"/>.</summary>
public enum EstadoCaso
{
    Radicado = 0,
    Validado = 1,
    EnAtencion = 2,
    EnCalificacionOrigen = 3,
    EnCalificacionPcl = 4,
    Cerrado = 5,
    Rechazado = 6
}
