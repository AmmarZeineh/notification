using System.Diagnostics.CodeAnalysis;

namespace Notifications.Domain.DeviceTokens;

/// <summary>
/// An FCM registration token issued to an app installation and owned by a single user.
/// </summary>
public sealed class DeviceToken
{
    /// <summary>FCM tokens are ~160 characters today; the margin protects against format changes.</summary>
    public const int TokenMaxLength = 512;

    /// <summary>Matches the ASP.NET Core Identity user key length so the column joins cleanly.</summary>
    public const int UserIdMaxLength = 450;

    private DeviceToken()
    {
    }

    public Guid Id { get; private set; }

    public string UserId { get; private set; } = null!;

    public string Token { get; private set; } = null!;

    public DevicePlatform Platform { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public static DeviceToken Create(string userId, string token, DevicePlatform platform, DateTimeOffset now)
    {
        EnsureValid(userId, token, platform);

        return new DeviceToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Token = token,
            Platform = platform,
            CreatedAt = now,
            LastSeenAt = now,
        };
    }

    /// <summary>
    /// Called when the owner's app re-registers the token. A token never moves to another user:
    /// the app deletes its token on sign-out, so the next user on the same device gets a new one.
    /// </summary>
    public void Refresh(DevicePlatform platform, DateTimeOffset now)
    {
        EnsureValid(UserId, Token, platform);

        Platform = platform;
        LastSeenAt = now;
    }

    public bool IsOwnedBy(string userId) => string.Equals(UserId, userId, StringComparison.Ordinal);

    /// <summary>
    /// The single source of the registration rules. Callers that want a friendly error (instead of the
    /// exception thrown by <see cref="Create"/>) check this first.
    /// </summary>
    /// <returns>The first invalid field, or <see langword="null"/> when all values are valid.</returns>
    public static DeviceTokenField? FindInvalidField(string? userId, string? token, DevicePlatform platform)
    {
        if (!IsValidUserId(userId))
        {
            return DeviceTokenField.UserId;
        }

        if (!IsValidToken(token))
        {
            return DeviceTokenField.Token;
        }

        return Enum.IsDefined(platform) ? null : DeviceTokenField.Platform;
    }

    public static bool IsValidUserId([NotNullWhen(true)] string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && userId.Length <= UserIdMaxLength;

    /// <summary>
    /// Length plus a basic format check that keeps obvious junk (spaces, line breaks, non-ASCII) out.
    /// It deliberately does not mirror FCM's undocumented format, so a future token format still works.
    /// Also used before sending, so an obviously bad token is rejected without calling the provider.
    /// </summary>
    public static bool IsValidToken([NotNullWhen(true)] string? token) =>
        !string.IsNullOrEmpty(token) && token.Length <= TokenMaxLength && token.All(c => c is > ' ' and <= '~');

    private static void EnsureValid(string userId, string token, DevicePlatform platform)
    {
        if (FindInvalidField(userId, token, platform) is { } field)
        {
            throw new ArgumentException($"Invalid device token {field}.", field switch
            {
                DeviceTokenField.UserId => nameof(userId),
                DeviceTokenField.Token => nameof(token),
                _ => nameof(platform),
            });
        }
    }
}
