namespace Halsa.Backend.SharedKernel.Tenancy;

/// <summary>
/// Tipo de actor que origina un request autenticado.
/// El valor <see cref="Sistema"/> está reservado para procesos sin HTTP (SPEC 05);
/// nunca llega en un token.
/// </summary>
public enum ActorTipo
{
    Arl = 1,
    Ips = 2,
    Paciente = 3,
    Integracion = 4,
    Sistema = 5
}
