using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Halsa.Backend.Infrastructure.Archivos;

/// <summary>
/// Un cliente mínimo del protocolo de <c>clamd</c> sobre TCP: <c>zPING</c> e <c>zINSTREAM</c>. Los comandos con el
/// prefijo <c>z</c> terminan en <c>\0</c>, y la respuesta también. Una conexión por comando: <c>clamd</c> la cierra
/// después de responder.
/// </summary>
internal sealed class ClamdClient(string host, int puerto)
{
    /// <summary>Lo que se envía por fragmento de <c>INSTREAM</c>.</summary>
    private const int TamanoFragmento = 64 * 1024;

    /// <summary><c>"PONG"</c> si <c>clamd</c> está vivo.</summary>
    public Task<string> PingAsync(CancellationToken ct) => ComandoAsync("zPING\0", cuerpo: null, ct);

    /// <summary>
    /// Envía <paramref name="contenido"/> en fragmentos (<c>longitud big-endian de 4 bytes ‖ datos</c>) y un fragmento
    /// vacío al final. Responde <c>"stream: OK"</c>, <c>"stream: {firma} FOUND"</c> o un <c>"... ERROR"</c>.
    /// </summary>
    public Task<string> InstreamAsync(Stream contenido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contenido);

        return ComandoAsync("zINSTREAM\0", red => EnviarFragmentosAsync(contenido, red, ct), ct);
    }

    private async Task<string> ComandoAsync(string comando, Func<NetworkStream, Task>? cuerpo, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, puerto, ct);

        NetworkStream red = tcp.GetStream();
        await red.WriteAsync(Encoding.ASCII.GetBytes(comando), ct);

        if (cuerpo is not null)
        {
            await cuerpo(red);
        }

        await red.FlushAsync(ct);

        return await LeerRespuestaAsync(red, ct);
    }

    private static async Task EnviarFragmentosAsync(Stream contenido, NetworkStream red, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(TamanoFragmento + sizeof(int));
        try
        {
            int leidos;
            while ((leidos = await contenido.ReadAsync(buffer.AsMemory(sizeof(int), TamanoFragmento), ct)) > 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(buffer, leidos);
                await red.WriteAsync(buffer.AsMemory(0, sizeof(int) + leidos), ct);
            }

            BinaryPrimitives.WriteInt32BigEndian(buffer, 0);
            await red.WriteAsync(buffer.AsMemory(0, sizeof(int)), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Hasta el <c>\0</c> o hasta que <c>clamd</c> cierre la conexión.</summary>
    private static async Task<string> LeerRespuestaAsync(NetworkStream red, CancellationToken ct)
    {
        using var respuesta = new MemoryStream();
        byte[] buffer = new byte[256];

        int leidos;
        while ((leidos = await red.ReadAsync(buffer, ct)) > 0)
        {
            int fin = Array.IndexOf(buffer, (byte)0, 0, leidos);
            respuesta.Write(buffer, 0, fin < 0 ? leidos : fin);

            if (fin >= 0)
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(respuesta.GetBuffer(), 0, (int)respuesta.Length).Trim();
    }
}
