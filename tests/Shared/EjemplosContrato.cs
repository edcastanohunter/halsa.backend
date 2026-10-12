using System.Text.Json.Nodes;

namespace Halsa.Backend.Tests.Shared;

/// <summary>
/// Los ejemplos de <c>docs/api/atel-api.openapi.yaml</c>, como JSON. Compartido (enlazado) por los proyectos de
/// tests unitarios, de integración y funcionales.
/// </summary>
internal static class EjemplosContrato
{
    /// <summary><c>components.examples.FuratCaidaAltura</c>.</summary>
    public const string FuratCaidaAltura = """
        {
          "version": "1.0",
          "numeroReporteOrigen": "POS-FURAT-2026-884123",
          "fechaReporte": "2026-10-08T09:15:00-05:00",
          "empleador": {
            "identificacion": { "tipo": "NIT", "numero": "900123456" },
            "razonSocial": "Constructora Andina S.A.S.",
            "tipoVinculador": "EMPLEADOR",
            "actividadEconomica": { "sistema": "CIIU-4AC", "codigo": "5410101", "descripcion": "Construcción de edificios" },
            "sede": {
              "nombre": "Obra Torre Norte",
              "ubicacion": { "municipio": { "sistema": "DIVIPOLA", "codigo": "05001" }, "zona": "URBANA", "direccion": "Cra 43A 1-50" }
            }
          },
          "trabajador": {
            "identificacion": { "tipo": "CC", "numero": "1020304050" },
            "primerNombre": "Juan",
            "primerApellido": "Pérez",
            "fechaNacimiento": "1990-04-12",
            "sexo": "M",
            "tipoVinculacion": "PLANTA",
            "cargo": "Oficial de obra",
            "fechaIngreso": "2024-02-01",
            "jornadaHabitual": "DIURNA"
          },
          "evento": {
            "fechaHora": "2026-10-07T14:30:00-05:00",
            "jornada": "NORMAL",
            "realizabaLaborHabitual": true,
            "tipo": "PROPIO_DEL_TRABAJO",
            "causoMuerte": false,
            "esGrave": true,
            "lugar": "DENTRO_EMPRESA",
            "sitio": "OTRO",
            "ubicacion": { "municipio": { "sistema": "DIVIPOLA", "codigo": "05001" }, "zona": "URBANA" },
            "tiposLesion": [{ "sistema": "RES156-TIPO-LESION", "codigo": "20", "descripcion": "Fractura" }],
            "partesCuerpo": [{ "sistema": "RES156-PARTE-CUERPO", "codigo": "50", "descripcion": "Miembros superiores" }],
            "agente": { "sistema": "RES156-AGENTE", "codigo": "300", "descripcion": "Andamio" },
            "mecanismo": { "sistema": "RES156-MECANISMO", "codigo": "10", "descripcion": "Caída de personas" },
            "descripcion": "El trabajador cae desde el segundo nivel del andamio al desprenderse una plataforma durante el armado de formaleta."
          },
          "informante": {
            "identificacion": { "tipo": "CC", "numero": "43123456" },
            "nombreCompleto": "María Gómez",
            "cargo": "Coordinadora SST",
            "fechaDiligenciamiento": "2026-10-08T09:00:00-05:00"
          },
          "ipsAtencionInicial": { "codigoReps": "050010212301", "nombre": "Clínica Las Américas" }
        }
        """;

    /// <summary>Un FUREP armado con los mismos datos y un diagnóstico de asma (<c>J45.0</c>).</summary>
    public const string FurepAsma = """
        {
          "version": "1.0",
          "numeroReporteOrigen": "POS-FUREP-2026-000777",
          "empleador": {
            "identificacion": { "tipo": "NIT", "numero": "900123456" },
            "razonSocial": "Constructora Andina S.A.S.",
            "actividadEconomica": { "sistema": "CIIU-4AC", "codigo": "5410101" },
            "sede": { "ubicacion": { "municipio": { "sistema": "DIVIPOLA", "codigo": "11001" }, "zona": "URBANA" } }
          },
          "trabajador": {
            "identificacion": { "tipo": "CC", "numero": "79111222" },
            "primerNombre": "Ana",
            "primerApellido": "Rojas",
            "fechaNacimiento": "1985-01-20",
            "sexo": "F",
            "tipoVinculacion": "PLANTA",
            "cargo": "Pintora",
            "fechaIngreso": "2015-03-01"
          },
          "informante": {
            "identificacion": { "tipo": "CC", "numero": "43123456" },
            "nombreCompleto": "María Gómez",
            "cargo": "Coordinadora SST",
            "fechaDiligenciamiento": "2026-10-08T09:00:00-05:00"
          },
          "enfermedad": {
            "fechaDiagnostico": "2026-10-01",
            "diagnosticos": [{ "sistema": "CIE-10", "codigo": "J45.0", "descripcion": "Asma predominantemente alérgica" }],
            "factoresRiesgo": [{ "descripcion": "Exposición a solventes", "tiempoExposicionMeses": 120 }],
            "medicoDiagnostica": {
              "identificacion": { "tipo": "CC", "numero": "80123123" },
              "nombreCompleto": "Carlos Díaz",
              "registroRethus": "RM-55667"
            }
          }
        }
        """;

    public static JsonObject Objeto(string json) => JsonNode.Parse(json)!.AsObject();
}
