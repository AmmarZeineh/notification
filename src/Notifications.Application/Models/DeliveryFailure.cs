namespace Notifications.Application.Models;

/// <summary>Provider-neutral reasons a single delivery can fail.</summary>
public enum DeliveryFailure
{
    /// <summary>The token is malformed or the app was uninstalled. The token should be deleted.</summary>
    InvalidToken = 1,

    /// <summary>Rate limited or the provider is down. Safe to retry later.</summary>
    Unavailable = 2,

    /// <summary>The provider refused the request for a configuration reason (credentials, APNs setup). Retrying will not help.</summary>
    Rejected = 3,

    /// <summary>The message itself is invalid (e.g. over the 4 KB payload limit). The caller must change it.</summary>
    InvalidRequest = 4,

    /// <summary>
    /// No answer (timeout, dropped connection): the provider may already have delivered the message.
    /// Resending risks a duplicate, so these tokens are never reported as safe to retry.
    /// </summary>
    Unknown = 5,
}
