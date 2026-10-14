using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Incapacidades;

/// <summary>
/// Un certificado de incapacidad temporal (SPEC 07, Ley 776/2002): su parte administrativa, en <c>atel</c>. El
/// diagnóstico y las observaciones viven cifrados en la historia clínica (<see cref="RegistroClinicoId"/>) y el PDF en
/// <c>clinico.soportes</c> (<see cref="SoporteId"/>); aquí solo queda el índice ciego de la categoría CIE-10, para
/// comparar una prórroga con su inicial sin descifrar nada.
///
/// Las incapacidades de un caso forman cadenas: una <see cref="TipoIncapacidad.Inicial"/> y sus prórrogas, en orden,
/// todas con el mismo <see cref="CadenaId"/> (el id de la inicial). Inmutable: no hay métodos de cambio.
/// </summary>
public sealed class Incapacidad : AggregateRoot<IncapacidadId>
{
    /// <summary>Lo que dura un certificado como máximo.</summary>
    public const int MaxDias = 30;

    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    // Parameterless constructor required by EF Core materializer.
    private Incapacidad() { }

    // ── State ────────────────────────────────────────────────────────────────

    public TenantId TenantId { get; private set; }

    public CasoMedicoId CasoMedicoId { get; private set; }

    public string NumeroCertificado { get; private set; } = string.Empty;

    public IpsEmisora IpsEmisora { get; private set; } = null!;

    public TipoIncapacidad Tipo { get; private set; }

    public IncapacidadId? ProrrogaDe { get; private set; }

    /// <summary>El id de la <see cref="TipoIncapacidad.Inicial"/> de la cadena (el propio, si esta es la inicial).</summary>
    public IncapacidadId CadenaId { get; private set; }

    public DateOnly FechaInicio { get; private set; }

    public int Dias { get; private set; }

    /// <summary><see cref="FechaInicio"/> + <see cref="Dias"/> − 1: el último día incapacitado, calculado aquí.</summary>
    public DateOnly FechaFin { get; private set; }

    /// <summary>La suma de los días de la cadena, incluida esta incapacidad.</summary>
    public int DiasAcumulados { get; private set; }

    public ModalidadIncapacidad Modalidad { get; private set; }

    public EstadoIncapacidad Estado { get; private set; }

    public ProfesionalIncapacidad Profesional { get; private set; } = null!;

    /// <summary>HMAC, con la DEK <c>INDICE</c> del tenant, de <c>"CIE-10|{categoría}"</c>.</summary>
    public byte[] DiagnosticoCategoriaIndice { get; private set; } = [];

    public Guid SoporteId { get; private set; }

    public Guid RegistroClinicoId { get; private set; }

    public DateTimeOffset RegistradaEn { get; private set; }

    /// <summary>El <c>sub</c> de quien la cargó.</summary>
    public string RegistradaPor { get; private set; } = string.Empty;

    public string? ClientId { get; private set; }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Aplica las reglas de la SPEC 07 contra las incapacidades que el caso ya tiene, en este orden: el estado del
    /// caso, la fecha del evento, el solapamiento y, para una prórroga, su anterior, que sea la última de la cadena,
    /// la continuidad y el diagnóstico.
    /// </summary>
    /// <param name="delCaso">Todas las incapacidades del caso.</param>
    public static Result<Incapacidad> Registrar(
        DatosIncapacidad datos,
        CasoParaIncapacidad caso,
        IReadOnlyCollection<Incapacidad> delCaso,
        ParametrosIncapacidad parametros,
        DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(datos);
        ArgumentNullException.ThrowIfNull(caso);
        ArgumentNullException.ThrowIfNull(delCaso);
        ArgumentNullException.ThrowIfNull(parametros);

        if (datos.Dias is < 1 or > MaxDias)
        {
            return Result.Failure<Incapacidad>(IncapacidadErrors.DiasFueraDeRango);
        }

        if (datos.Tipo == TipoIncapacidad.Inicial && datos.ProrrogaDe is not null)
        {
            return Result.Failure<Incapacidad>(IncapacidadErrors.InicialConProrrogaDe);
        }

        if (caso.Estado is EstadoCaso.Cerrado or EstadoCaso.Rechazado)
        {
            return Result.Failure<Incapacidad>(IncapacidadErrors.CasoCerrado);
        }

        if (datos.FechaInicio < FechaColombia(caso.FechaEvento))
        {
            return Result.Failure<Incapacidad>(IncapacidadErrors.FechaAnteriorAlEvento);
        }

        DateOnly fechaFin = datos.FechaInicio.AddDays(datos.Dias - 1);

        if (delCaso.Any(otra => otra.FechaInicio <= fechaFin && datos.FechaInicio <= otra.FechaFin))
        {
            return Result.Failure<Incapacidad>(IncapacidadErrors.Solapada);
        }

        Result<(IncapacidadId CadenaId, int DiasPrevios)> cadena = datos.Tipo == TipoIncapacidad.Inicial
            ? Result.Success((datos.Id, 0))
            : Continuar(datos, delCaso, parametros);

        if (cadena.IsFailure)
        {
            return Result.Failure<Incapacidad>(cadena.Error);
        }

        return Result.Success(new Incapacidad
        {
            Id = datos.Id,
            TenantId = caso.TenantId,
            CasoMedicoId = datos.CasoMedicoId,
            NumeroCertificado = datos.NumeroCertificado,
            IpsEmisora = datos.IpsEmisora,
            Tipo = datos.Tipo,
            ProrrogaDe = datos.ProrrogaDe,
            CadenaId = cadena.Value.CadenaId,
            FechaInicio = datos.FechaInicio,
            Dias = datos.Dias,
            FechaFin = fechaFin,
            DiasAcumulados = cadena.Value.DiasPrevios + datos.Dias,
            Modalidad = datos.Modalidad,
            Estado = EstadoIncapacidad.Registrada,
            Profesional = datos.Profesional,
            DiagnosticoCategoriaIndice = datos.DiagnosticoCategoriaIndice,
            SoporteId = datos.SoporteId,
            RegistroClinicoId = datos.RegistroClinicoId,
            RegistradaEn = ahora,
            RegistradaPor = datos.RegistradaPor,
            ClientId = datos.ClientId
        });
    }

    /// <summary>Las alertas de esta incapacidad, con sus días acumulados y su fecha de carga.</summary>
    public IReadOnlyList<string> Alertas(ParametrosIncapacidad parametros) =>
        AlertasIncapacidad.Calcular(DiasAcumulados, FechaInicio, RegistradaEn, parametros);

    /// <summary>La fecha civil de <paramref name="momento"/> en Colombia (UTC−5, sin horario de verano).</summary>
    public static DateOnly FechaColombia(DateTimeOffset momento) =>
        DateOnly.FromDateTime(momento.ToOffset(ZonaColombia).DateTime);

    /// <summary>Las reglas de una prórroga. Devuelve la cadena y los días que ya acumula.</summary>
    private static Result<(IncapacidadId, int)> Continuar(
        DatosIncapacidad datos,
        IReadOnlyCollection<Incapacidad> delCaso,
        ParametrosIncapacidad parametros)
    {
        Incapacidad? anterior = datos.ProrrogaDe is IncapacidadId prorrogaDe
            ? delCaso.FirstOrDefault(i => i.Id == prorrogaDe)
            : null;

        if (anterior is null)
        {
            return Result.Failure<(IncapacidadId, int)>(IncapacidadErrors.ProrrogaSinInicial);
        }

        Incapacidad[] cadena = [.. delCaso.Where(i => i.CadenaId == anterior.CadenaId)];

        if (cadena.Any(i => i.FechaInicio > anterior.FechaInicio))
        {
            return Result.Failure<(IncapacidadId, int)>(IncapacidadErrors.ProrrogaNoEsUltima);
        }

        if (datos.FechaInicio.DayNumber - anterior.FechaFin.DayNumber > parametros.MaxDiasEntreProrrogas)
        {
            return Result.Failure<(IncapacidadId, int)>(IncapacidadErrors.ProrrogaDiscontinua);
        }

        Incapacidad inicial = cadena.Single(i => i.Id == anterior.CadenaId);
        if (!inicial.DiagnosticoCategoriaIndice.AsSpan().SequenceEqual(datos.DiagnosticoCategoriaIndice))
        {
            return Result.Failure<(IncapacidadId, int)>(IncapacidadErrors.ProrrogaDiagnosticoNoRelacionado);
        }

        return Result.Success((anterior.CadenaId, anterior.DiasAcumulados));
    }
}
