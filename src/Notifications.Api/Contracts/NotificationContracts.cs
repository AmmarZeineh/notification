using System.ComponentModel.DataAnnotations;
using Notifications.Application.Models;

namespace Notifications.Api.Contracts;

public sealed record NotificationContent(
    [MaxLength(256)] string? Title,
    [MaxLength(4000)] string? Body,
    IReadOnlyDictionary<string, string>? Data,
    Uri? ImageUrl)
{
    public PushNotification ToPushNotification() => new(Title, Body, Data, ImageUrl);
}

public sealed record SendToDeviceRequest(
    [Required] string Token,
    [Required] NotificationContent Notification);

public sealed record SendToDevicesRequest(
    [Required, MinLength(1)] IReadOnlyList<string> Tokens,
    [Required] NotificationContent Notification);

public sealed record TopicSubscriptionRequest(
    [Required, MinLength(1)] IReadOnlyList<string> Tokens);

/// <param name="MessageId">The provider message id; null when <paramref name="Confirmed"/> is false.</param>
/// <param name="Confirmed">False when the provider never answered: the notification may already have been delivered.</param>
public sealed record MessageSentResponse(string? MessageId, bool Confirmed);
