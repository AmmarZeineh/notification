using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Notifications.Application.Abstractions;
using Notifications.Infrastructure.Cleanup;
using Notifications.Infrastructure.Firebase;
using Notifications.Infrastructure.Persistence;

namespace Notifications.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers Firebase messaging and the device-token store on <typeparamref name="TDbContext"/>,
    /// which must already be registered and include the notification model
    /// (see <see cref="ModelBuilderExtensions.ApplyNotificationsConfiguration"/>).
    /// </summary>
    public static IServiceCollection AddNotificationsInfrastructure<TDbContext>(
        this IServiceCollection services, IConfiguration configuration)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FirebaseOptions>()
            .Bind(configuration.GetSection(FirebaseOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<FirebaseOptions>, FirebaseCredentialsPathResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FirebaseOptions>, FirebaseOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<FcmMessageFactory>();
        services.TryAddSingleton<IPushNotificationSender, FirebaseMessagingSender>();

        // The request's own DbContext, so per-request state the host's context relies on (tenant, audit user,
        // transaction) applies to device tokens too. See INTEGRATION.md about pending changes being saved with it.
        services.TryAddScoped<IDeviceTokenRepository>(sp => new DeviceTokenRepository(sp.GetRequiredService<TDbContext>()));

        services.AddOptions<DeviceTokenCleanupOptions>()
            .Bind(configuration.GetSection(DeviceTokenCleanupOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DeviceTokenCleanupOptions>, DeviceTokenCleanupOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddHostedService<DeviceTokenCleanupService>();

        return services;
    }
}
