namespace Notifications.Application.Models;

/// <summary>Outcome of sending to one target (a token or a topic).</summary>
public sealed record SendResult(string? MessageId, DeliveryFailure? Failure, string? FailureReason)
{
    public bool IsSuccess => Failure is null;

    public static SendResult Delivered(string messageId) => new(messageId, null, null);

    public static SendResult Failed(DeliveryFailure failure, string reason) => new(null, failure, reason);
}

public sealed record TokenSendResult(string Token, SendResult Result);

/// <summary>Outcome of a successful single-target send (one device or one topic).</summary>
/// <param name="MessageId">The provider message id; null when the provider never answered.</param>
public sealed record SendReceipt(string? MessageId)
{
    /// <summary>
    /// False when the provider did not answer (timeout, dropped connection): the notification may already
    /// have been delivered. This is deliberately not an error, so callers are never nudged into a retry
    /// that could send a duplicate.
    /// </summary>
    public bool IsConfirmed => MessageId is not null;

    public static SendReceipt Confirmed(string messageId) => new(messageId);

    public static readonly SendReceipt Unconfirmed = new((string?)null);
}

/// <summary>Summary of a multi-device send returned to callers.</summary>
public sealed record BatchSendSummary(int SuccessCount, int FailureCount, int RemovedInvalidTokenCount)
{
    /// <summary>
    /// Tokens that failed for a temporary reason (outage, quota, network). Resend to these only;
    /// every other token was either delivered or will not succeed on a retry.
    /// </summary>
    public IReadOnlyList<string> RetryableTokens { get; init; } = [];

    /// <summary>
    /// Tokens the provider did not answer for (timeout, dropped connection): the notification may already
    /// have arrived. Resend to these only if a duplicate is acceptable.
    /// </summary>
    public IReadOnlyList<string> UnconfirmedTokens { get; init; } = [];

    /// <summary>Compares the tokens by value; a record would compare the list by reference.</summary>
    public bool Equals(BatchSendSummary? other) =>
        other is not null &&
        SuccessCount == other.SuccessCount &&
        FailureCount == other.FailureCount &&
        RemovedInvalidTokenCount == other.RemovedInvalidTokenCount &&
        RetryableTokens.SequenceEqual(other.RetryableTokens) &&
        UnconfirmedTokens.SequenceEqual(other.UnconfirmedTokens);

    public override int GetHashCode() =>
        HashCode.Combine(SuccessCount, FailureCount, RemovedInvalidTokenCount, RetryableTokens.Count, UnconfirmedTokens.Count);
}

/// <param name="Token">The token that could not be subscribed or unsubscribed.</param>
/// <param name="Reason">The provider's reason, for diagnostics.</param>
/// <param name="Failure">The classified reason; <see cref="DeliveryFailure.InvalidToken"/> tokens are removed from storage.</param>
public sealed record TopicSubscriptionFailure(string Token, string Reason, DeliveryFailure Failure);

/// <param name="SuccessCount">Tokens whose subscription changed.</param>
/// <param name="Failures">Tokens that were not changed, with the reason. Includes unprocessed tokens.</param>
/// <param name="ProviderFailure">
/// Set when the provider failed a whole batch (outage, quota), so some tokens were never processed.
/// </param>
/// <param name="UnprocessedCount">
/// Tokens left unchanged because of <paramref name="ProviderFailure"/>: those in the batch the provider failed
/// as a whole, plus those in batches skipped after it. Equal to the request size when nothing was applied.
/// </param>
public sealed record TopicSubscriptionResult(
    int SuccessCount,
    IReadOnlyList<TopicSubscriptionFailure> Failures,
    DeliveryFailure? ProviderFailure = null,
    int UnprocessedCount = 0)
{
    public int FailureCount => Failures.Count;

    /// <summary>Invalid tokens deleted from storage after the provider reported them.</summary>
    public int RemovedInvalidTokenCount { get; init; }
}
