using Notifications.Application.Common;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Application.Services;

/// <summary>Manages which FCM tokens belong to which user.</summary>
public interface IDeviceRegistrationService
{
    /// <summary>
    /// Registers a token for the user. Idempotent: re-registering refreshes it.
    /// A token owned by another user is never moved; this returns
    /// <see cref="NotificationErrors.DeviceOwnedByAnotherUser"/> (409), and the app must delete its token and
    /// register the new one. The app deletes its token on sign-out, so shared devices get a fresh token anyway.
    /// </summary>
    Task<Result> RegisterAsync(
        string userId, string token, DevicePlatform platform, CancellationToken cancellationToken = default);

    /// <summary>Removes the token if it belongs to the user, typically on sign-out.</summary>
    Task<Result> UnregisterAsync(string userId, string token, CancellationToken cancellationToken = default);
}
