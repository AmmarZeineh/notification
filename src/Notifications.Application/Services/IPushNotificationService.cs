using Notifications.Application.Common;
using Notifications.Application.Models;

namespace Notifications.Application.Services;

/// <summary>
/// Entry point for sending push notifications. Tokens that Firebase reports as invalid
/// (app uninstalled, token rotated) are removed from storage automatically.
/// </summary>
public interface IPushNotificationService
{
    /// <returns>The provider message id, or an unconfirmed receipt when the provider never answered.</returns>
    Task<Result<SendReceipt>> SendToDeviceAsync(
        string token, PushNotification notification, CancellationToken cancellationToken = default);

    Task<Result<BatchSendSummary>> SendToDevicesAsync(
        IReadOnlyCollection<string> tokens, PushNotification notification, CancellationToken cancellationToken = default);

    /// <summary>Sends to every device the user has registered.</summary>
    Task<Result<BatchSendSummary>> SendToUserAsync(
        string userId, PushNotification notification, CancellationToken cancellationToken = default);

    /// <returns>The provider message id, or an unconfirmed receipt when the provider never answered.</returns>
    Task<Result<SendReceipt>> SendToTopicAsync(
        string topic, PushNotification notification, CancellationToken cancellationToken = default);

    Task<Result<TopicSubscriptionResult>> SubscribeToTopicAsync(
        IReadOnlyCollection<string> tokens, string topic, CancellationToken cancellationToken = default);

    Task<Result<TopicSubscriptionResult>> UnsubscribeFromTopicAsync(
        IReadOnlyCollection<string> tokens, string topic, CancellationToken cancellationToken = default);
}
