using Microsoft.Extensions.Logging;
using Notifications.Application.Abstractions;
using Notifications.Application.Common;
using Notifications.Application.Models;
using Notifications.Domain.DeviceTokens;

namespace Notifications.Application.Services;

internal sealed partial class PushNotificationService(
    IPushNotificationSender sender,
    IDeviceTokenRepository deviceTokens,
    ILogger<PushNotificationService> logger) : IPushNotificationService
{
    /// <summary>
    /// Upper bound for one multi-device or topic request. Larger audiences belong in a topic, or in several
    /// requests: once sending starts it always completes, so an unbounded request could run for minutes.
    /// </summary>
    public const int MaxTokensPerRequest = 10_000;

    public async Task<Result<SendReceipt>> SendToDeviceAsync(
        string token, PushNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (PushNotificationValidator.ValidateContent(notification) is { } contentError)
        {
            return contentError;
        }

        if (PushNotificationValidator.NormalizeToken(token, out token) is { } tokenError)
        {
            return tokenError;
        }

        var result = await sender.SendToTokenAsync(token, notification, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return SendReceipt.Confirmed(result.MessageId!);
        }

        if (result.Failure == DeliveryFailure.Unknown)
        {
            return SendReceipt.Unconfirmed;
        }

        var outcome = await ProcessResultsAsync([new TokenSendResult(token, result)], notification).ConfigureAwait(false);

        // With no other error, the only remaining cause is the token itself (removed if it was unregistered).
        return outcome.Error ?? NotificationErrors.InvalidToken;
    }

    public async Task<Result<BatchSendSummary>> SendToDevicesAsync(
        IReadOnlyCollection<string> tokens, PushNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(notification);

        if (PushNotificationValidator.ValidateContent(notification) is { } contentError)
        {
            return contentError;
        }

        if (PushNotificationValidator.NormalizeTokens(tokens, MaxTokensPerRequest, out var distinctTokens) is { } tokenError)
        {
            return tokenError;
        }

        return await SendToTokensCoreAsync(distinctTokens, notification, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<BatchSendSummary>> SendToUserAsync(
        string userId, PushNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!DeviceToken.IsValidUserId(userId))
        {
            return NotificationErrors.InvalidUserId;
        }

        if (PushNotificationValidator.ValidateContent(notification) is { } contentError)
        {
            return contentError;
        }

        // Real users have a handful of devices; the cap only keeps one request bounded.
        var tokens = await deviceTokens
            .GetTokensForUserAsync(userId, MaxTokensPerRequest, cancellationToken)
            .ConfigureAwait(false);
        if (tokens.Count == 0)
        {
            return NotificationErrors.UserHasNoDevices;
        }

        return await SendToTokensCoreAsync(tokens, notification, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SendReceipt>> SendToTopicAsync(
        string topic, PushNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!TopicName.IsValid(topic))
        {
            return NotificationErrors.InvalidTopic;
        }

        if (PushNotificationValidator.ValidateContent(notification) is { } contentError)
        {
            return contentError;
        }

        var result = await sender.SendToTopicAsync(topic, notification, cancellationToken).ConfigureAwait(false);
        if (result.Failure == DeliveryFailure.Unknown)
        {
            return SendReceipt.Unconfirmed;
        }

        if (!result.IsSuccess)
        {
            return DeliveryErrorPolicy.ForFailure(result.Failure, result.FailureReason);
        }

        LogTopicSent(topic, result.MessageId!);
        return SendReceipt.Confirmed(result.MessageId!);
    }

    public Task<Result<TopicSubscriptionResult>> SubscribeToTopicAsync(
        IReadOnlyCollection<string> tokens, string topic, CancellationToken cancellationToken = default) =>
        ChangeSubscriptionAsync(tokens, topic, subscribe: true, cancellationToken);

    public Task<Result<TopicSubscriptionResult>> UnsubscribeFromTopicAsync(
        IReadOnlyCollection<string> tokens, string topic, CancellationToken cancellationToken = default) =>
        ChangeSubscriptionAsync(tokens, topic, subscribe: false, cancellationToken);

    private async Task<Result<TopicSubscriptionResult>> ChangeSubscriptionAsync(
        IReadOnlyCollection<string> tokens, string topic, bool subscribe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (!TopicName.IsValid(topic))
        {
            return NotificationErrors.InvalidTopic;
        }

        if (PushNotificationValidator.NormalizeTokens(tokens, MaxTokensPerRequest, out var distinctTokens) is { } tokenError)
        {
            return tokenError;
        }

        var result = subscribe
            ? await sender.SubscribeToTopicAsync(distinctTokens, topic, cancellationToken).ConfigureAwait(false)
            : await sender.UnsubscribeFromTopicAsync(distinctTokens, topic, cancellationToken).ConfigureAwait(false);

        LogSubscriptionChanged(subscribe ? "Subscribed" : "Unsubscribed", topic, result.SuccessCount, result.FailureCount);

        var invalidTokens = result.Failures
            .Where(f => f.Failure == DeliveryFailure.InvalidToken)
            .Select(f => f.Token)
            .ToList();
        var removed = await RemoveInvalidTokensAsync(invalidTokens).ConfigureAwait(false);

        // The provider failed before processing a single token, so there is no partial result to report.
        // Otherwise the per-token results are returned, including the tokens that were never processed.
        if (result.ProviderFailure is { } providerFailure && result.UnprocessedCount == distinctTokens.Count)
        {
            return DeliveryErrorPolicy.ForFailure(providerFailure, result.Failures.Count > 0 ? result.Failures[0].Reason : null);
        }

        return result with { RemovedInvalidTokenCount = removed };
    }

    private async Task<Result<BatchSendSummary>> SendToTokensCoreAsync(
        IReadOnlyList<string> tokens, PushNotification notification, CancellationToken cancellationToken)
    {
        var results = await sender.SendToTokensAsync(tokens, notification, cancellationToken).ConfigureAwait(false);
        var outcome = await ProcessResultsAsync(results, notification).ConfigureAwait(false);
        var failureCount = results.Count - outcome.SuccessCount;

        LogBatchSent(tokens.Count, outcome.SuccessCount, failureCount);

        if (outcome.Error is { } error)
        {
            return error;
        }

        return new BatchSendSummary(outcome.SuccessCount, failureCount, outcome.RemovedCount)
        {
            RetryableTokens = TokensThatFailedWith(results, DeliveryFailure.Unavailable),
            UnconfirmedTokens = TokensThatFailedWith(results, DeliveryFailure.Unknown),
        };
    }

    /// <summary>
    /// Shared by single and multi-device sends: removes the tokens the provider reported as unregistered and,
    /// when nothing was delivered, picks the error to return (see <see cref="DeliveryErrorPolicy.WhenNothingDelivered"/>).
    /// </summary>
    /// <remarks>
    /// Only an explicit "unregistered" deletes a token. Providers report a malformed token and an invalid
    /// message the same way, so those tokens are kept: a stale token costs one wasted send, a wrongly deleted
    /// one silently loses a user's device. The dry run only tells the caller whether the message must change.
    /// </remarks>
    private async Task<DeliveryOutcome> ProcessResultsAsync(IReadOnlyList<TokenSendResult> results, PushNotification notification)
    {
        var removed = await RemoveInvalidTokensAsync(TokensThatFailedWith(results, DeliveryFailure.InvalidToken)).ConfigureAwait(false);
        var successCount = results.Count(r => r.Result.IsSuccess);

        // When anything was, or may have been, delivered, the summary is always returned: an error would
        // imply that a retry is safe.
        if (successCount > 0 || results.Any(r => r.Result.Failure == DeliveryFailure.Unknown))
        {
            return new DeliveryOutcome(successCount, removed, null);
        }

        var validation = results.Any(r => r.Result.Failure == DeliveryFailure.InvalidRequest)
            ? await ValidateMessageAsync(notification).ConfigureAwait(false)
            : null;
        return new DeliveryOutcome(0, removed, DeliveryErrorPolicy.WhenNothingDelivered(results, removed, validation));
    }

    /// <remarks>
    /// Runs after the send, so the caller's cancellation is not used. Provider and network failures come back
    /// as a result; anything thrown is a real fault (e.g. broken credentials) and is left to surface. It only
    /// runs when nothing was, or may have been, delivered, so no outcome is lost.
    /// </remarks>
    private Task<SendResult> ValidateMessageAsync(PushNotification notification) =>
        sender.ValidateAsync(notification, CancellationToken.None);

    /// <remarks>
    /// Runs after the provider has already delivered the notification, so neither a storage failure nor
    /// the caller disconnecting may fail the request: callers would retry and send duplicates. Failures
    /// are logged and the stale tokens are removed on a later send; the request's cancellation is ignored.
    /// </remarks>
    private async Task<int> RemoveInvalidTokensAsync(List<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        try
        {
            var removed = await deviceTokens.RemoveTokensAsync(tokens, CancellationToken.None).ConfigureAwait(false);
            if (removed > 0)
            {
                LogInvalidTokensRemoved(removed);
            }

            return removed;
        }
        catch (Exception ex)
        {
            LogInvalidTokenRemovalFailed(ex, tokens.Count);
            return 0;
        }
    }

    private static List<string> TokensThatFailedWith(IReadOnlyList<TokenSendResult> results, DeliveryFailure failure) =>
        results.Where(r => r.Result.Failure == failure).Select(r => r.Token).ToList();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent push notification to topic {Topic} ({MessageId})")]
    private partial void LogTopicSent(string topic, string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent push notification to {TokenCount} device(s): {SuccessCount} succeeded, {FailureCount} failed")]
    private partial void LogBatchSent(int tokenCount, int successCount, int failureCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} invalid device token(s)")]
    private partial void LogInvalidTokensRemoved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove {Count} invalid device token(s); they will be removed on a later send")]
    private partial void LogInvalidTokenRemovalFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Action} topic {Topic}: {SuccessCount} succeeded, {FailureCount} failed")]
    private partial void LogSubscriptionChanged(string action, string topic, int successCount, int failureCount);

    private sealed record DeliveryOutcome(int SuccessCount, int RemovedCount, Error? Error);
}
