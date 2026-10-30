using Notifications.Application.Common;
using Notifications.Application.Models;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Application.Services;

/// <summary>Checks send requests before anything reaches the provider.</summary>
internal static class PushNotificationValidator
{
    public static Error? ValidateContent(PushNotification notification)
    {
        if (notification.IsEmpty)
        {
            return NotificationErrors.EmptyNotification;
        }

        if (!notification.HasValidImageUrl)
        {
            return NotificationErrors.InvalidImageUrl;
        }

        if (!notification.HasValidData)
        {
            return NotificationErrors.InvalidData;
        }

        // A data-only message has no visible notification to attach the image to; it would be dropped silently.
        if (notification.ImageUrl is not null && !notification.HasVisibleContent)
        {
            return NotificationErrors.ImageWithoutVisibleContent;
        }

        return notification.FindReservedDataKey() is { } key ? NotificationErrors.ReservedDataKey(key) : null;
    }

    /// <summary>Trims the token and rejects it when it is blank or malformed.</summary>
    public static Error? NormalizeToken(string? raw, out string token)
    {
        token = raw?.Trim() ?? string.Empty;
        return DeviceToken.IsValidToken(token) ? null : NotificationErrors.InvalidToken;
    }

    /// <summary>Trims and de-duplicates the tokens, ignoring blank entries; rejects malformed ones and oversized requests.</summary>
    public static Error? NormalizeTokens(IReadOnlyCollection<string> tokens, int maxTokens, out List<string> normalized)
    {
        normalized = [];
        if (tokens.Count > maxTokens)
        {
            return NotificationErrors.TooManyTokens(maxTokens);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var raw in tokens)
        {
            position++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var token = raw.Trim();
            if (!DeviceToken.IsValidToken(token))
            {
                return NotificationErrors.InvalidTokenAt(position);
            }

            if (seen.Add(token))
            {
                normalized.Add(token);
            }
        }

        return normalized.Count == 0 ? NotificationErrors.NoTokens : null;
    }
}
