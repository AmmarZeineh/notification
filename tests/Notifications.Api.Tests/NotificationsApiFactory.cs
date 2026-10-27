using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notifications.Application.Abstractions;
using Notifications.Infrastructure.Persistence;
using Notifications.Tests.Shared;

namespace Notifications.Api.Tests;

/// <summary>Runs the API against in-memory SQLite and a substitute push sender (no Firebase or SQL Server needed).</summary>
public sealed class NotificationsApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public IPushNotificationSender Sender { get; } = Substitute.For<IPushNotificationSender>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        // Added last so it overrides any Firebase settings on the developer's machine (e.g. user-secrets).
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Firebase:CredentialsPath"] = null,
            ["Firebase:CredentialsJson"] = "{}",
            ["Firebase:UseApplicationDefaultCredentials"] = "false",
            // Never used (the DbContext is replaced with SQLite below); only satisfies the startup check.
            ["ConnectionStrings:Notifications"] = "Server=unused",
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<NotificationsDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NotificationsDbContext>>();

            _connection.Open();
            _connection.UseSqlServerCaseSensitiveCollation();
            services.AddDbContext<NotificationsDbContext>(options =>
                ((DbContextOptionsBuilder<NotificationsDbContext>)options).UseSqliteAsSqlServer(_connection));

            services.RemoveAll<IPushNotificationSender>();
            services.AddSingleton(Sender);
        });
    }

    public async Task InitializeDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.EnsureCreatedAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
