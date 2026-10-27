using System.Diagnostics;
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

        if (ValidateContent(notification) is { } contentError)
        {
            return contentError;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return NotificationErrors.NoTokens;
        }

        token = token.Trim();
        if (!DeviceToken.IsValidToken(token))
        {
            return NotificationErrors.InvalidToken;
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

        if (ValidateContent(notification) is { } contentError)
        {
            return contentError;
        }

        if (NormalizeTokens(tokens, out var distinctTokens) is { } tokenError)
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

        if (ValidateContent(notification) is { } contentError)
        {
            return contentError;
        }

        // One more than the cap, only to detect (and log) a user with more devices than one send allows.
        var tokens = await deviceTokens
            .GetTokensForUserAsync(userId, MaxTokensPerRequest + 1, cancellationToken)
            .ConfigureAwait(false);
        if (tokens.Count > MaxTokensPerRequest)
        {
            LogUserDevicesTruncated(MaxTokensPerRequest);
            tokens = tokens.Take(MaxTokensPerRequest).ToList();
        }
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

        if (ValidateContent(notification) is { } contentError)
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
            return ToError(result.Failure, result.FailureReason);
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

        if (NormalizeTokens(tokens, out var distinctTokens) is { } tokenError)
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
            return ToError(providerFailure, result.Failures.Count > 0 ? result.Failures[0].Reason : null);
        }

        return result with { RemovedInvalidTokenCount = removed };
    }

    private async Task<Result<BatchSendSummary>> SendToTokensCoreAsync(
        IReadOnlyList<string> tokens, PushNotification notification, CancellationToken cancellationToken)
    {
        var results = await sender.SendToTokensAsync(tokens, notification, cancellationToken).ConfigureAwait(false);
        var outcome = await ProcessResultsAsync(results, notification).ConfigureAwait(false);

        LogBatchSent(tokens.Count, outcome.SuccessCount, results.Count - outcome.SuccessCount);

        if (outcome.Error is { } error)
        {
            return error;
        }

        return new BatchSendSummary(outcome.SuccessCount, results.Count - outcome.SuccessCount, outcome.RemovedCount)
        {
            RetryableTokens = TokensThatFailedWith(results, DeliveryFailure.Unavailable),
            UnconfirmedTokens = TokensThatFailedWith(results, DeliveryFailure.Unknown),
        };
    }

    /// <summary>
    /// Shared by single and multi-device sends: removes the tokens the provider reported as unregistered and,
    /// when nothing was delivered, picks the error to return (null when the tokens themselves are at fault).
    /// </summary>
    /// <remarks>
    /// Only an explicit "unregistered" deletes a token. Providers report a malformed token and an invalid
    /// message the same way, and no check proves which one it was for every platform, so those tokens are
    /// kept: a stale token costs one wasted send, a wrongly deleted one silently loses a user's device.
    /// The dry-run validation is used only to tell the caller whether the message must change.
    /// Runs after the send, so the caller's cancellation is not used (see <see cref="RemoveInvalidTokensAsync"/>).
    /// </remarks>
    private async Task<DeliveryOutcome> ProcessResultsAsync(IReadOnlyList<TokenSendResult> results, PushNotification notification)
    {
        var successCount = results.Count(r => r.Result.IsSuccess);

        var invalidTokens = results
            .Where(r => r.Result.Failure == DeliveryFailure.InvalidToken)
            .Select(r => r.Token)
            .ToList();
        var removed = await RemoveInvalidTokensAsync(invalidTokens).ConfigureAwait(false);

        // The one rule for possibly-delivered tokens: when any outcome is unknown, the summary is always
        // returned (never an error that implies a retry is safe, never an exception that loses the results).
        // So validation is skipped too: its only purpose is to choose an error.
        if (successCount > 0 || results.Any(r => r.Result.Failure == DeliveryFailure.Unknown))
        {
            return new DeliveryOutcome(successCount, removed, null);
        }

        var invalidRequest = results.FirstOrDefault(r => r.Result.Failure == DeliveryFailure.InvalidRequest);
        var validation = invalidRequest is null ? null : await ValidateMessageAsync(notification).ConfigureAwait(false);
        return new DeliveryOutcome(successCount, removed, ToNoDeliveryError(results, removed, invalidRequest, validation));
    }

    /// <remarks>
    /// An error replaces the summary, so one is returned only when the summary would tell the caller nothing
    /// actionable: every token failed temporarily (retry everything), the message must change, or a
    /// configuration problem hit every device. Otherwise the summary carries what to retry and what was removed.
    /// </remarks>
    private static Error? ToNoDeliveryError(
        IReadOnlyList<TokenSendResult> results, int removed, TokenSendResult? invalidRequest, SendResult? validation)
    {
        // Only called when nothing was delivered and no outcome is unknown, so a retry can never duplicate.
        // Nothing went out because the provider is down or throttling (possibly only while validating; a dry
        // run delivers nothing, so one that got no answer is simply an outage): retry later.
        if (validation?.Failure is DeliveryFailure.Unavailable or DeliveryFailure.Unknown ||
            results.All(r => r.Result.Failure == DeliveryFailure.Unavailable))
        {
            return NotificationErrors.ProviderUnavailable;
        }

        if (invalidRequest is not null && validation is { IsSuccess: false })
        {
            // The provider rejected the message on its own too, so the message is what must change.
            return ToError(validation.Failure, validation.FailureReason ?? invalidRequest.Result.FailureReason);
        }

        // A configuration problem (credentials, APNs key) that callers must not mistake for a successful send,
        // unless some tokens can still be retried or were removed: then the summary is what the caller needs.
        var hasRetryable = results.Any(r => r.Result.Failure == DeliveryFailure.Unavailable);
        if (!hasRetryable && removed == 0 && results.Any(r => r.Result.Failure == DeliveryFailure.Rejected))
        {
            return NotificationErrors.ProviderRejected;
        }

        return null;
    }

    /// <remarks>
    /// Provider and network failures come back as a result. Anything thrown is a real fault (e.g. broken
    /// credentials) and is left to surface: it only runs when nothing was delivered or possibly delivered,
    /// so no outcome is lost.
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

    private static Error? ValidateContent(PushNotification notification)
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

    /// <summary>Trims and de-duplicates the tokens, ignoring blank entries; rejects malformed ones and oversized requests.</summary>
    private static Error? NormalizeTokens(IReadOnlyCollection<string> tokens, out List<string> normalized)
    {
        normalized = [];
        if (tokens.Count > MaxTokensPerRequest)
        {
            return NotificationErrors.TooManyTokens(MaxTokensPerRequest);
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

    private static Error ToError(DeliveryFailure? failure, string? reason) => failure switch
    {
        DeliveryFailure.InvalidToken => NotificationErrors.InvalidToken,
        DeliveryFailure.Unavailable => NotificationErrors.ProviderUnavailable,
        // Unknown outcomes are reported as unconfirmed results, never as an error (see ProcessResultsAsync).
        DeliveryFailure.Unknown => throw new UnreachableException("An unknown delivery outcome must not become an error."),
        DeliveryFailure.InvalidRequest => NotificationErrors.InvalidRequest(reason ?? "unknown"),
        _ => NotificationErrors.ProviderRejected,
    };

    private static List<string> TokensThatFailedWith(IReadOnlyList<TokenSendResult> results, DeliveryFailure failure) =>
        results.Where(r => r.Result.Failure == failure).Select(r => r.Token).ToList();

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent push notification to topic {Topic} ({MessageId})")]
    private partial void LogTopicSent(string topic, string messageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent push notification to {TokenCount} device(s): {SuccessCount} succeeded, {FailureCount} failed")]
    private partial void LogBatchSent(int tokenCount, int successCount, int failureCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "User has more devices than one send allows; sent to the {Limit} most recently seen")]
    private partial void LogUserDevicesTruncated(int limit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} invalid device token(s)")]
    private partial void LogInvalidTokensRemoved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not remove {Count} invalid device token(s); they will be removed on a later send")]
    private partial void LogInvalidTokenRemovalFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Action} topic {Topic}: {SuccessCount} succeeded, {FailureCount} failed")]
    private partial void LogSubscriptionChanged(string action, string topic, int successCount, int failureCount);

    private sealed record DeliveryOutcome(int SuccessCount, int RemovedCount, Error? Error);
}
