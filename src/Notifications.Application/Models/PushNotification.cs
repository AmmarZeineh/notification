namespace Notifications.Application.Models;

/// <summary>
/// Platform-neutral notification content. <see cref="Data"/> is delivered to the app as key/value
/// pairs (e.g. a screen to open) and is available even when the visible notification is not tapped.
/// </summary>
public sealed record PushNotification(
    string? Title,
    string? Body,
    IReadOnlyDictionary<string, string>? Data = null,
    Uri? ImageUrl = null)
{
    private static readonly string[] ReservedDataKeys = ["from", "notification", "message_type"];
    // FCM reserves every key that starts with these words, not only "google." and "gcm.".
    private static readonly string[] ReservedDataKeyPrefixes = ["google", "gcm"];

    public bool IsEmpty => !HasVisibleContent && (Data is null || Data.Count == 0);

    /// <summary>False for a data-only (silent) message.</summary>
    public bool HasVisibleContent => !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Body);

    /// <summary>
    /// Push providers can only download images from an absolute address, and iOS blocks plain http
    /// by default (App Transport Security), so only https works on both platforms.
    /// </summary>
    public bool HasValidImageUrl =>
        ImageUrl is null || (ImageUrl.IsAbsoluteUri && ImageUrl.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// False when a key is empty or a value is null. JSON input can carry nulls despite the non-nullable
    /// type, and the provider rejects them.
    /// </summary>
    public bool HasValidData => Data is null || Data.All(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null);

    /// <summary>Returns the first data key FCM reserves for itself, or <see langword="null"/>.</summary>
    public string? FindReservedDataKey() =>
        Data?.Keys.FirstOrDefault(key =>
            ReservedDataKeys.Contains(key, StringComparer.OrdinalIgnoreCase) ||
            ReservedDataKeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
}
