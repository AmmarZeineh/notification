using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Notifications.Api.Contracts;
using Notifications.Api.Extensions;
using Notifications.Application.Models;
using Notifications.Application.Services;

namespace Notifications.Api.Controllers;

/// <summary>
/// Sending endpoints for back-office use. Most notifications are sent from application code through
/// <see cref="IPushNotificationService"/>; these endpoints exist for admin tools and manual testing.
/// </summary>
[ApiController]
[Authorize(Policy = NotificationPolicies.SendNotifications)]
[Route("api/notifications")]
public sealed class NotificationsController(IPushNotificationService notifications) : ControllerBase
{
    [HttpPost("device")]
    [ProducesResponseType<MessageSentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MessageSentResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SendToDevice(SendToDeviceRequest request, CancellationToken cancellationToken)
    {
        var result = await notifications.SendToDeviceAsync(
            request.Token, request.Notification.ToPushNotification(), cancellationToken);

        return result.IsSuccess ? ToResponse(result.Value) : this.ToProblem(result.Error);
    }

    [HttpPost("devices")]
    [ProducesResponseType<BatchSendSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<BatchSendSummary>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SendToDevices(SendToDevicesRequest request, CancellationToken cancellationToken)
    {
        var result = await notifications.SendToDevicesAsync(
            request.Tokens, request.Notification.ToPushNotification(), cancellationToken);

        return result.IsSuccess ? ToResponse(result.Value) : this.ToProblem(result.Error);
    }

    [HttpPost("users/{userId}")]
    [ProducesResponseType<BatchSendSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<BatchSendSummary>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SendToUser(
        string userId, NotificationContent notification, CancellationToken cancellationToken)
    {
        var result = await notifications.SendToUserAsync(userId, notification.ToPushNotification(), cancellationToken);
        return result.IsSuccess ? ToResponse(result.Value) : this.ToProblem(result.Error);
    }

    [HttpPost("topics/{topic}")]
    [ProducesResponseType<MessageSentResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MessageSentResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SendToTopic(
        string topic, NotificationContent notification, CancellationToken cancellationToken)
    {
        var result = await notifications.SendToTopicAsync(topic, notification.ToPushNotification(), cancellationToken);
        return result.IsSuccess ? ToResponse(result.Value) : this.ToProblem(result.Error);
    }

    [HttpPost("topics/{topic}/subscribe")]
    [ProducesResponseType<TopicSubscriptionResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Subscribe(
        string topic, TopicSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var result = await notifications.SubscribeToTopicAsync(request.Tokens, topic, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    [HttpPost("topics/{topic}/unsubscribe")]
    [ProducesResponseType<TopicSubscriptionResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Unsubscribe(
        string topic, TopicSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var result = await notifications.UnsubscribeFromTopicAsync(request.Tokens, topic, cancellationToken);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error);
    }

    // 202 Accepted when the provider did not confirm every delivery: the request was taken, but some
    // notifications may or may not have arrived. A 2xx keeps HTTP retry policies (which retry 5xx) from
    // resending automatically and causing duplicates; the body says what is unconfirmed.
    private IActionResult ToResponse(SendReceipt receipt) =>
        receipt.IsConfirmed
            ? Ok(new MessageSentResponse(receipt.MessageId!, Confirmed: true))
            : Accepted(new MessageSentResponse(null, Confirmed: false));

    private IActionResult ToResponse(BatchSendSummary summary) =>
        summary.UnconfirmedTokens.Count == 0 ? Ok(summary) : Accepted(summary);
}
