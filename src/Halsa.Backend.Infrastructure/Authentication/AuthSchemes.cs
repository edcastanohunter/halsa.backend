namespace Halsa.Backend.Infrastructure.Authentication;

/// <summary>
/// Nombres de los esquemas de autenticación registrados.
/// <see cref="Bearer"/> es el policy scheme selector que elige entre los dos esquemas JWT
/// leyendo el claim <c>iss</c> del token sin validarlo.
/// </summary>
internal static class AuthSchemes
{
    /// <summary>Esquema por defecto: policy scheme que delega a <see cref="Personal"/> o <see cref="Paciente"/>.</summary>
    internal const string Bearer = "Bearer";

    /// <summary>Esquema JWT para tokens emitidos por el realm <c>halsa</c> (personal de la ARL/IPS).</summary>
    internal const string Personal = "Personal";

    /// <summary>Esquema JWT para tokens emitidos por el realm <c>halsa-pacientes</c>.</summary>
    internal const string Paciente = "Paciente";
}
