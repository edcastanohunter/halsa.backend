namespace Halsa.Backend.Infrastructure.Terminos;

/// <summary>Una fila de <c>sst.festivos</c>. <see cref="Origen"/> es <c>CALCULADO</c> (lo carga el job) o <c>MANUAL</c>.</summary>
public sealed class FestivoFila
{
    public const string Calculado = "CALCULADO";
    public const string Manual = "MANUAL";

    public DateOnly Fecha { get; set; }

    public string Descripcion { get; set; } = string.Empty;

    public string Origen { get; set; } = Calculado;
}
