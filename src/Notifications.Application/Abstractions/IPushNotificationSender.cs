using Notifications.Application.Models;

namespace Notifications.Application.Abstractions;

/// <summary>
/// Port to the push provider (Firebase Cloud Messaging in production).
/// Implementations report per-target failures in the results and throw only for unexpected faults.
/// </summary>
public interface IPushNotificationSender
{
    Task<SendResult> SendToTokenAsync(string token, PushNotification notification, CancellationToken cancellationToken);

    /// <summary>Sends to any number of tokens; the implementation handles provider batch limits.</summary>
    Task<IReadOnlyList<TokenSendResult>> SendToTokensAsync(
        IReadOnlyList<string> tokens, PushNotification notification, CancellationToken cancellationToken);

    Task<SendResult> SendToTopicAsync(string topic, PushNotification notification, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider to validate the message without delivering it. Used to tell an invalid message
    /// apart from an invalid token, because providers report both the same way.
    /// </summary>
    Task<SendResult> ValidateAsync(PushNotification notification, CancellationToken cancellationToken);

    Task<TopicSubscriptionResult> SubscribeToTopicAsync(
        IReadOnlyList<string> tokens, string topic, CancellationToken cancellationToken);

    Task<TopicSubscriptionResult> UnsubscribeFromTopicAsync(
        IReadOnlyList<string> tokens, string topic, CancellationToken cancellationToken);
}
