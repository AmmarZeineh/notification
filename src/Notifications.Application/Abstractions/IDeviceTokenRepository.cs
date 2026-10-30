using Notifications.Domain.DeviceTokens;

namespace Notifications.Application.Abstractions;

public interface IDeviceTokenRepository
{
    Task<DeviceToken?> GetByTokenAsync(string token, CancellationToken cancellationToken);

    /// <summary>The user's tokens, most recently seen first, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<string>> GetTokensForUserAsync(string userId, int limit, CancellationToken cancellationToken);

    void Add(DeviceToken deviceToken);

    /// <summary>
    /// Deletes the token immediately, but only while <paramref name="userId"/> owns it, so a concurrent
    /// re-registration by another user on the same device is never deleted. Returns whether a row was removed.
    /// </summary>
    Task<bool> RemoveUserTokenAsync(string userId, string token, CancellationToken cancellationToken);

    /// <summary>Deletes the given tokens immediately (no <see cref="SaveChangesAsync"/> needed). Returns the number removed.</summary>
    Task<int> RemoveTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes tokens whose app has not re-registered since <paramref name="cutoff"/> (no <see cref="SaveChangesAsync"/>
    /// needed). Returns the number removed.
    /// </summary>
    Task<int> RemoveTokensLastSeenBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <exception cref="DeviceTokenConflictException">A concurrent request changed the same token first.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
