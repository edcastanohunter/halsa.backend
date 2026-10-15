using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Halsa.Backend.FunctionalTests.Abstractions;

/// <summary>
/// The TestServer has no TCP connection, so <c>RemoteIpAddress</c> is <c>null</c>. With <see cref="Cabecera"/> a test
/// says where the request comes from (SPEC 13: the trusted proxy or anyone else). Runs before the API's pipeline.
/// </summary>
internal sealed class RemoteIpDePrueba : IStartupFilter
{
    public const string Cabecera = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, siguiente) =>
        {
            if (context.Request.Headers.TryGetValue(Cabecera, out var valor))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(valor.ToString());
                context.Request.Headers.Remove(Cabecera);
            }

            return siguiente(context);
        });

        next(app);
    };
}
