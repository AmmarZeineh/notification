using Microsoft.EntityFrameworkCore;

namespace Notifications.Infrastructure.Persistence;

/// <summary>
/// Standalone context used when the feature runs on its own. A host with an existing DbContext
/// calls <see cref="ModelBuilderExtensions.ApplyNotificationsConfiguration"/> instead.
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyNotificationsConfiguration();
}
