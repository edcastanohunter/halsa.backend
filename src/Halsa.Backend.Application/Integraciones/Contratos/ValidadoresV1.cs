using FluentValidation;

namespace Halsa.Backend.Application.Integraciones.Contratos;

// Validación sintáctica de la versión 1.0, según docs/api/atel-api.openapi.yaml: obligatorios, longitudes,
// patrones y enumerados. Solo el formato de los códigos; validarlos contra los catálogos (CIIU, DIVIPOLA,
// Res. 156) es otra spec. Los validadores de los tipos compartidos no tienen parámetros de constructor:
// el escaneo del ensamblado los registra, y el contenedor tiene que poder construirlos.

/// <summary>Valores admitidos por los enumerados del contrato.</summary>
internal static class ValoresV1
{
    public const string Version = "1.0";

    public static readonly string[] TiposDocumento = ["CC", "CE", "TI", "PA", "PE", "PT", "NIT"];

    public static readonly string[] Sistemas =
    [
        SistemaCie10, SistemaCiiu, SistemaDivipola, "RES156-TIPO-LESION", "RES156-PARTE-CUERPO",
        "RES156-AGENTE", "RES156-MECANISMO", "CIUO-08AC"
    ];

    public const string SistemaCie10 = "CIE-10";
    public const string SistemaCiiu = "CIIU-4AC";
    public const string SistemaDivipola = "DIVIPOLA";

    public static readonly string[] Zonas = ["URBANA", "RURAL"];
    public static readonly string[] TiposVinculador = ["EMPLEADOR", "CONTRATANTE", "COOPERATIVA", "EMPRESA_SERVICIOS_TEMPORALES"];
    public static readonly string[] Sexos = ["F", "M", "I"];
    public static readonly string[] TiposVinculacion = ["PLANTA", "MISION", "COOPERADO", "ESTUDIANTE_APRENDIZ", "INDEPENDIENTE"];
    public static readonly string[] JornadasHabituales = ["DIURNA", "NOCTURNA", "MIXTA", "TURNOS"];
    public static readonly string[] JornadasEvento = ["NORMAL", "EXTRA"];
    public static readonly string[] TiposEvento = ["PROPIO_DEL_TRABAJO", "VIOLENCIA", "TRANSITO", "DEPORTIVO", "RECREATIVO_CULTURAL"];
    public static readonly string[] Lugares = ["DENTRO_EMPRESA", "FUERA_EMPRESA"];
    public static readonly string[] Sitios = ["ALMACEN", "CORREDOR", "PARQUEADERO", "OFICINA", "PLANTA", "VIA_PUBLICA", "OTRO"];

    /// <summary>Colombia no tiene horario de verano: la zona es siempre -05:00.</summary>
    public static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);
}

internal static class MensajesV1
{
    public const string Requerido = "Es obligatorio.";
    public const string NoPermitido = "El valor '{PropertyValue}' no está permitido.";
    public const string Formato = "Formato inválido.";
    public const string MaximoCaracteres = "Admite como máximo {MaxLength} caracteres.";
    public const string FechaFutura = "La fecha del evento no puede ser futura.";
}

internal static class ReglasV1
{
    public static IRuleBuilderOptions<T, string?> Requerido<T>(this IRuleBuilder<T, string?> regla, int maximo) =>
        regla.NotEmpty().WithMessage(MensajesV1.Requerido)
            .MaximumLength(maximo).WithMessage(MensajesV1.MaximoCaracteres);

    public static IRuleBuilderOptions<T, string?> Opcional<T>(this IRuleBuilder<T, string?> regla, int maximo) =>
        regla.MaximumLength(maximo).WithMessage(MensajesV1.MaximoCaracteres);

    /// <summary>Si viene, tiene que ser uno de <paramref name="valores"/>.</summary>
    public static IRuleBuilderOptions<T, string?> EnLista<T>(this IRuleBuilder<T, string?> regla, string[] valores) =>
        regla.Must(valor => valor is null || valores.Contains(valor, StringComparer.Ordinal))
            .WithMessage(MensajesV1.NoPermitido);

    public static IRuleBuilderOptions<T, TProperty?> Presente<T, TProperty>(this IRuleBuilder<T, TProperty?> regla) =>
        regla.NotNull().WithMessage(MensajesV1.Requerido);
}

internal sealed class IdentificacionV1Validator : AbstractValidator<IdentificacionV1>
{
    public IdentificacionV1Validator()
    {
        RuleFor(x => x.Tipo).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.TiposDocumento);
        RuleFor(x => x.Numero)
            .NotEmpty().WithMessage(MensajesV1.Requerido)
            .Matches("^[A-Za-z0-9]{3,20}$").WithMessage(MensajesV1.Formato);
    }
}

internal sealed class CodificadoV1Validator : AbstractValidator<CodificadoV1>
{
    public CodificadoV1Validator()
    {
        RuleFor(x => x.Sistema).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.Sistemas);
        RuleFor(x => x.Codigo).Requerido(20);
        RuleFor(x => x.Descripcion).Opcional(250);
    }
}

internal sealed class UbicacionV1Validator : AbstractValidator<UbicacionV1>
{
    public UbicacionV1Validator()
    {
        RuleFor(x => x.Municipio).Presente().SetValidator(new CodificadoV1Validator()!);
        RuleFor(x => x.Municipio!.Sistema)
            .Equal(ValoresV1.SistemaDivipola).WithMessage($"Debe ser '{ValoresV1.SistemaDivipola}'.")
            .When(x => x.Municipio?.Sistema is not null);
        RuleFor(x => x.Municipio!.Codigo)
            .Matches("^[0-9]{5}$").WithMessage("El código DIVIPOLA tiene 5 dígitos.")
            .When(x => x.Municipio?.Codigo is not null);
        RuleFor(x => x.Zona).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.Zonas);
        RuleFor(x => x.Direccion).Opcional(200);
    }
}

internal sealed class EmpleadorV1Validator : AbstractValidator<EmpleadorV1>
{
    public EmpleadorV1Validator()
    {
        RuleFor(x => x.Identificacion).Presente().SetValidator(new IdentificacionV1Validator()!);
        RuleFor(x => x.RazonSocial).Requerido(200);
        RuleFor(x => x.TipoVinculador).EnLista(ValoresV1.TiposVinculador);
        RuleFor(x => x.ActividadEconomica).Presente().SetValidator(new CodificadoV1Validator()!);
        RuleFor(x => x.ActividadEconomica!.Sistema)
            .Equal(ValoresV1.SistemaCiiu).WithMessage($"Debe ser '{ValoresV1.SistemaCiiu}'.")
            .When(x => x.ActividadEconomica?.Sistema is not null);
        RuleFor(x => x.Sede).Presente().ChildRules(sede =>
        {
            sede.RuleFor(s => s.Nombre).Opcional(120);
            sede.RuleFor(s => s.Ubicacion).Presente().SetValidator(new UbicacionV1Validator()!);
            sede.RuleFor(s => s.Telefono).Opcional(20);
        });
    }
}

internal sealed class TrabajadorV1Validator : AbstractValidator<TrabajadorV1>
{
    public TrabajadorV1Validator()
    {
        RuleFor(x => x.Identificacion).Presente().SetValidator(new IdentificacionV1Validator()!);
        RuleFor(x => x.PrimerNombre).Requerido(60);
        RuleFor(x => x.SegundoNombre).Opcional(60);
        RuleFor(x => x.PrimerApellido).Requerido(60);
        RuleFor(x => x.SegundoApellido).Opcional(60);
        RuleFor(x => x.FechaNacimiento).Presente();
        RuleFor(x => x.Sexo).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.Sexos);
        RuleFor(x => x.TipoVinculacion).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.TiposVinculacion);
        RuleFor(x => x.Cargo).Requerido(120);
        RuleFor(x => x.Ocupacion).SetValidator(new CodificadoV1Validator()!);
        RuleFor(x => x.FechaIngreso).Presente();
        RuleFor(x => x.JornadaHabitual).EnLista(ValoresV1.JornadasHabituales);
        RuleFor(x => x.Contacto).ChildRules(contacto =>
        {
            contacto.RuleFor(c => c.Telefono).Opcional(20);
            contacto.RuleFor(c => c.Correo).EmailAddress().WithMessage(MensajesV1.Formato);
        });
    }
}

internal sealed class InformanteV1Validator : AbstractValidator<InformanteV1>
{
    public InformanteV1Validator()
    {
        RuleFor(x => x.Identificacion).Presente().SetValidator(new IdentificacionV1Validator()!);
        RuleFor(x => x.NombreCompleto).Requerido(200);
        RuleFor(x => x.Cargo).Requerido(120);
        RuleFor(x => x.FechaDiligenciamiento).Presente();
    }
}

internal sealed class ProfesionalV1Validator : AbstractValidator<ProfesionalV1>
{
    public ProfesionalV1Validator()
    {
        RuleFor(x => x.Identificacion).Presente().SetValidator(new IdentificacionV1Validator()!);
        RuleFor(x => x.NombreCompleto).Requerido(200);
        RuleFor(x => x.RegistroRethus).Requerido(30);
        RuleFor(x => x.Especialidad).Opcional(120);
    }
}

/// <summary>El FURAT 1.0. <c>evento.fechaHora</c> no puede ser futura.</summary>
internal sealed class FuratV1Validator : AbstractValidator<FuratV1>
{
    public FuratV1Validator(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        RuleFor(x => x.Version)
            .NotEmpty().WithMessage(MensajesV1.Requerido)
            .Equal(ValoresV1.Version).WithMessage($"Debe ser '{ValoresV1.Version}'.");
        RuleFor(x => x.NumeroReporteOrigen).Requerido(40);
        RuleFor(x => x.Empleador).Presente().SetValidator(new EmpleadorV1Validator()!);
        RuleFor(x => x.Trabajador).Presente().SetValidator(new TrabajadorV1Validator()!);
        RuleFor(x => x.Informante).Presente().SetValidator(new InformanteV1Validator()!);

        RuleFor(x => x.Evento).Presente().ChildRules(evento =>
        {
            evento.RuleFor(e => e.FechaHora)
                .Presente()
                .Must(fecha => fecha is null || fecha <= time.GetUtcNow()).WithMessage(MensajesV1.FechaFutura);
            evento.RuleFor(e => e.Jornada).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.JornadasEvento);
            evento.RuleFor(e => e.RealizabaLaborHabitual).Presente();
            evento.RuleFor(e => e.LaborRealizada).Opcional(200);
            evento.RuleFor(e => e.TiempoLaboradoPrevioMinutos)
                .GreaterThanOrEqualTo(0).WithMessage("No puede ser negativo.");
            evento.RuleFor(e => e.Tipo).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.TiposEvento);
            evento.RuleFor(e => e.CausoMuerte).Presente();
            evento.RuleFor(e => e.Lugar).NotEmpty().WithMessage(MensajesV1.Requerido).EnLista(ValoresV1.Lugares);
            evento.RuleFor(e => e.Sitio).EnLista(ValoresV1.Sitios);
            evento.RuleFor(e => e.Ubicacion).Presente().SetValidator(new UbicacionV1Validator()!);
            evento.RuleForEach(e => e.TiposLesion).Presente().SetValidator(new CodificadoV1Validator()!);
            evento.RuleForEach(e => e.PartesCuerpo).Presente().SetValidator(new CodificadoV1Validator()!);
            evento.RuleFor(e => e.Agente).SetValidator(new CodificadoV1Validator()!);
            evento.RuleFor(e => e.Mecanismo).SetValidator(new CodificadoV1Validator()!);
            evento.RuleFor(e => e.Descripcion)
                .NotEmpty().WithMessage(MensajesV1.Requerido)
                .Length(20, 4000).WithMessage("Debe tener entre {MinLength} y {MaxLength} caracteres.");
        });

        RuleFor(x => x.Testigos)
            .Must(testigos => testigos is null || testigos.Count <= 5).WithMessage("Admite como máximo 5 testigos.");
        RuleForEach(x => x.Testigos).Presente().ChildRules(testigo =>
        {
            testigo.RuleFor(t => t.Identificacion).Presente().SetValidator(new IdentificacionV1Validator()!);
            testigo.RuleFor(t => t.NombreCompleto).Requerido(200);
            testigo.RuleFor(t => t.Cargo).Opcional(120);
        });

        RuleFor(x => x.IpsAtencionInicial).ChildRules(ips =>
        {
            ips.RuleFor(i => i.CodigoReps)
                .Matches("^[0-9]{10,12}$").WithMessage(MensajesV1.Formato);
            ips.RuleFor(i => i.Nombre).Opcional(200);
        });
    }
}

/// <summary>El FUREP 1.0. <c>enfermedad.fechaDiagnostico</c> no puede ser futura en Colombia.</summary>
internal sealed class FurepV1Validator : AbstractValidator<FurepV1>
{
    public FurepV1Validator(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);

        RuleFor(x => x.Version)
            .NotEmpty().WithMessage(MensajesV1.Requerido)
            .Equal(ValoresV1.Version).WithMessage($"Debe ser '{ValoresV1.Version}'.");
        RuleFor(x => x.NumeroReporteOrigen).Requerido(40);
        RuleFor(x => x.Empleador).Presente().SetValidator(new EmpleadorV1Validator()!);
        RuleFor(x => x.Trabajador).Presente().SetValidator(new TrabajadorV1Validator()!);
        RuleFor(x => x.Informante).Presente().SetValidator(new InformanteV1Validator()!);

        RuleFor(x => x.Enfermedad).Presente().ChildRules(enfermedad =>
        {
            enfermedad.RuleFor(e => e.FechaDiagnostico)
                .Presente()
                .Must(fecha => fecha is null || fecha <= HoyEnColombia(time))
                .WithMessage("La fecha del diagnóstico no puede ser futura.");
            enfermedad.RuleFor(e => e.Diagnosticos)
                .Must(diagnosticos => diagnosticos is { Count: > 0 }).WithMessage("Debe incluir al menos un diagnóstico.");
            enfermedad.RuleForEach(e => e.Diagnosticos).Presente().SetValidator(new CodificadoV1Validator()!);
            enfermedad.RuleForEach(e => e.FactoresRiesgo).Presente().ChildRules(factor =>
            {
                factor.RuleFor(f => f.Descripcion).Opcional(300);
                factor.RuleFor(f => f.TiempoExposicionMeses)
                    .GreaterThanOrEqualTo(0).WithMessage("No puede ser negativo.");
            });
            enfermedad.RuleFor(e => e.MedicoDiagnostica).Presente().SetValidator(new ProfesionalV1Validator()!);
        });
    }

    private static DateOnly HoyEnColombia(TimeProvider time) =>
        DateOnly.FromDateTime(time.GetUtcNow().ToOffset(ValoresV1.ZonaColombia).DateTime);
}
