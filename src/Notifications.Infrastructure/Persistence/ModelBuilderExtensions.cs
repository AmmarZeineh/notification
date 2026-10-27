using Microsoft.EntityFrameworkCore;
using Notifications.Infrastructure.Persistence.Configurations;

namespace Notifications.Infrastructure.Persistence;

public static class ModelBuilderExtensions
{
    /// <summary>Adds the notification tables to any DbContext (call from <c>OnModelCreating</c>).</summary>
    public static ModelBuilder ApplyNotificationsConfiguration(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        return modelBuilder.ApplyConfiguration(new DeviceTokenConfiguration());
    }
}
