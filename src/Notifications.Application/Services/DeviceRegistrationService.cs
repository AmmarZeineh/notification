using Microsoft.Extensions.Logging;
using Notifications.Application.Abstractions;
using Notifications.Application.Common;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Application.Services;

internal sealed partial class DeviceRegistrationService(
    IDeviceTokenRepository deviceTokens,
    TimeProvider timeProvider,
    ILogger<DeviceRegistrationService> logger) : IDeviceRegistrationService
{
    private const int MaxRegisterAttempts = 3;

    public async Task<Result> RegisterAsync(
        string userId, string token, DevicePlatform platform, CancellationToken cancellationToken = default)
    {
        token = token?.Trim() ?? string.Empty;
        if (Validate(userId, token, platform) is { } error)
        {
            return error;
        }

        // A concurrent register (double tap) or unregister (sign-out) of the same token can win the race.
        // The repository then discards the rejected change, and a retry from a fresh read applies cleanly.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RegisterOnceAsync(userId, token, platform, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceTokenConflictException) when (attempt < MaxRegisterAttempts)
            {
                LogRegisterConflict(attempt);
            }
            catch (DeviceTokenConflictException)
            {
                return NotificationErrors.RegistrationConflict;
            }
        }
    }

    private async Task<Result> RegisterOnceAsync(
        string userId, string token, DevicePlatform platform, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var existing = await deviceTokens.GetByTokenAsync(token, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            deviceTokens.Add(DeviceToken.Create(userId, token, platform, now));
        }
        else if (!existing.IsOwnedBy(userId))
        {
            // Knowing a token is no proof of owning the device, so it is never moved to another user.
            // The app deletes its token on sign-out, and on this error, so the next user registers a new one.
            LogTokenOwnedByAnotherUser(existing.Id);
            return NotificationErrors.DeviceOwnedByAnotherUser;
        }
        else
        {
            existing.Refresh(platform, now);
        }

        await deviceTokens.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> UnregisterAsync(string userId, string token, CancellationToken cancellationToken = default)
    {
        // The same rules as registration, so a malformed token is a 400 rather than a 404 from the delete.
        if (!DeviceToken.IsValidUserId(userId))
        {
            return NotificationErrors.InvalidUserId;
        }

        token = token?.Trim() ?? string.Empty;
        if (!DeviceToken.IsValidToken(token))
        {
            return NotificationErrors.InvalidToken;
        }

        // A single conditional delete: another user's token is reported as "not found" so callers cannot
        // probe for foreign tokens, and a concurrent sign-in by another user on this device is never deleted.
        var removed = await deviceTokens.RemoveUserTokenAsync(userId, token, cancellationToken).ConfigureAwait(false);
        return removed ? Result.Success() : NotificationErrors.DeviceNotFound;
    }

    private static Error? Validate(string userId, string token, DevicePlatform platform) =>
        DeviceToken.FindInvalidField(userId, token, platform) switch
        {
            null => null,
            DeviceTokenField.UserId => NotificationErrors.InvalidUserId,
            DeviceTokenField.Token => NotificationErrors.InvalidToken,
            _ => NotificationErrors.InvalidPlatform,
        };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused to register device token {DeviceTokenId}: it belongs to another user")]
    private partial void LogTokenOwnedByAnotherUser(Guid deviceTokenId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Device registration attempt {Attempt} lost a race with a concurrent request; retrying")]
    private partial void LogRegisterConflict(int attempt);
}
