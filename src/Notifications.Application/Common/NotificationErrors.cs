namespace Notifications.Application.Common;

public static class NotificationErrors
{
    public static readonly Error EmptyNotification = Error.Validation(
        "Notifications.EmptyNotification", "A notification needs a title, a body, or data.");

    public static readonly Error NoTokens = Error.Validation(
        "Notifications.NoTokens", "At least one device token is required.");

    public static readonly Error InvalidToken = Error.Validation(
        "Notifications.InvalidToken", "The device token is malformed or no longer registered with Firebase.");

    public static readonly Error InvalidTopic = Error.Validation(
        "Notifications.InvalidTopic", "Topic names may only contain letters, digits and - _ . ~ % (max 900 characters).");

    public static readonly Error UserHasNoDevices = Error.NotFound(
        "Notifications.UserHasNoDevices", "The user has no registered devices.");

    public static readonly Error DeviceNotFound = Error.NotFound(
        "Notifications.DeviceNotFound", "The device token is not registered for this user.");

    public static readonly Error ProviderUnavailable = Error.Unavailable(
        "Notifications.ProviderUnavailable", "The push provider is temporarily unavailable or rate limited. Retry later.");

    public static readonly Error InvalidUserId = Error.Validation(
        "Notifications.InvalidUserId", "A user id is required.");

    public static readonly Error InvalidPlatform = Error.Validation(
        "Notifications.InvalidPlatform", "The device platform is not supported.");

    public static readonly Error ImageWithoutVisibleContent = Error.Validation(
        "Notifications.ImageWithoutVisibleContent", "An image is only shown with a title or a body.");

    public static readonly Error DeviceOwnedByAnotherUser = Error.Conflict(
        "Notifications.DeviceOwnedByAnotherUser",
        "This device token is registered to another user. Delete the token on the device and register the new one.");

    public static readonly Error RegistrationConflict = Error.Conflict(
        "Notifications.RegistrationConflict", "The device was changed by another request at the same time. Retry.");

    public static readonly Error InvalidImageUrl = Error.Validation(
        "Notifications.InvalidImageUrl", "The image URL must be an absolute https address (iOS blocks plain http).");

    public static readonly Error InvalidData = Error.Validation(
        "Notifications.InvalidData", "Data keys must not be empty and data values must not be null.");

    public static Error TooManyTokens(int max) => Error.Validation(
        "Notifications.TooManyTokens", $"A request may contain at most {max} device tokens. Split it into several requests.");

    /// <remarks>Identifies the token by position, never by value: tokens are not echoed back into logs or responses.</remarks>
    public static Error InvalidTokenAt(int position) => Error.Validation(
        "Notifications.InvalidToken", $"The device token at position {position} is malformed.");

    public static Error ReservedDataKey(string key) => Error.Validation(
        "Notifications.ReservedDataKey", $"The data key '{key}' is reserved by Firebase. Use a different name.");

    /// <remarks>
    /// Keeps the provider's reason (e.g. "Message is too large"): it describes the caller's own payload and is
    /// what they need to fix it. Configuration failures use the generic <see cref="ProviderRejected"/> instead.
    /// </remarks>
    public static Error InvalidRequest(string reason) => Error.Validation(
        "Notifications.InvalidRequest", $"The push provider rejected the message as invalid: {reason.TrimEnd('.')}.");

    /// <remarks>
    /// Deliberately generic: the provider's own text can contain project or credential details, so it is
    /// logged by the sender and never returned to API callers.
    /// </remarks>
    public static readonly Error ProviderRejected = Error.Failure(
        "Notifications.ProviderRejected",
        "The push provider rejected the request because of a configuration problem (credentials, APNs key). See the server logs.");
}
