using System.Collections.Frozen;

namespace Halsa.Backend.Domain.Webhooks;

/// <summary>A quién va un tipo de evento público: solo una ARL o solo una IPS puede suscribirse a él.</summary>
public enum DestinatarioWebhook
{
    Arl,
    Ips
}

/// <summary>
/// El catálogo de los 10 eventos públicos de la SPEC 10. Ninguno lleva diagnósticos, contenido clínico ni fundamentos.
/// </summary>
public static class EventosWebhook
{
    public const string PlazoProximoAVencer = "plazo.proximo-a-vencer";
    public const string PlazoVencido = "plazo.vencido";
    public const string CasoValidado = "caso.validado";
    public const string CasoRechazado = "caso.rechazado";
    public const string IncapacidadRegistrada = "incapacidad.registrada";
    public const string ConceptoRegistrado = "concepto.registrado";
    public const string DictamenNotificado = "dictamen.notificado";
    public const string AccesoEmergencia = "auditoria.acceso-emergencia";
    public const string CasoRemitido = "caso.remitido";
    public const string RemisionRevocada = "remision.revocada";

    public static readonly FrozenDictionary<string, DestinatarioWebhook> Catalogo =
        new Dictionary<string, DestinatarioWebhook>(StringComparer.Ordinal)
        {
            [PlazoProximoAVencer] = DestinatarioWebhook.Arl,
            [PlazoVencido] = DestinatarioWebhook.Arl,
            [CasoValidado] = DestinatarioWebhook.Arl,
            [CasoRechazado] = DestinatarioWebhook.Arl,
            [IncapacidadRegistrada] = DestinatarioWebhook.Arl,
            [ConceptoRegistrado] = DestinatarioWebhook.Arl,
            [DictamenNotificado] = DestinatarioWebhook.Arl,
            [AccesoEmergencia] = DestinatarioWebhook.Arl,
            [CasoRemitido] = DestinatarioWebhook.Ips,
            [RemisionRevocada] = DestinatarioWebhook.Ips
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>El tipo existe y su destinatario es <paramref name="destinatario"/>.</summary>
    public static bool Admite(string tipo, DestinatarioWebhook destinatario) =>
        Catalogo.TryGetValue(tipo, out DestinatarioWebhook propio) && propio == destinatario;
}
