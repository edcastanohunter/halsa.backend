using Halsa.Backend.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Halsa.Backend.FunctionalTests.Abstractions;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("halsa.backend")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:Database", _container.GetConnectionString());

        builder.ConfigureTestServices(services =>
        {
            // Stub identity: registered last, so it becomes the default scheme.
            services
                .AddAuthentication(TestAuthenticationHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName,
                    _ => { });

            // The outbox dispatcher would race with assertions; drain it explicitly instead.
            services.RemoveAll<IHostedService>();
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _container.StartAsync();

        using IServiceScope scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await context.Database.EnsureCreatedAsync();
    }

    // Explicit implementation: WebApplicationFactory already exposes ValueTask DisposeAsync(),
    // and two methods cannot differ by return type alone.
    async Task IAsyncLifetime.DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>;
