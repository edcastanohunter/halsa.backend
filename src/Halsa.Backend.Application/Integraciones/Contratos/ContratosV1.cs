namespace Halsa.Backend.Application.Integraciones.Contratos;

// La versión 1.0 de los cuerpos de docs/api/atel-api.openapi.yaml. Todo es nullable: que un campo falte es un
// error de validación con su ruta JSON, no una excepción de deserialización. Los enumerados llegan como texto
// y los valida el validador, por la misma razón.

/// <summary><c>FuratRequest</c>.</summary>
public sealed record FuratV1(
    string? Version,
    string? NumeroReporteOrigen,
    DateTimeOffset? FechaReporte,
    EmpleadorV1? Empleador,
    TrabajadorV1? Trabajador,
    EventoV1? Evento,
    IReadOnlyList<TestigoV1>? Testigos,
    InformanteV1? Informante,
    IpsAtencionInicialV1? IpsAtencionInicial);

/// <summary><c>FurepRequest</c>.</summary>
public sealed record FurepV1(
    string? Version,
    string? NumeroReporteOrigen,
    EmpleadorV1? Empleador,
    TrabajadorV1? Trabajador,
    InformanteV1? Informante,
    EnfermedadV1? Enfermedad);

public sealed record CodificadoV1(string? Sistema, string? Codigo, string? Descripcion);

public sealed record IdentificacionV1(string? Tipo, string? Numero);

public sealed record UbicacionV1(CodificadoV1? Municipio, string? Zona, string? Direccion);

public sealed record SedeV1(string? Nombre, UbicacionV1? Ubicacion, string? Telefono);

public sealed record EmpleadorV1(
    IdentificacionV1? Identificacion,
    string? RazonSocial,
    string? TipoVinculador,
    CodificadoV1? ActividadEconomica,
    SedeV1? Sede);

public sealed record ContactoV1(string? Telefono, string? Correo);

public sealed record TrabajadorV1(
    IdentificacionV1? Identificacion,
    string? PrimerNombre,
    string? SegundoNombre,
    string? PrimerApellido,
    string? SegundoApellido,
    DateOnly? FechaNacimiento,
    string? Sexo,
    string? TipoVinculacion,
    string? Cargo,
    CodificadoV1? Ocupacion,
    DateOnly? FechaIngreso,
    string? JornadaHabitual,
    ContactoV1? Contacto);

public sealed record EventoV1(
    DateTimeOffset? FechaHora,
    string? Jornada,
    bool? RealizabaLaborHabitual,
    string? LaborRealizada,
    int? TiempoLaboradoPrevioMinutos,
    string? Tipo,
    bool? CausoMuerte,
    bool? EsGrave,
    string? Lugar,
    string? Sitio,
    UbicacionV1? Ubicacion,
    IReadOnlyList<CodificadoV1>? TiposLesion,
    IReadOnlyList<CodificadoV1>? PartesCuerpo,
    CodificadoV1? Agente,
    CodificadoV1? Mecanismo,
    string? Descripcion);

public sealed record TestigoV1(IdentificacionV1? Identificacion, string? NombreCompleto, string? Cargo);

public sealed record InformanteV1(
    IdentificacionV1? Identificacion,
    string? NombreCompleto,
    string? Cargo,
    DateTimeOffset? FechaDiligenciamiento);

public sealed record IpsAtencionInicialV1(string? CodigoReps, string? Nombre);

public sealed record ProfesionalV1(
    IdentificacionV1? Identificacion,
    string? NombreCompleto,
    string? RegistroRethus,
    string? Especialidad);

public sealed record FactorRiesgoV1(string? Descripcion, int? TiempoExposicionMeses);

public sealed record EnfermedadV1(
    DateOnly? FechaDiagnostico,
    IReadOnlyList<CodificadoV1>? Diagnosticos,
    IReadOnlyList<FactorRiesgoV1>? FactoresRiesgo,
    ProfesionalV1? MedicoDiagnostica);
