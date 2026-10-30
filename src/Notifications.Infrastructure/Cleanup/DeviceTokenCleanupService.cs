using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Abstractions;

namespace Notifications.Infrastructure.Cleanup;

/// <summary>
/// Periodically deletes device tokens the app has not re-registered for <see cref="DeviceTokenCleanupOptions.StaleAfter"/>.
/// Firebase rejects such tokens anyway, so keeping them only wastes sends and inflates device counts.
/// </summary>
internal sealed partial class DeviceTokenCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<DeviceTokenCleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<DeviceTokenCleanupService> logger) : BackgroundService
{
    private readonly DeviceTokenCleanupOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // A failed run (e.g. the database is briefly down) must not stop the host; the next run retries.
                LogCleanupFailed(ex);
            }
        }
    }

    /// <summary>Deletes the stale tokens once. Returns the number removed.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        // The repository uses the host's scoped DbContext, so each run gets its own scope.
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var cutoff = timeProvider.GetUtcNow() - _options.StaleAfter;
            var removed = await scope.ServiceProvider
                .GetRequiredService<IDeviceTokenRepository>()
                .RemoveTokensLastSeenBeforeAsync(cutoff, cancellationToken)
                .ConfigureAwait(false);

            LogStaleTokensRemoved(removed, cutoff);
            return removed;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} device token(s) not seen since {Cutoff}")]
    private partial void LogStaleTokensRemoved(int count, DateTimeOffset cutoff);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stale device token cleanup failed; it runs again at the next interval")]
    private partial void LogCleanupFailed(Exception exception);
}
