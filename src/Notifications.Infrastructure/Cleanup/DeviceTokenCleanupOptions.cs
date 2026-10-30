namespace Notifications.Infrastructure.Cleanup;

/// <summary>
/// Bound from the "DeviceTokenCleanup" configuration section. Off by default: enable it only once the app
/// re-registers its token on every start, otherwise devices that are still in use look stale and are deleted.
/// </summary>
public sealed class DeviceTokenCleanupOptions
{
    public const string SectionName = "DeviceTokenCleanup";

    /// <summary>The shortest <see cref="StaleAfter"/> allowed, so a typo cannot wipe active devices.</summary>
    public static readonly TimeSpan MinimumStaleAfter = TimeSpan.FromDays(30);

    public bool Enabled { get; set; }

    /// <summary>
    /// A token not re-registered for this long is deleted. Firebase treats a token inactive after 270 days
    /// and rejects it as unregistered from then on.
    /// </summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromDays(270);

    /// <summary>How often the cleanup runs. The first run is one interval after startup.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);
}
